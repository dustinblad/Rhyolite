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
            """;
        cmd.ExecuteNonQuery();
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
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath)
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

        if (!_notesByRelativePath.TryGetValue(normalizedRel, out var note))
        {
            note = new VaultNote
            {
                Id = normalizedRel,
                RelativePath = normalizedRel,
                FullPath = fullPath,
                Title = Path.GetFileNameWithoutExtension(fullPath),
                FolderPath = Path.GetDirectoryName(normalizedRel)?.Replace('\\', '/') ?? string.Empty,
                RawMarkdown = updatedMarkdown
            };
            _notesByRelativePath[normalizedRel] = note;
        }
        else
        {
            note.RawMarkdown = updatedMarkdown;
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
        var existing = Directory.EnumerateFiles(VaultRootPath, "*.md", SearchOption.AllDirectories).Any();
        if (existing) return;

        var archDir = Path.Combine(VaultRootPath, "01 — Architecture");
        var dailyDir = Path.Combine(VaultRootPath, "02 — Daily Notes");
        var studyDir = Path.Combine(VaultRootPath, "03 — Study & Research");
        Directory.CreateDirectory(archDir);
        Directory.CreateDirectory(dailyDir);
        Directory.CreateDirectory(studyDir);

        File.WriteAllText(Path.Combine(archDir, "Storage Architecture.md"), """
            ---
            status: Shipped
            subsystem: Rhyolite.Core
            priority: P0 Critical
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
            status: Shipped
            subsystem: Rhyolite.UI
            priority: P0 Critical
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
            status: Shipped
            subsystem: Rhyolite.UI
            priority: P0 Critical
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
            status: In Progress
            subsystem: Rhyolite.Core.Protocol
            priority: P0 Critical
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
            status: In Progress
            subsystem: Rhyolite.Study
            priority: P1 High
            tags: [fsrs, flashcards, study, expansion]
            ---
            # Native FSRS v4 Spaced Repetition Studio

            Unlike Obsidian (which requires external plugins for flashcards), Rhyolite natively parses `Q:/A:`, `::`, and `{{c1::cloze}}` cards from any note in [[Storage Architecture]] and schedules reviews using the **FSRS v4** memory stability model.

            {{c1::Hybrid Logical Clocks (HLC)}} combine physical wall-clock time with a logical counter for deterministic P2P conflict resolution in [[MCP and GraphQL P2P Mesh]].
            """);

        File.WriteAllText(Path.Combine(archDir, "Mobile HIG Adaptive Shell.md"), """
            ---
            status: Planned
            subsystem: Rhyolite.iOS / Android
            priority: P1 High
            tags: [mobile, ios, android, apple-design]
            ---
            # Cross-Platform Mobile & Tablet Adaptive Shell

            Targets `net10.0-ios` and `net10.0-android` using [[Apple Design Tokens]] (`apple-music-ios`), featuring 44pt minimum touch targets, `.regularMaterial` bottom navigation bars, and instant Quick Capture into [[2026-11-03]].
            """);

        File.WriteAllText(Path.Combine(dailyDir, "2026-11-03.md"), """
            ---
            status: Active
            subsystem: Daily Note
            priority: P1 High
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
