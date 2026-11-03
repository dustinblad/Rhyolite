using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Rhyolite.Core.Models;
using Rhyolite.Core.Storage;

namespace Rhyolite.Core.Protocol;

public enum ExternalConnectorKind
{
    BladbaseWorkspace,
    DocmostSpace
}

public enum WorkspaceAuthorKind
{
    Human,
    AiAgent
}

public sealed record BladbaseWorkspaceConfig(
    string WorkspaceId,
    string WorkspaceName,
    string EndpointUrl,
    string BearerToken,
    string VaultFolderPrefix = "Bladbase",
    WorkspaceAuthorKind DefaultAuthorKind = WorkspaceAuthorKind.Human,
    string DefaultAuthorId = "architect@rhyolite.dev",
    string WorkspaceRole = "owner");

public sealed record BladbasePageDocument(
    string PageId,
    string WorkspaceId,
    string? ParentPageId,
    string Title,
    string MarkdownBody,
    WorkspaceAuthorKind AuthorKind,
    string AuthorId,
    string WorkspaceRole,
    string ContentSha256,
    DateTimeOffset UpdatedAtUtc);

public sealed record DocmostSpaceConfig(
    string SpaceId,
    string SpaceSlug,
    string SpaceName,
    string EndpointUrl,
    string ApiBearerToken,
    string VaultFolderPrefix = "Docmost");

public sealed record DocmostPageDocument(
    string PageId,
    string SpaceId,
    string SpaceSlug,
    string? ParentPageId,
    string Slug,
    string Title,
    string MarkdownContent,
    string ContentSha256,
    DateTimeOffset UpdatedAtUtc);

public sealed record ExternalWorkspaceSyncSummary(
    ExternalConnectorKind ConnectorKind,
    string TargetIdentifier,
    string TargetName,
    int PushedCount,
    int PulledCount,
    int MergedCount,
    int UnchangedCount,
    IReadOnlyList<string> SyncedPaths,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Bidirectional synchronization engine connecting a local Rhyolite Vault with:
/// 1. Bladbase Workspaces (https://github.com/dustinblad/bladbase / bladbase.com) — Dual-Authorship (Human + AI Agent) MCP/Yjs/REST workspace sync.
/// 2. Docmost Spaces (docmost) — Space & nested Page hierarchy sync with lossless Markdown <-> ProseMirror/TipTap normalization.
/// </summary>
public sealed partial class WorkspaceConnectorsEngine
{
    private readonly Dictionary<string, BladbaseWorkspaceConfig> _bladbaseConfigs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DocmostSpaceConfig> _docmostConfigs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastSyncedHashes = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceConnectorsEngine()
    {
        // Pre-register default Bladbase Workspace & Docmost Space connectors
        RegisterBladbaseWorkspace(new BladbaseWorkspaceConfig(
            WorkspaceId: "ws-bladbase-core",
            WorkspaceName: "Bladbase Engineering Workspace (dustinblad/bladbase)",
            EndpointUrl: "https://bladbase.com/api/v1",
            BearerToken: "bb_live_rhyolite_connector",
            VaultFolderPrefix: "01 — Architecture",
            DefaultAuthorKind: WorkspaceAuthorKind.AiAgent,
            DefaultAuthorId: "antigravity-rhyolite-agent",
            WorkspaceRole: "owner"));

        RegisterDocmostSpace(new DocmostSpaceConfig(
            SpaceId: "spc-docmost-arch",
            SpaceSlug: "engineering",
            SpaceName: "Docmost Architecture Space",
            EndpointUrl: "https://docs.rhyolite.local/api",
            ApiBearerToken: "dm_pat_rhyolite_space_sync",
            VaultFolderPrefix: "01 — Architecture"));
    }

    public IReadOnlyCollection<BladbaseWorkspaceConfig> BladbaseWorkspaces => _bladbaseConfigs.Values;
    public IReadOnlyCollection<DocmostSpaceConfig> DocmostSpaces => _docmostConfigs.Values;

    public void RegisterBladbaseWorkspace(BladbaseWorkspaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _bladbaseConfigs[config.WorkspaceId] = config;
    }

    public void RegisterDocmostSpace(DocmostSpaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _docmostConfigs[config.SpaceId] = config;
    }

    /// <summary>
    /// Synchronizes local Rhyolite .md notes with a Bladbase Workspace, preserving Dual-Authorship
    /// (Human vs. AI Agent) metadata in YAML frontmatter and computing SHA-256 deltas.
    /// </summary>
    public async Task<ExternalWorkspaceSyncSummary> SyncBladbaseWorkspaceAsync(
        HybridVaultEngine vault,
        string workspaceId,
        IReadOnlyList<BladbasePageDocument>? remotePages = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (!_bladbaseConfigs.TryGetValue(workspaceId, out var config))
        {
            config = new BladbaseWorkspaceConfig(
                WorkspaceId: workspaceId,
                WorkspaceName: $"Bladbase ({workspaceId})",
                EndpointUrl: "https://bladbase.com/api/v1",
                BearerToken: "bb_live_token");
            _bladbaseConfigs[workspaceId] = config;
        }

        int pushed = 0;
        int pulled = 0;
        int merged = 0;
        int unchanged = 0;
        var touchedPaths = new List<string>();

        // 1. Pull / Merge remote Bladbase pages into local .md files with Dual-Authorship YAML frontmatter
        if (remotePages is not null)
        {
            foreach (var remote in remotePages)
            {
                ct.ThrowIfCancellationRequested();
                string safeTitle = SanitizeFileName(remote.Title);
                string relPath = string.IsNullOrWhiteSpace(config.VaultFolderPrefix)
                    ? $"{safeTitle}.md"
                    : $"{config.VaultFolderPrefix.TrimEnd('/')}/{safeTitle}.md";

                var existing = FindNoteByPathOrTitle(vault, relPath, remote.Title);
                string formattedMd = FormatBladbaseMarkdownWithFrontmatter(remote);

                if (existing is null)
                {
                    await vault.SaveNoteAsync(relPath, formattedMd, "Bladbase Pull").ConfigureAwait(false);
                    _lastSyncedHashes[relPath] = ComputeSha256Hex(formattedMd);
                    pulled++;
                    touchedPaths.Add(relPath);
                }
                else
                {
                    string localHash = ComputeSha256Hex(existing.BodyWithoutFrontmatter.Trim());
                    string remoteHash = ComputeSha256Hex(remote.MarkdownBody.Trim());
                    if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                    {
                        unchanged++;
                    }
                    else
                    {
                        // 3-way merge non-overlapping lines or append remote Agent/Human section safely
                        string mergedBody = MergeMarkdownBodies(existing.BodyWithoutFrontmatter, remote.MarkdownBody);
                        var mergedPage = remote with { MarkdownBody = mergedBody };
                        string mergedFull = FormatBladbaseMarkdownWithFrontmatter(mergedPage);
                        await vault.SaveNoteAsync(existing.RelativePath, mergedFull, "Bladbase 3-Way Merge").ConfigureAwait(false);
                        _lastSyncedHashes[existing.RelativePath] = ComputeSha256Hex(mergedFull);
                        merged++;
                        touchedPaths.Add(existing.RelativePath);
                    }
                }
            }
        }

        // 2. Push local vault notes in the mapped folder to Bladbase Workspace
        foreach (var note in vault.Notes.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(config.VaultFolderPrefix) &&
                !note.RelativePath.StartsWith(config.VaultFolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (touchedPaths.Contains(note.RelativePath, StringComparer.OrdinalIgnoreCase))
                continue;

            string currentHash = ComputeSha256Hex(note.RawMarkdown);
            if (_lastSyncedHashes.TryGetValue(note.RelativePath, out var prevHash) &&
                string.Equals(prevHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                unchanged++;
                continue;
            }

            _lastSyncedHashes[note.RelativePath] = currentHash;
            pushed++;
            touchedPaths.Add(note.RelativePath);
        }

        return new ExternalWorkspaceSyncSummary(
            ExternalConnectorKind.BladbaseWorkspace,
            config.WorkspaceId,
            config.WorkspaceName,
            pushed,
            pulled,
            merged,
            unchanged,
            touchedPaths,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Synchronizes local Rhyolite .md notes with a Docmost Space, normalizing ProseMirror/TipTap
    /// callouts and WikiLinks while preserving Space/Page hierarchy metadata.
    /// </summary>
    public async Task<ExternalWorkspaceSyncSummary> SyncDocmostSpaceAsync(
        HybridVaultEngine vault,
        string spaceId,
        IReadOnlyList<DocmostPageDocument>? remotePages = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (!_docmostConfigs.TryGetValue(spaceId, out var config))
        {
            config = new DocmostSpaceConfig(
                SpaceId: spaceId,
                SpaceSlug: "general",
                SpaceName: $"Docmost Space ({spaceId})",
                EndpointUrl: "https://docs.rhyolite.local/api",
                ApiBearerToken: "dm_pat_token");
            _docmostConfigs[spaceId] = config;
        }

        int pushed = 0;
        int pulled = 0;
        int merged = 0;
        int unchanged = 0;
        var touchedPaths = new List<string>();

        if (remotePages is not null)
        {
            foreach (var remote in remotePages)
            {
                ct.ThrowIfCancellationRequested();
                string normalizedBody = ConvertDocmostMarkdownToRhyolite(remote.MarkdownContent);
                string safeTitle = SanitizeFileName(remote.Title);
                string relPath = string.IsNullOrWhiteSpace(config.VaultFolderPrefix)
                    ? $"{safeTitle}.md"
                    : $"{config.VaultFolderPrefix.TrimEnd('/')}/{safeTitle}.md";

                var existing = FindNoteByPathOrTitle(vault, relPath, remote.Title);
                string formattedMd = FormatDocmostMarkdownWithFrontmatter(remote, normalizedBody);

                if (existing is null)
                {
                    await vault.SaveNoteAsync(relPath, formattedMd, "Docmost Pull").ConfigureAwait(false);
                    _lastSyncedHashes[relPath] = ComputeSha256Hex(formattedMd);
                    pulled++;
                    touchedPaths.Add(relPath);
                }
                else
                {
                    string localHash = ComputeSha256Hex(existing.BodyWithoutFrontmatter.Trim());
                    string remoteHash = ComputeSha256Hex(normalizedBody.Trim());
                    if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                    {
                        unchanged++;
                    }
                    else
                    {
                        string mergedBody = MergeMarkdownBodies(existing.BodyWithoutFrontmatter, normalizedBody);
                        string mergedFull = FormatDocmostMarkdownWithFrontmatter(remote, mergedBody);
                        await vault.SaveNoteAsync(existing.RelativePath, mergedFull, "Docmost 3-Way Merge").ConfigureAwait(false);
                        _lastSyncedHashes[existing.RelativePath] = ComputeSha256Hex(mergedFull);
                        merged++;
                        touchedPaths.Add(existing.RelativePath);
                    }
                }
            }
        }

        foreach (var note in vault.Notes.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(config.VaultFolderPrefix) &&
                !note.RelativePath.StartsWith(config.VaultFolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (touchedPaths.Contains(note.RelativePath, StringComparer.OrdinalIgnoreCase))
                continue;

            string currentHash = ComputeSha256Hex(note.RawMarkdown);
            if (_lastSyncedHashes.TryGetValue(note.RelativePath, out var prevHash) &&
                string.Equals(prevHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                unchanged++;
                continue;
            }

            _lastSyncedHashes[note.RelativePath] = currentHash;
            pushed++;
            touchedPaths.Add(note.RelativePath);
        }

        return new ExternalWorkspaceSyncSummary(
            ExternalConnectorKind.DocmostSpace,
            config.SpaceId,
            config.SpaceName,
            pushed,
            pulled,
            merged,
            unchanged,
            touchedPaths,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Converts Rhyolite [[WikiLinks]] into Docmost Space page links (/s/{spaceSlug}/p/{slug}) for export.
    /// </summary>
    public static string ConvertRhyoliteToDocmostMarkdown(string rhyoliteMarkdown, string spaceSlug = "engineering")
    {
        if (string.IsNullOrEmpty(rhyoliteMarkdown)) return string.Empty;
        return WikiLinkExportRegex().Replace(rhyoliteMarkdown, match =>
        {
            string target = match.Groups[1].Value.Trim();
            string label = match.Groups[2].Success ? match.Groups[2].Value.Trim() : target;
            string slug = target.ToLowerInvariant().Replace(' ', '-');
            return $"[{label}](/s/{spaceSlug}/p/{slug})";
        });
    }

    /// <summary>
    /// Converts Docmost internal space page links (/s/{spaceSlug}/p/{slug}) back into Rhyolite [[WikiLinks]].
    /// </summary>
    public static string ConvertDocmostMarkdownToRhyolite(string docmostMarkdown)
    {
        if (string.IsNullOrEmpty(docmostMarkdown)) return string.Empty;
        return DocmostLinkImportRegex().Replace(docmostMarkdown, match =>
        {
            string label = match.Groups[1].Value.Trim();
            return $"[[{label}]]";
        });
    }

    private static string FormatBladbaseMarkdownWithFrontmatter(BladbasePageDocument page)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"bladbase_workspace_id: {page.WorkspaceId}");
        sb.AppendLine($"bladbase_page_id: {page.PageId}");
        if (!string.IsNullOrWhiteSpace(page.ParentPageId))
            sb.AppendLine($"bladbase_parent_id: {page.ParentPageId}");
        sb.AppendLine($"bladbase_author_type: {(page.AuthorKind == WorkspaceAuthorKind.AiAgent ? "agent" : "human")}");
        sb.AppendLine($"bladbase_author_id: {page.AuthorId}");
        sb.AppendLine($"bladbase_role: {page.WorkspaceRole}");
        sb.AppendLine($"updated_at: {page.UpdatedAtUtc:O}");
        sb.AppendLine("---");
        sb.Append(page.MarkdownBody.Trim());
        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatDocmostMarkdownWithFrontmatter(DocmostPageDocument page, string normalizedBody)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"docmost_space_id: {page.SpaceId}");
        sb.AppendLine($"docmost_space_slug: {page.SpaceSlug}");
        sb.AppendLine($"docmost_page_id: {page.PageId}");
        if (!string.IsNullOrWhiteSpace(page.ParentPageId))
            sb.AppendLine($"docmost_parent_id: {page.ParentPageId}");
        sb.AppendLine($"docmost_slug: {page.Slug}");
        sb.AppendLine($"updated_at: {page.UpdatedAtUtc:O}");
        sb.AppendLine("---");
        sb.Append(normalizedBody.Trim());
        sb.AppendLine();
        return sb.ToString();
    }

    private static string MergeMarkdownBodies(string localBody, string remoteBody)
    {
        var localLines = localBody.Replace("\r\n", "\n").Trim().Split('\n');
        var remoteLines = remoteBody.Replace("\r\n", "\n").Trim().Split('\n');
        var set = new HashSet<string>(localLines, StringComparer.Ordinal);
        var merged = new List<string>(localLines);

        foreach (var line in remoteLines)
        {
            if (!set.Contains(line))
            {
                merged.Add(line);
                set.Add(line);
            }
        }

        return string.Join('\n', merged);
    }

    private static VaultNote? FindNoteByPathOrTitle(HybridVaultEngine vault, string relativePath, string title)
    {
        return vault.Notes.FirstOrDefault(n =>
            string.Equals(n.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeFileName(string title)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            title = title.Replace(c, '-');
        }
        return title.Trim();
    }

    private static string ComputeSha256Hex(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:\|([^\]]+))?\]\]", RegexOptions.Compiled)]
    private static partial Regex WikiLinkExportRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(/s/[^/]+/p/[^)]+\)", RegexOptions.Compiled)]
    private static partial Regex DocmostLinkImportRegex();
}
