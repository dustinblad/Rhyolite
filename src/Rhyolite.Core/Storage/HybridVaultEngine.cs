using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rhyolite.Core.Canvas;
using Rhyolite.Core.Expansions;
using Rhyolite.Core.Graph;
using Rhyolite.Core.Models;
using Rhyolite.Core.Parsing;

namespace Rhyolite.Core.Storage;

/// <summary>
/// 3-Tier Hybrid Storage Engine:
/// Tier 1: Lock-free In-Memory ConcurrentDictionary + Incremental Single-File AST Delta (&lt; 0.5 ms).
/// Tier 2: Plain .md &amp; .canvas Files on Disk (Canonical Source of Truth, ~0.15 ms atomic flush).
/// Tier 3: SQLite WAL Sidecar Cache (.rhyolite/cache.db) for &lt; 50 ms cold-boot indexing, FTS, and Snapshots.
/// </summary>
public sealed class HybridVaultEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, VaultNote> _notesByRelativePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<NoteSnapshot>> _snapshotsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _sqliteDbPath;
    private readonly string _canvasFilePath;
    private long _mutationVersion;

    public string VaultRootPath { get; }
    public GraphTopologyMetrics LatestMetrics { get; private set; } = new(0, 0, 0, 0, 1.0, "None", 0.0);
    public double LastColdBootMilliseconds { get; private set; }
    public long MutationVersion => Interlocked.Read(ref _mutationVersion);

    public HybridVaultEngine(string vaultRootPath)
    {
        VaultRootPath = Path.GetFullPath(vaultRootPath);
        Directory.CreateDirectory(VaultRootPath);

        var rhyoliteMetaDir = Path.Combine(VaultRootPath, ".rhyolite");
        Directory.CreateDirectory(rhyoliteMetaDir);

        _sqliteDbPath = Path.Combine(rhyoliteMetaDir, "cache.db");
        _canvasFilePath = Path.Combine(VaultRootPath, "System Map.canvas");

        InitializeSqliteWalSidecar();
    }

    public IReadOnlyCollection<VaultNote> Notes =>
        _notesByRelativePath.Values
            .OrderBy(n => n.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void InitializeSqliteWalSidecar()
    {
        EnsureSqliteSchema();
    }

    public Task InitializeSqliteSidecarAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureSqliteSchema();
        return Task.CompletedTask;
    }

    public void EnsureSqliteSchema()
    {
        using var conn = OpenConnection();

        // Migrate legacy note_snapshots table if it was created before the (path, content) schema upgrade
        bool hasLegacyNoteSnapshots = false;
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "PRAGMA table_info(note_snapshots);";
            using var reader = checkCmd.ExecuteReader();
            bool hasTable = false;
            bool hasPathColumn = false;
            while (reader.Read())
            {
                hasTable = true;
                if (string.Equals(reader.GetString(1), "path", StringComparison.OrdinalIgnoreCase))
                {
                    hasPathColumn = true;
                }
            }
            if (hasTable && !hasPathColumn)
            {
                hasLegacyNoteSnapshots = true;
            }
        }

        if (hasLegacyNoteSnapshots)
        {
            using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = "DROP TABLE IF EXISTS note_snapshots;";
            dropCmd.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS note_cache (
                relative_path TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                folder_path TEXT NOT NULL,
                content_sha256 TEXT NOT NULL,
                last_write_utc TEXT NOT NULL,
                word_count INTEGER NOT NULL,
                properties_json TEXT NOT NULL,
                tags_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS flashcard_reviews (
                card_id TEXT PRIMARY KEY,
                stability_days REAL NOT NULL,
                difficulty REAL NOT NULL,
                reps INTEGER NOT NULL,
                next_review_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS note_snapshots (
                snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                content TEXT NOT NULL,
                label TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_note_snapshots_path ON note_snapshots(path, snapshot_id DESC);

            CREATE TABLE IF NOT EXISTS github_pr_cache (
                cache_key TEXT PRIMARY KEY,
                repo TEXT NOT NULL,
                pr_number INTEGER NOT NULL,
                head_sha TEXT NOT NULL,
                etag TEXT NOT NULL,
                analysis_markdown TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS external_sync_bindings (
                binding_key TEXT PRIMARY KEY,
                connector_kind TEXT NOT NULL,
                workspace_or_space_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                last_synced_sha256 TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ui_preferences (
                pref_key TEXT PRIMARY KEY,
                pref_value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS folder_metadata (
                folder_path TEXT PRIMARY KEY,
                icon_emoji TEXT NOT NULL,
                color_hex TEXT NOT NULL,
                is_expanded INTEGER NOT NULL DEFAULT 1,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS capacities_object_types (
                type_id TEXT PRIMARY KEY,
                singular_name TEXT NOT NULL,
                plural_name TEXT NOT NULL,
                icon_emoji TEXT NOT NULL,
                accent_color_hex TEXT NOT NULL,
                default_folder TEXT NOT NULL,
                schema_props_csv TEXT NOT NULL,
                description TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        SeedDefaultFoldersAndObjectTypes(conn);
    }

    private static void SeedDefaultFoldersAndObjectTypes(SqliteConnection conn)
    {
        var defaultFolders = new (string Path, string Icon, string Color)[]
        {
            ("01 — Architecture", "🏛️", "#0071E3"),
            ("01 — Architecture/Storage", "💾", "#10B981"),
            ("01 — Architecture/Protocols", "🔌", "#8B5CF6"),
            ("02 — Daily Notes", "📅", "#EAB308"),
            ("03 — Study & Research", "🧠", "#06B6D4"),
            ("04 — Capacities Studio", "✨", "#EC4899"),
            ("04 — Capacities Studio/Projects", "🚀", "#8B5CF6"),
            ("04 — Capacities Studio/Meetings", "🤝", "#F97316")
        };

        string nowIso = DateTime.UtcNow.ToString("O");
        foreach (var (path, icon, color) in defaultFolders)
        {
            using var fCmd = conn.CreateCommand();
            fCmd.CommandText = """
                INSERT OR IGNORE INTO folder_metadata (folder_path, icon_emoji, color_hex, is_expanded, updated_utc)
                VALUES ($path, $icon, $color, 1, $utc);
                """;
            fCmd.Parameters.AddWithValue("$path", path);
            fCmd.Parameters.AddWithValue("$icon", icon);
            fCmd.Parameters.AddWithValue("$color", color);
            fCmd.Parameters.AddWithValue("$utc", nowIso);
            fCmd.ExecuteNonQuery();
        }

        var defaultTypes = new (string Id, string Singular, string Plural, string Icon, string Color, string Folder, string Props, string Desc)[]
        {
            ("architecture-adr", "Architecture ADR", "Architecture ADRs", "🏛️", "#0071E3", "01 — Architecture", "type,status,subsystem,priority,collection", "System architecture decision records and subsystem specifications"),
            ("project", "Project", "Projects", "🚀", "#8B5CF6", "04 — Capacities Studio/Projects", "type,status,owner,target_date,collection", "Capacities Object Studio project workspaces and launch roadmaps"),
            ("meeting", "Meeting", "Meetings", "🤝", "#EC4899", "04 — Capacities Studio/Meetings", "type,date,attendees,status,collection", "Engineering design reviews, sync notes, and action items"),
            ("daily-note", "Daily Note", "Daily Notes", "📅", "#EAB308", "02 — Daily Notes", "type,date,status,priority,collection", "Chronological daily engineering logs and quick captures"),
            ("study-card", "Study Card", "Study Cards", "🧠", "#10B981", "03 — Study & Research", "type,deck,status,difficulty,collection", "FSRS v4 spaced repetition decks and research syntheses"),
            ("person", "Person", "People", "👤", "#06B6D4", "04 — Capacities Studio", "type,role,team,handle,collection", "Team members, maintainers, and external collaborators")
        };

        foreach (var t in defaultTypes)
        {
            using var tCmd = conn.CreateCommand();
            tCmd.CommandText = """
                INSERT OR IGNORE INTO capacities_object_types (type_id, singular_name, plural_name, icon_emoji, accent_color_hex, default_folder, schema_props_csv, description)
                VALUES ($id, $singular, $plural, $icon, $color, $folder, $props, $desc);
                """;
            tCmd.Parameters.AddWithValue("$id", t.Id);
            tCmd.Parameters.AddWithValue("$singular", t.Singular);
            tCmd.Parameters.AddWithValue("$plural", t.Plural);
            tCmd.Parameters.AddWithValue("$icon", t.Icon);
            tCmd.Parameters.AddWithValue("$color", t.Color);
            tCmd.Parameters.AddWithValue("$folder", t.Folder);
            tCmd.Parameters.AddWithValue("$props", t.Props);
            tCmd.Parameters.AddWithValue("$desc", t.Desc);
            tCmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _sqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        return conn;
    }

    public async Task LoadOrSeedVaultAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        EnsureStarterVaultFilesOnDisk();

        var mdFiles = Directory.EnumerateFiles(VaultRootPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".rhyolite" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + ".obsidian" + Path.DirectorySeparatorChar))
            .ToList();

        foreach (var fullPath in mdFiles)
        {
            ct.ThrowIfCancellationRequested();
            string relPath = Path.GetRelativePath(VaultRootPath, fullPath).Replace('\\', '/');
            string folder = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? string.Empty;
            string title = Path.GetFileNameWithoutExtension(fullPath);
            string raw = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);

            var note = new VaultNote
            {
                Id = relPath,
                RelativePath = relPath,
                FullPath = fullPath,
                Title = title,
                FolderPath = folder,
                RawMarkdown = raw,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath),
                FolderIconEmoji = GetFolderMetadata(folder).IconEmoji
            };

            MarkdownAstEngine.ParseNoteMetadata(note);
            HydrateFlashcardReviewsForNote(note);
            _notesByRelativePath[relPath] = note;

            // Ensure baseline snapshot exists for Time-Machine diffing
            RecordSnapshotInternal(note.RelativePath, note.RawMarkdown, "Initial Baseline");
            UpsertSqliteCacheRow(note);
        }

        LatestMetrics = KnowledgeGraphEngine.RebuildGraphRelationships(_notesByRelativePath.Values.ToList());
        Interlocked.Increment(ref _mutationVersion);
        sw.Stop();
        LastColdBootMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
    }

    public Task<VaultNote> SaveNoteAsync(
        string relativePath,
        string updatedMarkdown,
        CancellationToken ct) =>
        SaveNoteAsync(relativePath, updatedMarkdown, null, ct);

    /// <summary>
    /// Updates a note in-memory immediately (&lt; 0.5 ms AST delta), flushes the plain .md file atomically to disk,
    /// and updates the SQLite WAL sidecar row.
    /// </summary>
    public async Task<VaultNote> SaveNoteAsync(
        string relativePath,
        string updatedMarkdown,
        string? snapshotLabel = null,
        CancellationToken ct = default)
    {
        string normalizedRel = relativePath.Replace('\\', '/');
        string fullPath = Path.Combine(VaultRootPath, normalizedRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var writeSw = Stopwatch.StartNew();
        string tempPath = fullPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, updatedMarkdown, Encoding.UTF8, ct);
        File.Move(tempPath, fullPath, overwrite: true);
        writeSw.Stop();

        string folderPath = Path.GetDirectoryName(normalizedRel)?.Replace('\\', '/') ?? string.Empty;
        if (!_notesByRelativePath.TryGetValue(normalizedRel, out var note))
        {
            note = new VaultNote
            {
                Id = normalizedRel,
                RelativePath = normalizedRel,
                FullPath = fullPath,
                Title = Path.GetFileNameWithoutExtension(fullPath),
                FolderPath = folderPath,
                RawMarkdown = updatedMarkdown,
                FolderIconEmoji = GetFolderMetadata(folderPath).IconEmoji
            };
            _notesByRelativePath[normalizedRel] = note;
        }
        else
        {
            note.FolderPath = folderPath;
            note.RawMarkdown = updatedMarkdown;
            note.FolderIconEmoji = GetFolderMetadata(folderPath).IconEmoji;
        }

        note.LastWriteTimeUtc = DateTime.UtcNow;
        note.LastWriteMilliseconds = Math.Round(writeSw.Elapsed.TotalMilliseconds, 3);

        // Incremental single-note AST re-parse
        MarkdownAstEngine.ParseNoteMetadata(note);
        HydrateFlashcardReviewsForNote(note);

        // Update bidirectional links & PageRank
        LatestMetrics = KnowledgeGraphEngine.RebuildGraphRelationships(_notesByRelativePath.Values.ToList());
        Interlocked.Increment(ref _mutationVersion);

        // Persist snapshot & SQLite WAL sidecar
        RecordSnapshotInternal(note.RelativePath, note.RawMarkdown, snapshotLabel ?? $"Saved at {DateTime.Now:HH:mm:ss}");
        UpsertSqliteCacheRow(note);

        return note;
    }

    /// <summary>
    /// Deletes a note from disk, in-memory dictionary, and SQLite WAL cache, then rebuilds graph backlinks and increments MutationVersion.
    /// </summary>
    public Task<bool> DeleteNoteAsync(string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(DeleteNote(relativePath));
    }

    /// <summary>
    /// Synchronously deletes a note from disk, in-memory dictionary, and SQLite WAL cache, then rebuilds graph backlinks and increments MutationVersion.
    /// </summary>
    public bool DeleteNote(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        string normalizedRel = relativePath.Replace('\\', '/').TrimStart('/');
        VaultNote? removedNote = null;

        if (!_notesByRelativePath.TryRemove(normalizedRel, out removedNote))
        {
            var match = _notesByRelativePath.Values.FirstOrDefault(n =>
                string.Equals(n.RelativePath, normalizedRel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Title, Path.GetFileNameWithoutExtension(normalizedRel), StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                normalizedRel = match.RelativePath;
                _notesByRelativePath.TryRemove(normalizedRel, out removedNote);
            }
        }

        string fullPath = removedNote?.FullPath
            ?? Path.Combine(VaultRootPath, normalizedRel.Replace('/', Path.DirectorySeparatorChar));

        bool fileExisted = false;
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            fileExisted = true;
        }

        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM note_cache WHERE relative_path = $path;";
            cmd.Parameters.AddWithValue("$path", normalizedRel);
            cmd.ExecuteNonQuery();
        }

        LatestMetrics = KnowledgeGraphEngine.RebuildGraphRelationships(_notesByRelativePath.Values.ToList());
        Interlocked.Increment(ref _mutationVersion);

        return removedNote is not null || fileExisted;
    }

    /// <summary>
    /// Renames a note and automatically refactors all [[OldTitle]] WikiLinks across the entire vault.
    /// </summary>
    public async Task<VaultNote?> RenameNoteAndRefactorLinksAsync(
        string oldRelativePath,
        string newTitle,
        CancellationToken ct = default)
    {
        string normalizedOld = oldRelativePath.Replace('\\', '/');
        if (!_notesByRelativePath.TryRemove(normalizedOld, out var note))
            return null;

        string oldTitle = note.Title;
        string folder = note.FolderPath;
        string newRelPath = string.IsNullOrEmpty(folder) ? $"{newTitle}.md" : $"{folder}/{newTitle}.md";
        string newFullPath = Path.Combine(VaultRootPath, newRelPath.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(note.FullPath))
        {
            File.Move(note.FullPath, newFullPath, overwrite: true);
        }

        note.RelativePath = newRelPath;
        note.FullPath = newFullPath;
        note.Title = newTitle;
        _notesByRelativePath[newRelPath] = note;

        // Refactor [[OldTitle]] -> [[NewTitle]] across all referencing notes
        foreach (var other in _notesByRelativePath.Values)
        {
            if (other.RawMarkdown.Contains($"[[{oldTitle}", StringComparison.OrdinalIgnoreCase))
            {
                other.RawMarkdown = other.RawMarkdown
                    .Replace($"[[{oldTitle}]]", $"[[{newTitle}]]", StringComparison.OrdinalIgnoreCase)
                    .Replace($"[[{oldTitle}|", $"[[{newTitle}|", StringComparison.OrdinalIgnoreCase)
                    .Replace($"[[{oldTitle}#", $"[[{newTitle}#", StringComparison.OrdinalIgnoreCase);

                await File.WriteAllTextAsync(other.FullPath, other.RawMarkdown, Encoding.UTF8, ct);
                MarkdownAstEngine.ParseNoteMetadata(other);
                HydrateFlashcardReviewsForNote(other);
                UpsertSqliteCacheRow(other);
            }
        }

        LatestMetrics = KnowledgeGraphEngine.RebuildGraphRelationships(_notesByRelativePath.Values.ToList());
        Interlocked.Increment(ref _mutationVersion);
        return note;
    }

    public List<NoteSnapshot> GetSnapshotsForNote(string relativePath) => GetSnapshots(relativePath);

    /// <summary>
    /// Returns snapshots for a note ordered newest-first (snapshot_id DESC), hydrating from SQLite WAL if needed.
    /// </summary>
    public List<NoteSnapshot> GetSnapshots(string relativePath)
    {
        string normalizedRel = relativePath.Replace('\\', '/').TrimStart('/');
        var dbSnapshots = QuerySnapshotsFromSqlite(normalizedRel);

        if (!_snapshotsByPath.TryGetValue(normalizedRel, out var memList) || memList.Count == 0)
        {
            if (dbSnapshots.Count > 0)
            {
                var chronological = dbSnapshots.AsEnumerable().Reverse().ToList();
                _snapshotsByPath[normalizedRel] = chronological;
            }
            return dbSnapshots;
        }

        if (dbSnapshots.Count > memList.Count)
        {
            var chronological = dbSnapshots.AsEnumerable().Reverse().ToList();
            _snapshotsByPath[normalizedRel] = chronological;
            return dbSnapshots;
        }

        return memList.AsEnumerable().Reverse().ToList();
    }

    /// <summary>
    /// Records a manual milestone snapshot for the specified note in both memory and SQLite WAL sidecar.
    /// </summary>
    public NoteSnapshot SaveMilestoneSnapshot(string relativePath, string label = "Manual Milestone")
    {
        string normalizedRel = relativePath.Replace('\\', '/').TrimStart('/');
        string rawMarkdown = string.Empty;

        if (_notesByRelativePath.TryGetValue(normalizedRel, out var note))
        {
            rawMarkdown = note.RawMarkdown;
        }
        else
        {
            var match = _notesByRelativePath.Values.FirstOrDefault(n =>
                string.Equals(n.RelativePath, normalizedRel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Title, Path.GetFileNameWithoutExtension(normalizedRel), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                normalizedRel = match.RelativePath;
                rawMarkdown = match.RawMarkdown;
            }
            else
            {
                string fullPath = Path.Combine(VaultRootPath, normalizedRel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    rawMarkdown = File.ReadAllText(fullPath, Encoding.UTF8);
                }
            }
        }

        return RecordSnapshotInternal(normalizedRel, rawMarkdown, string.IsNullOrWhiteSpace(label) ? "Manual Milestone" : label, forceInsert: true);
    }

    /// <summary>
    /// Restores a note from a recorded snapshot matching timestampUtc (or the snapshot prior to latest when timestampUtc is null).
    /// </summary>
    public async Task<VaultNote?> RestoreSnapshotAsync(
        string relativePath,
        DateTimeOffset? timestampUtc = null,
        CancellationToken ct = default)
    {
        string normalizedRel = relativePath.Replace('\\', '/').TrimStart('/');
        var snapshots = GetSnapshots(normalizedRel);
        if (snapshots.Count == 0)
        {
            return null;
        }

        NoteSnapshot target;
        if (timestampUtc is not null)
        {
            var requestedUtc = timestampUtc.Value.UtcDateTime;
            target = snapshots
                .OrderBy(s => Math.Abs((s.CreatedUtc.ToUniversalTime() - requestedUtc).TotalMilliseconds))
                .First();
        }
        else
        {
            target = snapshots.Count >= 2 ? snapshots[1] : snapshots[0];
        }

        return await SaveNoteAsync(normalizedRel, target.RawMarkdown, $"Restored ({target.Label})", ct);
    }

    public CanvasDocument LoadCanvasDocument()
    {
        if (!File.Exists(_canvasFilePath))
        {
            var doc = JsonCanvasEngine.CreateDefaultArchitectureCanvas();
            File.WriteAllText(_canvasFilePath, JsonCanvasEngine.Serialize(doc), Encoding.UTF8);
            return doc;
        }
        return JsonCanvasEngine.Parse(File.ReadAllText(_canvasFilePath, Encoding.UTF8));
    }

    public void SaveCanvasDocument(CanvasDocument doc)
    {
        File.WriteAllText(_canvasFilePath, JsonCanvasEngine.Serialize(doc), Encoding.UTF8);
    }

    /// <summary>
    /// Upserts a GitHub PR diff analysis row in the SQLite WAL sidecar cache with O(1) PRIMARY KEY B-tree indexing.
    /// </summary>
    public void UpsertGitHubPrCache(string cacheKey, string repo, int prNumber, string headSha, string etag, string analysisMarkdown)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO github_pr_cache (cache_key, repo, pr_number, head_sha, etag, analysis_markdown, updated_utc)
            VALUES ($cacheKey, $repo, $prNumber, $headSha, $etag, $analysisMarkdown, $updatedUtc)
            ON CONFLICT(cache_key) DO UPDATE SET
                repo = excluded.repo,
                pr_number = excluded.pr_number,
                head_sha = excluded.head_sha,
                etag = excluded.etag,
                analysis_markdown = excluded.analysis_markdown,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$cacheKey", cacheKey);
        cmd.Parameters.AddWithValue("$repo", repo);
        cmd.Parameters.AddWithValue("$prNumber", prNumber);
        cmd.Parameters.AddWithValue("$headSha", headSha);
        cmd.Parameters.AddWithValue("$etag", etag ?? string.Empty);
        cmd.Parameters.AddWithValue("$analysisMarkdown", analysisMarkdown);
        cmd.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Queries a cached GitHub PR analysis row in O(1) via the cache_key PRIMARY KEY B-tree index.
    /// </summary>
    public (bool Found, string HeadSha, string Etag, string AnalysisMarkdown) TryGetGitHubPrCache(string cacheKey)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT head_sha, etag, analysis_markdown
            FROM github_pr_cache
            WHERE cache_key = $cacheKey
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$cacheKey", cacheKey);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return (
                true,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2));
        }

        return (false, string.Empty, string.Empty, string.Empty);
    }

    /// <summary>
    /// Upserts an external workspace sync binding (e.g. Confluence/Notion/GitHub) in the SQLite WAL sidecar cache.
    /// </summary>
    public void UpsertExternalSyncBinding(string connectorKind, string workspaceOrSpaceId, string relativePath, string lastSyncedSha256)
    {
        string normalizedPath = relativePath.Replace('\\', '/');
        string bindingKey = BuildSyncBindingKey(connectorKind, workspaceOrSpaceId, normalizedPath);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO external_sync_bindings (binding_key, connector_kind, workspace_or_space_id, relative_path, last_synced_sha256, updated_utc)
            VALUES ($bindingKey, $connectorKind, $workspaceId, $relativePath, $sha256, $updatedUtc)
            ON CONFLICT(binding_key) DO UPDATE SET
                connector_kind = excluded.connector_kind,
                workspace_or_space_id = excluded.workspace_or_space_id,
                relative_path = excluded.relative_path,
                last_synced_sha256 = excluded.last_synced_sha256,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$bindingKey", bindingKey);
        cmd.Parameters.AddWithValue("$connectorKind", connectorKind);
        cmd.Parameters.AddWithValue("$workspaceId", workspaceOrSpaceId);
        cmd.Parameters.AddWithValue("$relativePath", normalizedPath);
        cmd.Parameters.AddWithValue("$sha256", lastSyncedSha256);
        cmd.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Retrieves the last synced SHA-256 content hash for an external connector binding via O(1) PRIMARY KEY lookup.
    /// </summary>
    public string? GetLastSyncedHash(string connectorKind, string workspaceOrSpaceId, string relativePath)
    {
        string normalizedPath = relativePath.Replace('\\', '/');
        string bindingKey = BuildSyncBindingKey(connectorKind, workspaceOrSpaceId, normalizedPath);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT last_synced_sha256
            FROM external_sync_bindings
            WHERE binding_key = $bindingKey
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$bindingKey", bindingKey);

        return cmd.ExecuteScalar() as string;
    }

    private static string BuildSyncBindingKey(string connectorKind, string workspaceOrSpaceId, string normalizedPath) =>
        $"{connectorKind.Trim()}:{workspaceOrSpaceId.Trim()}:{normalizedPath}";

    /// <summary>
    /// Lists external sync bindings from SQLite WAL, optionally filtered by connectorKind.
    /// </summary>
    public List<(string BindingKey, string ConnectorKind, string WorkspaceOrSpaceId, string RelativePath, string LastSyncedSha256, string UpdatedUtc)> GetExternalSyncBindings(string? connectorKind = null)
    {
        var results = new List<(string, string, string, string, string, string)>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(connectorKind))
        {
            cmd.CommandText = """
                SELECT binding_key, connector_kind, workspace_or_space_id, relative_path, last_synced_sha256, updated_utc
                FROM external_sync_bindings
                ORDER BY binding_key ASC;
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT binding_key, connector_kind, workspace_or_space_id, relative_path, last_synced_sha256, updated_utc
                FROM external_sync_bindings
                WHERE connector_kind = $kind
                ORDER BY binding_key ASC;
                """;
            cmd.Parameters.AddWithValue("$kind", connectorKind.Trim());
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return results;
    }

    /// <summary>
    /// Persists an FSRS v4 flashcard review state in O(1) to the SQLite WAL flashcard_reviews table.
    /// </summary>
    public void SaveFlashcardReview(string cardId, double stabilityDays, double difficulty, int reps, DateTimeOffset nextReviewUtc)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO flashcard_reviews (card_id, stability_days, difficulty, reps, next_review_utc, updated_utc)
            VALUES ($cardId, $stability, $difficulty, $reps, $nextUtc, $updatedUtc)
            ON CONFLICT(card_id) DO UPDATE SET
                stability_days = excluded.stability_days,
                difficulty = excluded.difficulty,
                reps = excluded.reps,
                next_review_utc = excluded.next_review_utc,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$cardId", cardId);
        cmd.Parameters.AddWithValue("$stability", stabilityDays);
        cmd.Parameters.AddWithValue("$difficulty", difficulty);
        cmd.Parameters.AddWithValue("$reps", reps);
        cmd.Parameters.AddWithValue("$nextUtc", nextReviewUtc.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();

        // Keep in-memory FlashcardItem instances synchronized if loaded
        foreach (var note in _notesByRelativePath.Values)
        {
            foreach (var card in note.Flashcards)
            {
                if (string.Equals(card.Id, cardId, StringComparison.Ordinal))
                {
                    card.StabilityDays = stabilityDays;
                    card.Difficulty = difficulty;
                    card.Reps = reps;
                    card.NextReviewUtc = nextReviewUtc.UtcDateTime;
                    card.LastReviewUtc = DateTime.UtcNow;
                }
            }
        }
    }

    /// <summary>
    /// Asynchronously persists an FSRS v4 flashcard review state to the SQLite WAL flashcard_reviews table.
    /// </summary>
    public Task SaveFlashcardReviewAsync(
        string cardId,
        double stabilityDays,
        double difficulty,
        int reps,
        DateTimeOffset nextReviewUtc,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SaveFlashcardReview(cardId, stabilityDays, difficulty, reps, nextReviewUtc);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads a persisted FSRS v4 flashcard review state in O(1) via the card_id PRIMARY KEY B-tree index.
    /// </summary>
    public (double StabilityDays, double Difficulty, int Reps, DateTimeOffset NextReviewUtc)? GetFlashcardReview(string cardId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT stability_days, difficulty, reps, next_review_utc
            FROM flashcard_reviews
            WHERE card_id = $cardId
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$cardId", cardId);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            double stability = reader.GetDouble(0);
            double difficulty = reader.GetDouble(1);
            int reps = reader.GetInt32(2);
            string nextUtcStr = reader.GetString(3);
            var nextReview = DateTimeOffset.TryParse(nextUtcStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
                ? dto
                : DateTimeOffset.UtcNow;

            return (stability, difficulty, reps, nextReview);
        }

        return null;
    }

    /// <summary>
    /// Asynchronously reads a persisted FSRS v4 flashcard review state in O(1) via the card_id PRIMARY KEY B-tree index.
    /// </summary>
    public Task<(double StabilityDays, double Difficulty, int Reps, DateTimeOffset NextReviewUtc)?> GetFlashcardReviewAsync(
        string cardId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetFlashcardReview(cardId));
    }

    private void HydrateFlashcardReviewsForNote(VaultNote note)
    {
        if (note.Flashcards.Count == 0)
        {
            return;
        }

        foreach (var card in note.Flashcards)
        {
            var review = GetFlashcardReview(card.Id);
            if (review is { } r)
            {
                card.StabilityDays = r.StabilityDays;
                card.Difficulty = r.Difficulty;
                card.Reps = r.Reps;
                card.NextReviewUtc = r.NextReviewUtc.UtcDateTime;
            }
        }
    }

    /// <summary>
    /// Persists a UI preference key-value pair (e.g. "design_system_id", "dark_mode") to the SQLite WAL sidecar in O(1) time.
    /// </summary>
    public async Task SaveUiPreferenceAsync(string key, string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ui_preferences (pref_key, pref_value, updated_utc)
            VALUES ($key, $val, $utc)
            ON CONFLICT(pref_key) DO UPDATE SET
                pref_value = excluded.pref_value,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$val", value ?? string.Empty);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Synchronously persists a UI preference key-value pair to the SQLite WAL sidecar in O(1) time.
    /// </summary>
    public void SaveUiPreference(string key, string value)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ui_preferences (pref_key, pref_value, updated_utc)
            VALUES ($key, $val, $utc)
            ON CONFLICT(pref_key) DO UPDATE SET
                pref_value = excluded.pref_value,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$val", value ?? string.Empty);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads a persisted UI preference value in O(1) via the pref_key PRIMARY KEY B-tree index.
    /// </summary>
    public async Task<string?> GetUiPreferenceAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pref_value
            FROM ui_preferences
            WHERE pref_key = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    /// <summary>
    /// Synchronously reads a persisted UI preference value in O(1) via the pref_key PRIMARY KEY B-tree index.
    /// </summary>
    public string? GetUiPreference(string key)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pref_value
            FROM ui_preferences
            WHERE pref_key = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private NoteSnapshot RecordSnapshotInternal(VaultNote note, string label = "Auto-Snapshot") =>
        RecordSnapshotInternal(note.RelativePath, note.RawMarkdown, label, forceInsert: false);

    private NoteSnapshot RecordSnapshotInternal(string relativePath, string content, string label = "Auto-Snapshot", bool forceInsert = false)
    {
        string normalizedRel = relativePath.Replace('\\', '/').TrimStart('/');
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));
        var list = _snapshotsByPath.GetOrAdd(normalizedRel, _ => []);

        if (!forceInsert && list.Count > 0 && list[^1].ContentSha256 == sha256)
        {
            return list[^1];
        }

        var nowUtc = DateTime.UtcNow;
        string nowIso = nowUtc.ToString("O");
        string effectiveLabel = string.IsNullOrWhiteSpace(label) ? "Auto-Snapshot" : label;
        long insertedId;

        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO note_snapshots (path, created_utc, content, label)
                VALUES ($path, $utc, $content, $label);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$path", normalizedRel);
            cmd.Parameters.AddWithValue("$utc", nowIso);
            cmd.Parameters.AddWithValue("$content", content ?? string.Empty);
            cmd.Parameters.AddWithValue("$label", effectiveLabel);
            insertedId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);

            using var trimCmd = conn.CreateCommand();
            trimCmd.CommandText = """
                DELETE FROM note_snapshots
                WHERE path = $path
                  AND snapshot_id NOT IN (
                      SELECT snapshot_id
                      FROM note_snapshots
                      WHERE path = $path
                      ORDER BY snapshot_id DESC
                      LIMIT 25
                  );
                """;
            trimCmd.Parameters.AddWithValue("$path", normalizedRel);
            trimCmd.ExecuteNonQuery();
        }

        var snap = new NoteSnapshot(
            insertedId > 0 ? insertedId.ToString(CultureInfo.InvariantCulture) : Guid.NewGuid().ToString("N")[..10],
            normalizedRel,
            nowUtc,
            effectiveLabel,
            content ?? string.Empty,
            sha256);

        list.Add(snap);
        while (list.Count > 25)
        {
            list.RemoveAt(0);
        }

        return snap;
    }

    private List<NoteSnapshot> QuerySnapshotsFromSqlite(string normalizedRel)
    {
        var results = new List<NoteSnapshot>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT snapshot_id, path, created_utc, content, label
            FROM note_snapshots
            WHERE path = $path
            ORDER BY snapshot_id DESC
            LIMIT 25;
            """;
        cmd.Parameters.AddWithValue("$path", normalizedRel);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string path = reader.GetString(1);
            string createdStr = reader.GetString(2);
            string content = reader.GetString(3);
            string label = reader.GetString(4);
            var createdUtc = DateTime.TryParse(createdStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToUniversalTime()
                : DateTime.UtcNow;
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

            results.Add(new NoteSnapshot(
                id.ToString(CultureInfo.InvariantCulture),
                path,
                createdUtc,
                label,
                content,
                sha256));
        }

        return results;
    }

    /// <summary>
    /// Assigns an icon emoji and optional accent hex color to a folder or subfolder in SQLite WAL folder_metadata.
    /// </summary>
    public void SetFolderIconAndColor(string folderPath, string iconEmoji, string? colorHex = null)
    {
        string normalized = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        var existing = GetFolderMetadata(normalized);
        string resolvedIcon = string.IsNullOrWhiteSpace(iconEmoji) ? existing.IconEmoji : iconEmoji.Trim();
        string resolvedColor = string.IsNullOrWhiteSpace(colorHex) ? existing.ColorHex : colorHex.Trim();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO folder_metadata (folder_path, icon_emoji, color_hex, is_expanded, updated_utc)
            VALUES ($path, $icon, $color, $expanded, $utc)
            ON CONFLICT(folder_path) DO UPDATE SET
                icon_emoji = excluded.icon_emoji,
                color_hex = excluded.color_hex,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$path", normalized);
        cmd.Parameters.AddWithValue("$icon", resolvedIcon);
        cmd.Parameters.AddWithValue("$color", resolvedColor);
        cmd.Parameters.AddWithValue("$expanded", existing.IsExpanded ? 1 : 0);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();

        foreach (var note in _notesByRelativePath.Values)
        {
            if (string.Equals(note.FolderPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                note.FolderIconEmoji = resolvedIcon;
            }
        }

        Interlocked.Increment(ref _mutationVersion);
    }

    /// <summary>
    /// Reads the icon emoji, accent color hex, and expansion state for a folder from SQLite WAL folder_metadata.
    /// </summary>
    public (string IconEmoji, string ColorHex, bool IsExpanded) GetFolderMetadata(string folderPath)
    {
        string normalized = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalized))
        {
            return ("📁", "#0071E3", true);
        }

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT icon_emoji, color_hex, is_expanded
            FROM folder_metadata
            WHERE folder_path = $path
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$path", normalized);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return (
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2) != 0);
        }

        return InferDefaultFolderMetadata(normalized);
    }

    /// <summary>
    /// Toggles the expanded/collapsed state of a folder in SQLite WAL folder_metadata and returns the new IsExpanded state.
    /// </summary>
    public bool ToggleFolderExpanded(string folderPath)
    {
        string normalized = NormalizeFolderPath(folderPath);
        var current = GetFolderMetadata(normalized);
        bool nextExpanded = !current.IsExpanded;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO folder_metadata (folder_path, icon_emoji, color_hex, is_expanded, updated_utc)
            VALUES ($path, $icon, $color, $expanded, $utc)
            ON CONFLICT(folder_path) DO UPDATE SET
                is_expanded = excluded.is_expanded,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$path", normalized);
        cmd.Parameters.AddWithValue("$icon", current.IconEmoji);
        cmd.Parameters.AddWithValue("$color", current.ColorHex);
        cmd.Parameters.AddWithValue("$expanded", nextExpanded ? 1 : 0);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();

        Interlocked.Increment(ref _mutationVersion);
        return nextExpanded;
    }

    /// <summary>
    /// Creates a folder or nested subfolder on disk, persists its icon/color metadata in SQLite WAL, and returns its VaultFolderNode.
    /// </summary>
    public VaultFolderNode CreateFolderOrSubfolder(string folderPath, string iconEmoji = "📁", string colorHex = "#0071E3")
    {
        string normalized = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ArgumentException("Folder path cannot be empty.", nameof(folderPath));
        }

        string fullDirPath = Path.Combine(VaultRootPath, normalized.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(fullDirPath);

        // Ensure ancestor folders also exist in folder_metadata
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string currentPrefix = string.Empty;
        for (int i = 0; i < segments.Length; i++)
        {
            currentPrefix = i == 0 ? segments[0] : $"{currentPrefix}/{segments[i]}";
            if (i < segments.Length - 1)
            {
                var meta = GetFolderMetadata(currentPrefix);
                using var conn = OpenConnection();
                using var ancestorCmd = conn.CreateCommand();
                ancestorCmd.CommandText = """
                    INSERT OR IGNORE INTO folder_metadata (folder_path, icon_emoji, color_hex, is_expanded, updated_utc)
                    VALUES ($path, $icon, $color, 1, $utc);
                    """;
                ancestorCmd.Parameters.AddWithValue("$path", currentPrefix);
                ancestorCmd.Parameters.AddWithValue("$icon", meta.IconEmoji);
                ancestorCmd.Parameters.AddWithValue("$color", meta.ColorHex);
                ancestorCmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                ancestorCmd.ExecuteNonQuery();
            }
        }

        SetFolderIconAndColor(normalized, iconEmoji, colorHex);

        var allNodes = GetFlattenedAllFolders();
        return allNodes.FirstOrDefault(f => string.Equals(f.FolderPath, normalized, StringComparison.OrdinalIgnoreCase))
            ?? new VaultFolderNode
            {
                FolderPath = normalized,
                DisplayName = segments[^1],
                ParentFolderPath = segments.Length > 1 ? string.Join('/', segments[..^1]) : null,
                Depth = segments.Length - 1,
                IconEmoji = iconEmoji,
                AccentColorHex = colorHex,
                IsExpanded = true
            };
    }

    /// <summary>
    /// Moves a note into a target folder or nested subfolder on disk, updating its path, FolderIconEmoji, and SQLite WAL cache row.
    /// </summary>
    public async Task<VaultNote?> MoveNoteToFolderAsync(
        string noteRelativePath,
        string targetFolderPath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string normalizedNoteRel = noteRelativePath.Replace('\\', '/').TrimStart('/');
        if (!_notesByRelativePath.TryRemove(normalizedNoteRel, out var note))
        {
            var match = _notesByRelativePath.Values.FirstOrDefault(n =>
                string.Equals(n.RelativePath, normalizedNoteRel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Title, Path.GetFileNameWithoutExtension(normalizedNoteRel), StringComparison.OrdinalIgnoreCase));
            if (match is null || !_notesByRelativePath.TryRemove(match.RelativePath, out note))
            {
                return null;
            }
            normalizedNoteRel = match.RelativePath;
        }

        string normalizedFolder = NormalizeFolderPath(targetFolderPath);
        string fileName = Path.GetFileName(note.FullPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{note.Title}.md";
        }

        string newRelPath = string.IsNullOrEmpty(normalizedFolder)
            ? fileName
            : $"{normalizedFolder}/{fileName}";
        string newFullPath = Path.Combine(VaultRootPath, newRelPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(newFullPath)!);

        if (!string.IsNullOrEmpty(normalizedFolder))
        {
            var meta = GetFolderMetadata(normalizedFolder);
            SetFolderIconAndColor(normalizedFolder, meta.IconEmoji, meta.ColorHex);
        }

        if (File.Exists(note.FullPath) && !string.Equals(note.FullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(note.FullPath, newFullPath, overwrite: true);
        }
        else if (!File.Exists(newFullPath))
        {
            await File.WriteAllTextAsync(newFullPath, note.RawMarkdown, Encoding.UTF8, ct);
        }

        using (var conn = OpenConnection())
        using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM note_cache WHERE relative_path = $oldPath;";
            delCmd.Parameters.AddWithValue("$oldPath", normalizedNoteRel);
            delCmd.ExecuteNonQuery();
        }

        note.RelativePath = newRelPath;
        note.FullPath = newFullPath;
        note.FolderPath = normalizedFolder;
        note.FolderIconEmoji = GetFolderMetadata(normalizedFolder).IconEmoji;
        _notesByRelativePath[newRelPath] = note;

        UpsertSqliteCacheRow(note);
        LatestMetrics = KnowledgeGraphEngine.RebuildGraphRelationships(_notesByRelativePath.Values.ToList());
        Interlocked.Increment(ref _mutationVersion);
        return note;
    }

    /// <summary>
    /// Builds the full hierarchical tree of root folders and nested subfolders with direct and total note counts.
    /// </summary>
    public IReadOnlyList<VaultFolderNode> GetFolderHierarchyTree()
    {
        var metadataMap = new Dictionary<string, (string Icon, string Color, bool Expanded)>(StringComparer.OrdinalIgnoreCase);

        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT folder_path, icon_emoji, color_hex, is_expanded FROM folder_metadata;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string path = NormalizeFolderPath(reader.GetString(0));
                if (!string.IsNullOrEmpty(path))
                {
                    metadataMap[path] = (reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0);
                }
            }
        }

        var allFolderPaths = new HashSet<string>(metadataMap.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var note in _notesByRelativePath.Values)
        {
            string f = NormalizeFolderPath(note.FolderPath);
            if (!string.IsNullOrEmpty(f))
            {
                allFolderPaths.Add(f);
            }
        }

        // Ensure all ancestor folder prefixes exist in the set
        foreach (string path in allFolderPaths.ToList())
        {
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string running = string.Empty;
            for (int i = 0; i < parts.Length; i++)
            {
                running = i == 0 ? parts[0] : $"{running}/{parts[i]}";
                allFolderPaths.Add(running);
            }
        }

        var nodesByPath = new Dictionary<string, VaultFolderNode>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in allFolderPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            int lastSlash = path.LastIndexOf('/');
            string? parentPath = lastSlash > 0 ? path[..lastSlash] : null;
            string displayName = lastSlash > 0 ? path[(lastSlash + 1)..] : path;
            int depth = path.Count(c => c == '/');

            (string IconEmoji, string ColorHex, bool IsExpanded) meta = metadataMap.TryGetValue(path, out var m)
                ? m
                : InferDefaultFolderMetadata(path);

            nodesByPath[path] = new VaultFolderNode
            {
                FolderPath = path,
                DisplayName = displayName,
                ParentFolderPath = parentPath,
                Depth = depth,
                IconEmoji = meta.IconEmoji,
                AccentColorHex = meta.ColorHex,
                IsExpanded = meta.IsExpanded
            };
        }

        foreach (var note in _notesByRelativePath.Values.OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase))
        {
            string f = NormalizeFolderPath(note.FolderPath);
            if (!string.IsNullOrEmpty(f) && nodesByPath.TryGetValue(f, out var folderNode))
            {
                note.FolderIconEmoji = folderNode.IconEmoji;
                folderNode.Notes.Add(note);
                folderNode.DirectNoteCount++;
            }
        }

        var roots = new List<VaultFolderNode>();
        foreach (var node in nodesByPath.Values.OrderBy(n => n.FolderPath, StringComparer.OrdinalIgnoreCase))
        {
            if (node.ParentFolderPath is not null && nodesByPath.TryGetValue(node.ParentFolderPath, out var parentNode))
            {
                parentNode.Subfolders.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        foreach (var root in roots)
        {
            ComputeTotalNotesRecursive(root);
        }

        return roots;
    }

    /// <summary>
    /// Returns the pre-order flattened list of visible folders/subfolders, respecting IsExpanded on parent folders.
    /// </summary>
    public IReadOnlyList<VaultFolderNode> GetFlattenedVisibleFolderTree()
    {
        var roots = GetFolderHierarchyTree();
        var visible = new List<VaultFolderNode>();
        foreach (var root in roots)
        {
            AppendVisibleRecursive(root, visible);
        }
        return visible;
    }

    /// <summary>
    /// Builds the unified hierarchical folder, subfolder, and Markdown note explorer tree (Obsidian/Craft standard:
    /// folders first at each level, followed by direct notes indented at Depth + 1).
    /// </summary>
    public IReadOnlyList<VaultTreeItem> GetUnifiedExplorerTree(
        string? searchFilter = null,
        string? objectTypeFilter = null,
        string? selectedNotePath = null,
        string? selectedFolderPath = null)
    {
        var roots = GetFolderHierarchyTree();
        var items = new List<VaultTreeItem>();

        bool hasSearch = !string.IsNullOrWhiteSpace(searchFilter);
        bool hasTypeFilter = !string.IsNullOrWhiteSpace(objectTypeFilter) &&
                             !string.Equals(objectTypeFilter, "All", StringComparison.OrdinalIgnoreCase);
        bool hasActiveFilter = hasSearch || hasTypeFilter;

        string? normalizedSelectedNote = string.IsNullOrWhiteSpace(selectedNotePath)
            ? null
            : selectedNotePath.Replace('\\', '/').TrimStart('/');
        string? normalizedSelectedFolder = string.IsNullOrWhiteSpace(selectedFolderPath)
            ? null
            : NormalizeFolderPath(selectedFolderPath);

        bool NoteMatches(VaultNote note)
        {
            if (hasTypeFilter &&
                !string.Equals(note.ObjectType, objectTypeFilter, StringComparison.OrdinalIgnoreCase) &&
                !note.ObjectType.Contains(objectTypeFilter!, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (hasSearch &&
                !note.Title.Contains(searchFilter!, StringComparison.OrdinalIgnoreCase) &&
                !note.RawMarkdown.Contains(searchFilter!, StringComparison.OrdinalIgnoreCase) &&
                !note.ObjectType.Contains(searchFilter!, StringComparison.OrdinalIgnoreCase) &&
                !note.FolderPath.Contains(searchFilter!, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        bool SubtreeMatches(VaultFolderNode node)
        {
            if (!hasActiveFilter)
            {
                return true;
            }

            if (hasSearch && !hasTypeFilter &&
                node.DisplayName.Contains(searchFilter!, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (node.Notes.Any(NoteMatches))
            {
                return true;
            }

            foreach (var sub in node.Subfolders)
            {
                if (SubtreeMatches(sub))
                {
                    return true;
                }
            }

            return false;
        }

        void TraverseFolder(VaultFolderNode node)
        {
            if (!SubtreeMatches(node))
            {
                return;
            }

            bool effectiveExpanded = node.IsExpanded || hasActiveFilter;
            items.Add(new VaultTreeItem
            {
                Id = "folder:" + node.FolderPath,
                DisplayName = node.DisplayName,
                RelativePath = node.FolderPath,
                ParentFolderPath = node.ParentFolderPath,
                Depth = node.Depth,
                IsFolder = true,
                IsExpanded = effectiveExpanded,
                IsSelected = normalizedSelectedFolder is not null &&
                             string.Equals(node.FolderPath, normalizedSelectedFolder, StringComparison.OrdinalIgnoreCase),
                IconEmoji = node.IconEmoji,
                AccentColorHex = node.AccentColorHex,
                RightBadgeText = node.TotalNoteCount.ToString(CultureInfo.InvariantCulture),
                FolderNode = node
            });

            if (!effectiveExpanded)
            {
                return;
            }

            // 1. Subfolders first (Obsidian / Craft hierarchy convention)
            foreach (var sub in node.Subfolders)
            {
                TraverseFolder(sub);
            }

            // 2. Direct notes inside this folder at Depth = node.Depth + 1
            bool folderNameMatchedDirectly = hasSearch && !hasTypeFilter &&
                                             node.DisplayName.Contains(searchFilter!, StringComparison.OrdinalIgnoreCase);
            foreach (var note in node.Notes)
            {
                if (!folderNameMatchedDirectly && !NoteMatches(note))
                {
                    continue;
                }

                items.Add(new VaultTreeItem
                {
                    Id = "note:" + note.RelativePath,
                    DisplayName = note.Title,
                    RelativePath = note.RelativePath,
                    ParentFolderPath = node.FolderPath,
                    Depth = node.Depth + 1,
                    IsFolder = false,
                    IsExpanded = false,
                    IsSelected = normalizedSelectedNote is not null &&
                                 string.Equals(note.RelativePath, normalizedSelectedNote, StringComparison.OrdinalIgnoreCase),
                    IsPinned = note.IsPinned,
                    IconEmoji = note.ObjectIconEmoji,
                    AccentColorHex = note.CoverColorHex,
                    RightBadgeText = note.IsPinned ? "📌" : string.Empty,
                    Note = note
                });
            }
        }

        foreach (var root in roots)
        {
            TraverseFolder(root);
        }

        // 3. Root-level notes (outside any folder) at Depth = 0
        foreach (var rootNote in _notesByRelativePath.Values
                     .Where(n => string.IsNullOrEmpty(NormalizeFolderPath(n.FolderPath)))
                     .OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (!NoteMatches(rootNote))
            {
                continue;
            }

            items.Add(new VaultTreeItem
            {
                Id = "note:" + rootNote.RelativePath,
                DisplayName = rootNote.Title,
                RelativePath = rootNote.RelativePath,
                ParentFolderPath = null,
                Depth = 0,
                IsFolder = false,
                IsExpanded = false,
                IsSelected = normalizedSelectedNote is not null &&
                             string.Equals(rootNote.RelativePath, normalizedSelectedNote, StringComparison.OrdinalIgnoreCase),
                IsPinned = rootNote.IsPinned,
                IconEmoji = rootNote.ObjectIconEmoji,
                AccentColorHex = rootNote.CoverColorHex,
                RightBadgeText = rootNote.IsPinned ? "📌" : string.Empty,
                Note = rootNote
            });
        }

        return items;
    }

    private List<VaultFolderNode> GetFlattenedAllFolders()
    {
        var roots = GetFolderHierarchyTree();
        var all = new List<VaultFolderNode>();
        foreach (var root in roots)
        {
            AppendAllRecursive(root, all);
        }
        return all;
    }

    private static int ComputeTotalNotesRecursive(VaultFolderNode node)
    {
        int sum = node.DirectNoteCount;
        foreach (var sub in node.Subfolders)
        {
            sum += ComputeTotalNotesRecursive(sub);
        }
        node.TotalNoteCount = sum;
        return sum;
    }

    private static void AppendVisibleRecursive(VaultFolderNode node, List<VaultFolderNode> target)
    {
        target.Add(node);
        if (!node.IsExpanded)
        {
            return;
        }

        foreach (var sub in node.Subfolders)
        {
            AppendVisibleRecursive(sub, target);
        }
    }

    private static void AppendAllRecursive(VaultFolderNode node, List<VaultFolderNode> target)
    {
        target.Add(node);
        foreach (var sub in node.Subfolders)
        {
            AppendAllRecursive(sub, target);
        }
    }

    /// <summary>
    /// Returns all Capacities Object Studio schemas with live object counts across the vault.
    /// </summary>
    public IReadOnlyList<CapacitiesObjectTypeSchema> GetObjectTypeSchemas()
    {
        var countsByType = _notesByRelativePath.Values
            .GroupBy(n => n.ObjectType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var schemas = new List<CapacitiesObjectTypeSchema>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type_id, singular_name, plural_name, icon_emoji, accent_color_hex, default_folder, schema_props_csv, description
            FROM capacities_object_types
            ORDER BY rowid ASC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string typeId = reader.GetString(0);
            string singular = reader.GetString(1);
            string plural = reader.GetString(2);
            string icon = reader.GetString(3);
            string color = reader.GetString(4);
            string folder = reader.GetString(5);
            string propsCsv = reader.GetString(6);
            string desc = reader.GetString(7);

            countsByType.TryGetValue(singular, out int count);
            var propList = propsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            schemas.Add(new CapacitiesObjectTypeSchema(
                typeId,
                singular,
                plural,
                icon,
                color,
                folder,
                propList,
                desc,
                count));
        }

        return schemas;
    }

    /// <summary>
    /// Persists a custom Capacities Object Type schema in SQLite WAL capacities_object_types.
    /// </summary>
    public void SaveCustomObjectType(CapacitiesObjectTypeSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO capacities_object_types (type_id, singular_name, plural_name, icon_emoji, accent_color_hex, default_folder, schema_props_csv, description)
            VALUES ($id, $singular, $plural, $icon, $color, $folder, $props, $desc)
            ON CONFLICT(type_id) DO UPDATE SET
                singular_name = excluded.singular_name,
                plural_name = excluded.plural_name,
                icon_emoji = excluded.icon_emoji,
                accent_color_hex = excluded.accent_color_hex,
                default_folder = excluded.default_folder,
                schema_props_csv = excluded.schema_props_csv,
                description = excluded.description;
            """;
        cmd.Parameters.AddWithValue("$id", schema.TypeId);
        cmd.Parameters.AddWithValue("$singular", schema.SingularName);
        cmd.Parameters.AddWithValue("$plural", schema.PluralName);
        cmd.Parameters.AddWithValue("$icon", schema.IconEmoji);
        cmd.Parameters.AddWithValue("$color", schema.AccentColorHex);
        cmd.Parameters.AddWithValue("$folder", NormalizeFolderPath(schema.DefaultFolderPath));
        cmd.Parameters.AddWithValue("$props", string.Join(",", schema.SchemaPropertyKeys));
        cmd.Parameters.AddWithValue("$desc", schema.Description);
        cmd.ExecuteNonQuery();

        Interlocked.Increment(ref _mutationVersion);
    }

    /// <summary>
    /// Creates a new typed Capacities Object Markdown note with structured YAML frontmatter, icon, cover color, and collection.
    /// </summary>
    public async Task<VaultNote> CreateTypedObjectNoteAsync(
        string objectTypeIdOrName,
        string title,
        string? targetFolder = null,
        string? collectionName = null,
        CancellationToken ct = default)
    {
        var schemas = GetObjectTypeSchemas();
        var schema = schemas.FirstOrDefault(s =>
            string.Equals(s.TypeId, objectTypeIdOrName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.SingularName, objectTypeIdOrName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.PluralName, objectTypeIdOrName, StringComparison.OrdinalIgnoreCase))
            ?? schemas[0];

        string folder = !string.IsNullOrWhiteSpace(targetFolder)
            ? NormalizeFolderPath(targetFolder)
            : NormalizeFolderPath(schema.DefaultFolderPath);

        if (!string.IsNullOrEmpty(folder))
        {
            var folderMeta = GetFolderMetadata(folder);
            CreateFolderOrSubfolder(folder, folderMeta.IconEmoji, folderMeta.ColorHex);
        }

        string safeTitle = string.IsNullOrWhiteSpace(title)
            ? $"New {schema.SingularName} {DateTime.UtcNow:HHmmss}"
            : title.Trim();

        string resolvedCollection = !string.IsNullOrWhiteSpace(collectionName)
            ? collectionName.Trim()
            : (folder.Contains("Capacities", StringComparison.OrdinalIgnoreCase) ? "Launch Roadmap" : "Core Architecture");

        string dateIso = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string markdown = $"""
            ---
            type: {schema.SingularName}
            icon: {schema.IconEmoji}
            cover: {schema.AccentColorHex}
            collection: {resolvedCollection}
            status: Active
            priority: P1 High
            date: {dateIso}
            tags: [capacities, {schema.TypeId}]
            ---
            # {schema.IconEmoji} {safeTitle}

            Created in **Capacities Object Studio** as a typed `{schema.SingularName}` object inside `{folder}` (`{resolvedCollection}`).

            ## Structured Object Context
            - Connected to [[Storage Architecture]] and [[Apple Design Tokens]].
            - [ ] Complete initial review for {safeTitle} 📅 {dateIso} #{schema.TypeId}
            """;

        string relPath = string.IsNullOrEmpty(folder) ? $"{safeTitle}.md" : $"{folder}/{safeTitle}.md";
        return await SaveNoteAsync(relPath, markdown, $"Created {schema.SingularName}", ct);
    }

    private static string NormalizeFolderPath(string? folderPath) =>
        string.IsNullOrWhiteSpace(folderPath)
            ? string.Empty
            : folderPath.Replace('\\', '/').Trim('/');

    private static (string IconEmoji, string ColorHex, bool IsExpanded) InferDefaultFolderMetadata(string normalizedFolder) =>
        normalizedFolder.ToLowerInvariant() switch
        {
            var f when f.Contains("storage") => ("💾", "#10B981", true),
            var f when f.Contains("protocol") => ("🔌", "#8B5CF6", true),
            var f when f.Contains("architecture") => ("🏛️", "#0071E3", true),
            var f when f.Contains("daily") => ("📅", "#EAB308", true),
            var f when f.Contains("study") => ("🧠", "#06B6D4", true),
            var f when f.Contains("project") => ("🚀", "#8B5CF6", true),
            var f when f.Contains("meeting") => ("🤝", "#F97316", true),
            var f when f.Contains("capacities") => ("✨", "#EC4899", true),
            _ => ("📁", "#0071E3", true)
        };

    private void UpsertSqliteCacheRow(VaultNote note)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO note_cache (relative_path, title, folder_path, content_sha256, last_write_utc, word_count, properties_json, tags_json)
            VALUES ($path, $title, $folder, $sha, $utc, $words, $props, $tags)
            ON CONFLICT(relative_path) DO UPDATE SET
                title = excluded.title,
                folder_path = excluded.folder_path,
                content_sha256 = excluded.content_sha256,
                last_write_utc = excluded.last_write_utc,
                word_count = excluded.word_count,
                properties_json = excluded.properties_json,
                tags_json = excluded.tags_json;
            """;
        cmd.Parameters.AddWithValue("$path", note.RelativePath);
        cmd.Parameters.AddWithValue("$title", note.Title);
        cmd.Parameters.AddWithValue("$folder", note.FolderPath);
        cmd.Parameters.AddWithValue("$sha", note.ContentSha256);
        cmd.Parameters.AddWithValue("$utc", note.LastWriteTimeUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$words", note.WordCount);
        cmd.Parameters.AddWithValue("$props", JsonSerializer.Serialize(note.Properties));
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(note.Tags));
        cmd.ExecuteNonQuery();
    }

    private void EnsureStarterVaultFilesOnDisk()
    {
        var mdFiles = Directory.EnumerateFiles(VaultRootPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".rhyolite" + Path.DirectorySeparatorChar))
            .ToList();

        if (mdFiles.Count == 0)
        {
            var archDir = Path.Combine(VaultRootPath, "01 — Architecture");
            var dailyDir = Path.Combine(VaultRootPath, "02 — Daily Notes");
            var studyDir = Path.Combine(VaultRootPath, "03 — Study & Research");
            Directory.CreateDirectory(archDir);
            Directory.CreateDirectory(dailyDir);
            Directory.CreateDirectory(studyDir);

            File.WriteAllText(Path.Combine(archDir, "Storage Architecture.md"), """
                ---
                type: Architecture ADR
                icon: 🏛️
                collection: Core Architecture
                status: Shipped
                subsystem: Rhyolite.Core
                priority: P0 Critical
                date: 2026-11-03
                pinned: true
                tags: [architecture, local-first, sqlite-wal, apple-design]
                ---
                # Hybrid Storage Engine: Plain Markdown + SQLite Sidecar

                Rhyolite keeps plain `.md` files on disk as the canonical source of truth while pairing them with an in-memory AST delta index and a **SQLite WAL sidecar cache** (`.rhyolite/cache.db`). It feeds directly into [[Avalonia Rendering Pipeline]], [[Apple Design Tokens]], and [[MCP and GraphQL P2P Mesh]].

                > [!IMPORTANT] Zero-Latency Typing Pipeline
                > Keystrokes mutate the in-memory rope buffer in < 0.05 ms. A background channel debounces disk flushes (400 ms) and updates only the modified note's edges in `.rhyolite/cache.db`.

                ## Implementation Milestones
                - [x] Configure plain `.md` atomic writer with temp-file swap 📅 2026-11-03 #architecture
                - [x] Enable SQLite Write-Ahead Logging (WAL) sidecar index for instant cold boots 📅 2026-11-03 #sqlite-wal
                - [ ] Benchmark 10,000-note graph PageRank & [[FSRS Spaced Repetition]] sync ⏫ 📅 2026-11-05 #benchmark

                ## Embedded Flashcards
                Q: Why does Rhyolite pair plain .md files with a SQLite WAL sidecar cache instead of storing note bodies inside SQLite?
                A: Plain .md files preserve zero lock-in for Git/VS Code/iCloud, while the SQLite WAL sidecar cache provides < 50 ms cold startup and instant FTS/graph indexing.
                """);

            File.WriteAllText(Path.Combine(archDir, "Avalonia Rendering Pipeline.md"), """
                ---
                type: Architecture ADR
                icon: 🏛️
                collection: Core Architecture
                status: Shipped
                subsystem: Rhyolite.UI
                priority: P0 Critical
                date: 2026-11-03
                tags: [avalonia, skia, cross-platform, architecture]
                ---
                # Avalonia 11 Skia Rendering Pipeline

                Built on **C# 14 / .NET 10** and **Avalonia UI**, Rhyolite renders a unified hardware-accelerated interface across **Windows, macOS, Linux, iOS, and Android**.

                It consumes incremental graph deltas from [[Storage Architecture]] and applies the [[Apple Design Tokens]] and [[Mobile HIG Adaptive Shell]].

                - [x] Implement 60fps Fruchterman-Reingold force-directed graph canvas 📅 2026-11-03 #skia
                - [x] Implement JSON Canvas 1.0 spatial workspace control 📅 2026-11-03 #canvas
                """);

            File.WriteAllText(Path.Combine(archDir, "Apple Design Tokens.md"), """
                ---
                type: Architecture ADR
                icon: 🏛️
                collection: Core Architecture
                status: Shipped
                subsystem: Rhyolite.UI
                priority: P0 Critical
                date: 2026-11-03
                pinned: true
                tags: [apple-design, open-design-agent-kit, ui]
                ---
                # Apple Design System (open-design-agent-kit)

                Rhyolite implements the exact color, typography, radius, and elevation tokens from `feimacode/open-design-agent-kit` (`claude-design-systems/apple` and `apple-music-ios`).

                > [!TIP] Binary Canvas Cadence
                > Light Mode alternates pure `#FFFFFF` canvases with `#F5F5F7` secondary surfaces and `#0071E3` Apple Action Blue. Dark Mode uses True Black `#000000` with `#1C1C1E` -> `#272729` graphite surface stepping.

                Connected subsystems: [[Avalonia Rendering Pipeline]] and [[Mobile HIG Adaptive Shell]].

                Apple Primary Action Hex :: #0071E3 (Apple Action Blue) with #0066CC inline links in light mode and #2997FF in dark mode.
                """);

            File.WriteAllText(Path.Combine(archDir, "MCP and GraphQL P2P Mesh.md"), """
                ---
                type: Architecture ADR
                icon: 🔌
                collection: Core Architecture
                status: In Progress
                subsystem: Rhyolite.Core.Protocol
                priority: P0 Critical
                date: 2026-11-03
                pinned: true
                tags: [mcp, graphql, p2p, auth, oauth, architecture]
                ---
                # High-Speed MCP + GraphQL + Peer-to-Peer Sync & Identity Engine

                Rhyolite exposes a dual **Model Context Protocol (MCP)** JSON-RPC 2.0 server for AI Agent harnesses and a **GraphQL + HLC Merkle P2P Mesh** for peer Rhyolite installations, backed by [[Storage Architecture]].

                > [!NOTE] RBAC & Pluggable OAuth/SSO
                > Supports local Email + Account authentication (`PBKDF2-SHA512` + `HMAC-SHA256` tokens) with granular roles (`VaultOwner`, `Editor`, `AiAgentReadWrite`, `TrustedPeerNode`) and pluggable OAuth2/OIDC adapters for **Enterprise SSO**, **Google**, **GitHub**, **Microsoft**, and **Apple**.

                - [x] Implement JSON-RPC 2.0 MCP tools for reading, writing, patching, and graph querying 📅 2026-11-03 #mcp
                - [x] Implement GraphQL schema & Merkle-tree HLC peer sync engine 📅 2026-11-03 #graphql #p2p
                - [ ] Wire external OIDC discovery endpoints for enterprise SSO deployment 🔼 📅 2026-11-08 #auth
                """);

            File.WriteAllText(Path.Combine(studyDir, "FSRS Spaced Repetition.md"), """
                ---
                type: Study Card
                icon: 🧠
                collection: Spaced Repetition
                status: In Progress
                subsystem: Rhyolite.Study
                priority: P1 High
                date: 2026-11-03
                tags: [fsrs, flashcards, study, expansion]
                ---
                # Native FSRS v4 Spaced Repetition Studio

                Unlike Obsidian (which requires external plugins for flashcards), Rhyolite natively parses `Q:/A:`, `::`, and `{{c1::cloze}}` cards from any note in [[Storage Architecture]] and schedules reviews using the **FSRS v4** memory stability model.

                {{c1::Hybrid Logical Clocks (HLC)}} combine physical wall-clock time with a logical counter for deterministic P2P conflict resolution in [[MCP and GraphQL P2P Mesh]].
                """);

            File.WriteAllText(Path.Combine(archDir, "Mobile HIG Adaptive Shell.md"), """
                ---
                type: Architecture ADR
                icon: 📱
                collection: Core Architecture
                status: Planned
                subsystem: Rhyolite.iOS / Android
                priority: P1 High
                date: 2026-11-04
                tags: [mobile, ios, android, apple-design]
                ---
                # Cross-Platform Mobile & Tablet Adaptive Shell

                Targets `net10.0-ios` and `net10.0-android` using [[Apple Design Tokens]] (`apple-music-ios`), featuring 44pt minimum touch targets, `.regularMaterial` bottom navigation bars, and instant Quick Capture into [[2026-11-03]].
                """);

            File.WriteAllText(Path.Combine(dailyDir, "2026-11-03.md"), """
                ---
                type: Daily Note
                icon: 📅
                collection: Daily Journal
                status: Active
                subsystem: Daily Note
                priority: P1 High
                date: 2026-11-03
                tags: [daily, sprint]
                ---
                # Daily Engineering Log — 2026-11-03

                Completed architectural verification of [[Storage Architecture]], [[Apple Design Tokens]], and [[MCP and GraphQL P2P Mesh]].

                - [x] Finalize 3-tier plain `.md` + SQLite WAL sidecar architecture 📅 2026-11-03 #daily
                - [ ] Review 12 due flashcards in [[FSRS Spaced Repetition]] 📅 2026-11-03 #study
                """);

            var defaultCanvas = JsonCanvasEngine.CreateDefaultArchitectureCanvas();
            File.WriteAllText(_canvasFilePath, JsonCanvasEngine.Serialize(defaultCanvas), Encoding.UTF8);
        }

        // Seed nested subfolder & Capacities Object Studio starter notes if default vault has < 10 notes
        bool isStarterVault = File.Exists(Path.Combine(VaultRootPath, "01 — Architecture", "Storage Architecture.md"));
        if (mdFiles.Count == 0 || (isStarterVault && mdFiles.Count < 10))
        {
            var storageSubdir = Path.Combine(VaultRootPath, "01 — Architecture", "Storage");
            var protocolsSubdir = Path.Combine(VaultRootPath, "01 — Architecture", "Protocols");
            var projectsSubdir = Path.Combine(VaultRootPath, "04 — Capacities Studio", "Projects");
            var meetingsSubdir = Path.Combine(VaultRootPath, "04 — Capacities Studio", "Meetings");

            Directory.CreateDirectory(storageSubdir);
            Directory.CreateDirectory(protocolsSubdir);
            Directory.CreateDirectory(projectsSubdir);
            Directory.CreateDirectory(meetingsSubdir);

            string walNotePath = Path.Combine(storageSubdir, "SQLite WAL Indexer.md");
            if (!File.Exists(walNotePath))
            {
                File.WriteAllText(walNotePath, """
                    ---
                    type: Architecture ADR
                    icon: 💾
                    cover: #10B981
                    collection: Core Architecture
                    status: Shipped
                    subsystem: Rhyolite.Core.Storage
                    priority: P0 Critical
                    date: 2026-11-04
                    tags: [sqlite-wal, storage,subfolder, architecture]
                    ---
                    # SQLite WAL Sidecar Indexer & Folder Metadata Tree

                    Nested under `01 — Architecture/Storage`, this subsystem indexes [[Storage Architecture]] notes, FSRS v4 reviews, and hierarchical folder/subfolder emoji metadata in `< 0.2 ms`.

                    - [x] Persist hierarchical folder icons and expansion state in `folder_metadata` 📅 2026-11-04 #sqlite-wal
                    - [x] Support Capacities typed object schemas in `capacities_object_types` 📅 2026-11-04 #capacities
                    """);
            }

            string launchProjectPath = Path.Combine(projectsSubdir, "Rhyolite 1.0 Launch.md");
            if (!File.Exists(launchProjectPath))
            {
                File.WriteAllText(launchProjectPath, """
                    ---
                    type: Project
                    icon: 🚀
                    cover: #8B5CF6
                    collection: Launch Roadmap
                    status: In Progress
                    subsystem: Rhyolite.Studio
                    priority: P0 Critical
                    date: 2026-11-04
                    pinned: true
                    tags: [capacities, project, launch, roadmap]
                    ---
                    # Rhyolite 1.0 Launch — Capacities Object Studio & Obsidian Bases

                    Unifies **Obsidian hierarchical folders/subfolders** (with custom emoji/color badges) and **Capacities Object Studio** (`Project`, `Meeting`, `Architecture ADR`, `Daily Note`, `Study Card`, `Person`) on top of [[Storage Architecture]] and [[Avalonia Rendering Pipeline]].

                    - [x] Deliver Table, Kanban, Gallery Wall, and Calendar Object Studio views 📅 2026-11-04 #capacities
                    - [ ] Publish cross-platform release artifacts for macOS, Windows, Linux, iOS, and Android 📅 2026-11-10 #launch
                    """);
            }

            string meetingNotePath = Path.Combine(meetingsSubdir, "Architecture Design Review.md");
            if (!File.Exists(meetingNotePath))
            {
                File.WriteAllText(meetingNotePath, """
                    ---
                    type: Meeting
                    icon: 🤝
                    cover: #EC4899
                    collection: Launch Roadmap
                    status: Active
                    subsystem: Rhyolite.Core
                    priority: P1 High
                    date: 2026-11-04
                    tags: [capacities, meeting, review, design]
                    ---
                    # Architecture Design Review — 9 Design Systems & Object Studio

                    Reviewed [[Apple Design Tokens]], [[MCP and GraphQL P2P Mesh]], and [[Rhyolite 1.0 Launch]] across all 9 `open-design-agent-kit` design systems.

                    - [x] Approve hierarchical subfolder tree and emoji badge picker 📅 2026-11-04 #design
                    - [x] Verify Capacities Gallery Wall and Calendar timeline projections 📅 2026-11-04 #capacities
                    """);
            }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
