using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Rhyolite.Core.Protocol;

/// <summary>
/// 128-bit Hybrid Logical Clock (HLC) timestamp combining physical UTC milliseconds,
/// a monotonic logical counter, and a deterministic node identifier.
/// </summary>
public readonly record struct HlcTimestamp(
    long PhysicalTimeUnixMs,
    uint LogicalCounter,
    string NodeId) : IComparable<HlcTimestamp>
{
    public static HlcTimestamp Zero(string nodeId = "node-local") =>
        new(0L, 0u, nodeId);

    public static HlcTimestamp TickLocal(HlcTimestamp last, string localNodeId, long? nowUnixMs = null)
    {
        var physicalNow = nowUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (physicalNow > last.PhysicalTimeUnixMs)
        {
            return new HlcTimestamp(physicalNow, 0u, localNodeId);
        }

        return new HlcTimestamp(last.PhysicalTimeUnixMs, last.LogicalCounter + 1u, localNodeId);
    }

    public static HlcTimestamp TickReceive(
        HlcTimestamp localLast,
        HlcTimestamp remote,
        string localNodeId,
        long? nowUnixMs = null)
    {
        var physicalNow = nowUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var maxPhysical = Math.Max(physicalNow, Math.Max(localLast.PhysicalTimeUnixMs, remote.PhysicalTimeUnixMs));

        uint nextLogical;
        if (maxPhysical == localLast.PhysicalTimeUnixMs && maxPhysical == remote.PhysicalTimeUnixMs)
        {
            nextLogical = Math.Max(localLast.LogicalCounter, remote.LogicalCounter) + 1u;
        }
        else if (maxPhysical == localLast.PhysicalTimeUnixMs)
        {
            nextLogical = localLast.LogicalCounter + 1u;
        }
        else if (maxPhysical == remote.PhysicalTimeUnixMs)
        {
            nextLogical = remote.LogicalCounter + 1u;
        }
        else
        {
            nextLogical = 0u;
        }

        return new HlcTimestamp(maxPhysical, nextLogical, localNodeId);
    }

    public int CompareTo(HlcTimestamp other)
    {
        var physCmp = PhysicalTimeUnixMs.CompareTo(other.PhysicalTimeUnixMs);
        if (physCmp != 0)
        {
            return physCmp;
        }

        var logCmp = LogicalCounter.CompareTo(other.LogicalCounter);
        if (logCmp != 0)
        {
            return logCmp;
        }

        return string.Compare(NodeId, other.NodeId, StringComparison.Ordinal);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{PhysicalTimeUnixMs:D13}:{LogicalCounter:D4}@{NodeId}");

    public static HlcTimestamp Parse(string value)
    {
        if (!TryParse(value, out var parsed))
        {
            throw new FormatException($"Invalid HLC timestamp string: '{value}'.");
        }
        return parsed;
    }

    public static bool TryParse(string? value, out HlcTimestamp timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var colonIdx = value.IndexOf(':');
        var atIdx = value.IndexOf('@');
        if (colonIdx <= 0 || atIdx <= colonIdx + 1 || atIdx >= value.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(value.AsSpan(0, colonIdx), NumberStyles.Integer, CultureInfo.InvariantCulture, out var phys))
        {
            return false;
        }

        if (!uint.TryParse(value.AsSpan(colonIdx + 1, atIdx - colonIdx - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var logical))
        {
            return false;
        }

        var nodeId = value[(atIdx + 1)..];
        timestamp = new HlcTimestamp(phys, logical, nodeId);
        return true;
    }
}

/// <summary>
/// Connection and synchronization status of a peer Rhyolite installation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeerNodeStatus>))]
public enum PeerNodeStatus
{
    Discovered,
    Authenticated,
    Synchronizing,
    InSync,
    ConflictPending,
    Disconnected
}

/// <summary>
/// Represents a discovered or paired peer Rhyolite node in the P2P mesh.
/// </summary>
public sealed record PeerNode(
    string NodeId,
    string DisplayName,
    string Endpoint,
    HlcTimestamp LastKnownHlc,
    string LastRootMerkleHash,
    PeerNodeStatus Status,
    DateTimeOffset LastSeenUtc);

/// <summary>
/// Immutable snapshot of a Markdown note in the Rhyolite vault, including extracted frontmatter,
/// outgoing wikilinks, #tags, SHA-256 content hash, and HLC causality vector.
/// </summary>
public sealed partial record VaultNoteSnapshot(
    string Path,
    string Title,
    string Content,
    string Sha256,
    HlcTimestamp Hlc,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Frontmatter,
    IReadOnlyList<string> OutgoingLinks,
    bool IsDeleted = false)
{
    public static VaultNoteSnapshot FromMarkdown(
        string path,
        string content,
        HlcTimestamp hlc,
        IEnumerable<string>? extraTags = null,
        bool isDeleted = false)
    {
        var normalizedPath = NormalizeVaultPath(path);
        var normalizedContent = content.Replace("\r\n", "\n");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedContent)));

        var frontmatter = ExtractFrontmatter(normalizedContent, out var bodyWithoutFrontmatter);
        var tags = ExtractTags(bodyWithoutFrontmatter, frontmatter, extraTags);
        var outgoingLinks = ExtractWikilinks(bodyWithoutFrontmatter);
        var title = ExtractTitle(normalizedPath, bodyWithoutFrontmatter, frontmatter);

        return new VaultNoteSnapshot(
            Path: normalizedPath,
            Title: title,
            Content: normalizedContent,
            Sha256: sha256,
            Hlc: hlc,
            Tags: tags,
            Frontmatter: frontmatter,
            OutgoingLinks: outgoingLinks,
            IsDeleted: isDeleted);
    }

    public static string NormalizeVaultPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').Trim().TrimStart('/');
        if (!normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
            !normalized.EndsWith(".canvas", StringComparison.OrdinalIgnoreCase))
        {
            normalized += ".md";
        }
        return normalized;
    }

    private static Dictionary<string, string> ExtractFrontmatter(string markdown, out string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        body = markdown;

        if (!markdown.StartsWith("---\n", StringComparison.Ordinal))
        {
            return result;
        }

        var closingIdx = markdown.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (closingIdx < 0)
        {
            return result;
        }

        var yamlBlock = markdown[4..closingIdx];
        body = markdown[(closingIdx + 5)..];

        foreach (var rawLine in yamlBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIdx = rawLine.IndexOf(':');
            if (colonIdx <= 0)
            {
                continue;
            }

            var key = rawLine[..colonIdx].Trim();
            var val = rawLine[(colonIdx + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0)
            {
                result[key] = val;
            }
        }

        return result;
    }

    private static List<string> ExtractTags(
        string body,
        IReadOnlyDictionary<string, string> frontmatter,
        IEnumerable<string>? extraTags)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (extraTags is not null)
        {
            foreach (var tag in extraTags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    set.Add(AccessScope.NormalizeTag(tag));
                }
            }
        }

        if (frontmatter.TryGetValue("tags", out var fmTags))
        {
            var cleaned = fmTags.Trim('[', ']');
            foreach (var part in cleaned.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                set.Add(AccessScope.NormalizeTag(part));
            }
        }

        foreach (Match match in InlineTagRegex().Matches(body))
        {
            set.Add(AccessScope.NormalizeTag(match.Groups[1].Value));
        }

        return [.. set];
    }

    private static List<string> ExtractWikilinks(string body)
    {
        var links = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in WikiLinkRegex().Matches(body))
        {
            var rawTarget = match.Groups[1].Value.Trim();
            var pipeIdx = rawTarget.IndexOf('|');
            if (pipeIdx > 0)
            {
                rawTarget = rawTarget[..pipeIdx].Trim();
            }

            var hashIdx = rawTarget.IndexOf('#');
            if (hashIdx > 0)
            {
                rawTarget = rawTarget[..hashIdx].Trim();
            }

            if (rawTarget.Length > 0)
            {
                links.Add(NormalizeVaultPath(rawTarget));
            }
        }

        return [.. links];
    }

    private static string ExtractTitle(
        string normalizedPath,
        string body,
        IReadOnlyDictionary<string, string> frontmatter)
    {
        if (frontmatter.TryGetValue("title", out var fmTitle) && !string.IsNullOrWhiteSpace(fmTitle))
        {
            return fmTitle;
        }

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }
        }

        var fileName = System.IO.Path.GetFileNameWithoutExtension(normalizedPath);
        return string.IsNullOrWhiteSpace(fileName) ? normalizedPath : fileName;
    }

    [GeneratedRegex(@"(?:^|\s)#([A-Za-z0-9_\-/]+)", RegexOptions.Compiled)]
    private static partial Regex InlineTagRegex();

    [GeneratedRegex(@"\[\[([^\[\]]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex WikiLinkRegex();
}

/// <summary>
/// Spatial canvas node representation for <c>rhyolite.canvas_op</c>.
/// </summary>
public sealed record SpatialCanvasNode(
    string NodeId,
    string Label,
    double X,
    double Y,
    double Width,
    double Height,
    string? LinkedNotePath);

/// <summary>
/// Spatial canvas edge representation connecting two canvas nodes.
/// </summary>
public sealed record SpatialCanvasEdge(
    string EdgeId,
    string FromNodeId,
    string ToNodeId,
    string? Label);

/// <summary>
/// Classification of reconciliation actions during P2P Merkle delta planning.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyncActionType>))]
public enum SyncActionType
{
    NoOp,
    PushToPeer,
    PullFromPeer,
    AutoMerge,
    ConflictBranch
}

/// <summary>
/// Describes the reconciliation action required for a single note path between two peers.
/// </summary>
public sealed record SyncDeltaItem(
    string Path,
    SyncActionType Action,
    HlcTimestamp? LocalHlc,
    HlcTimestamp? RemoteHlc,
    string? LocalSha256,
    string? RemoteSha256,
    string Reason);

/// <summary>
/// Complete synchronization plan produced by <see cref="PeerSyncEngine.ComputeSyncPlan"/>.
/// </summary>
public sealed record SyncPlan(
    string LocalNodeId,
    string RemoteNodeId,
    string LocalMerkleRoot,
    string RemoteMerkleRoot,
    bool IsIdentical,
    IReadOnlyList<SyncDeltaItem> Deltas,
    DateTimeOffset ComputedAtUtc);

/// <summary>
/// Outcome of merging two concurrent note revisions.
/// </summary>
public sealed record NoteMergeOutcome(
    VaultNoteSnapshot WinningNote,
    VaultNoteSnapshot? ConflictBranchNote,
    bool WasAutoMerged,
    bool CreatedConflictBranch);

/// <summary>
/// Peer-to-Peer (P2P) Mesh Synchronization Engine implementing Hybrid Logical Clocks (HLC),
/// binary SHA-256 Merkle Tree state digests, line-level automatic merging, and deterministic conflict branching.
/// </summary>
public sealed class PeerSyncEngine
{
    private readonly Lock _clockLock = new();
    private HlcTimestamp _currentHlc;
    private readonly ConcurrentDictionary<string, VaultNoteSnapshot> _notes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<SpatialCanvasNode>> _canvasNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<SpatialCanvasEdge>> _canvasEdges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PeerNode> _peers = new(StringComparer.Ordinal);

    public PeerSyncEngine(string localNodeId = "rhyolite-node-primary")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localNodeId);
        LocalNodeId = localNodeId.Trim();
        _currentHlc = new HlcTimestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0u, LocalNodeId);
    }

    public string LocalNodeId { get; }

    public HlcTimestamp CurrentHlc
    {
        get
        {
            lock (_clockLock)
            {
                return _currentHlc;
            }
        }
    }

    /// <summary>
    /// Advances the local Hybrid Logical Clock for a local mutation event.
    /// </summary>
    public HlcTimestamp AdvanceClock(long? nowUnixMs = null)
    {
        lock (_clockLock)
        {
            _currentHlc = HlcTimestamp.TickLocal(_currentHlc, LocalNodeId, nowUnixMs);
            return _currentHlc;
        }
    }

    /// <summary>
    /// Updates the local Hybrid Logical Clock upon receiving a message or note from a peer node.
    /// </summary>
    public HlcTimestamp ObserveRemoteClock(HlcTimestamp remoteHlc, long? nowUnixMs = null)
    {
        lock (_clockLock)
        {
            _currentHlc = HlcTimestamp.TickReceive(_currentHlc, remoteHlc, LocalNodeId, nowUnixMs);
            return _currentHlc;
        }
    }

    /// <summary>
    /// Creates or replaces a Markdown note in the vault and advances the local HLC.
    /// </summary>
    public VaultNoteSnapshot UpsertNote(string path, string content, IEnumerable<string>? extraTags = null)
    {
        var hlc = AdvanceClock();
        var snapshot = VaultNoteSnapshot.FromMarkdown(path, content, hlc, extraTags, isDeleted: false);
        _notes[snapshot.Path] = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Surgically patches a specific Markdown heading section while preserving surrounding content and YAML frontmatter.
    /// </summary>
    public VaultNoteSnapshot PatchNoteSection(
        string path,
        string heading,
        string replacementContent,
        bool createIfMissing = true)
    {
        var normalizedPath = VaultNoteSnapshot.NormalizeVaultPath(path);
        var normalizedHeading = heading.Trim();
        if (!normalizedHeading.StartsWith('#'))
        {
            normalizedHeading = $"## {normalizedHeading}";
        }

        var headingLevel = normalizedHeading.TakeWhile(c => c == '#').Count();
        var cleanBody = replacementContent.Replace("\r\n", "\n").Trim();

        if (!_notes.TryGetValue(normalizedPath, out var existing) || existing.IsDeleted)
        {
            if (!createIfMissing)
            {
                throw new KeyNotFoundException($"Note '{normalizedPath}' was not found.");
            }

            var initialMarkdown = $"# {System.IO.Path.GetFileNameWithoutExtension(normalizedPath)}\n\n{normalizedHeading}\n{cleanBody}\n";
            return UpsertNote(normalizedPath, initialMarkdown);
        }

        var lines = existing.Content.Split('\n').ToList();
        var startIdx = -1;
        var endIdx = lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.Equals(trimmed, normalizedHeading, StringComparison.OrdinalIgnoreCase))
            {
                startIdx = i;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    var nextTrimmed = lines[j].Trim();
                    if (nextTrimmed.StartsWith('#'))
                    {
                        var nextLevel = nextTrimmed.TakeWhile(c => c == '#').Count();
                        if (nextLevel <= headingLevel && nextTrimmed.Length > nextLevel && nextTrimmed[nextLevel] == ' ')
                        {
                            endIdx = j;
                            break;
                        }
                    }
                }
                break;
            }
        }

        string updatedMarkdown;
        if (startIdx >= 0)
        {
            var builder = new List<string>(lines.Count + 4);
            builder.AddRange(lines.Take(startIdx + 1));
            builder.Add(cleanBody);
            if (endIdx < lines.Count)
            {
                builder.Add(string.Empty);
                builder.AddRange(lines.Skip(endIdx));
            }
            updatedMarkdown = string.Join('\n', builder);
        }
        else if (createIfMissing)
        {
            updatedMarkdown = $"{existing.Content.TrimEnd()}\n\n{normalizedHeading}\n{cleanBody}\n";
        }
        else
        {
            throw new InvalidOperationException($"Heading '{normalizedHeading}' was not found in '{normalizedPath}'.");
        }

        return UpsertNote(normalizedPath, updatedMarkdown, existing.Tags);
    }

    /// <summary>
    /// Atomically merges key-value pairs into a note's YAML frontmatter block.
    /// </summary>
    public VaultNoteSnapshot UpdateFrontmatter(string path, IReadOnlyDictionary<string, string> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var normalizedPath = VaultNoteSnapshot.NormalizeVaultPath(path);
        if (!_notes.TryGetValue(normalizedPath, out var existing) || existing.IsDeleted)
        {
            throw new KeyNotFoundException($"Note '{normalizedPath}' does not exist.");
        }

        var mergedProps = new Dictionary<string, string>(existing.Frontmatter, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in properties)
        {
            if (!string.IsNullOrWhiteSpace(k))
            {
                mergedProps[k.Trim()] = v;
            }
        }

        var body = existing.Content;
        if (body.StartsWith("---\n", StringComparison.Ordinal))
        {
            var closeIdx = body.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (closeIdx >= 0)
            {
                body = body[(closeIdx + 5)..];
            }
        }

        var sb = new StringBuilder();
        sb.Append("---\n");
        foreach (var (k, v) in mergedProps.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(k).Append(": ").Append(v).Append('\n');
        }
        sb.Append("---\n");
        sb.Append(body.TrimStart('\n'));

        return UpsertNote(normalizedPath, sb.ToString(), existing.Tags);
    }

    /// <summary>
    /// Marks a note as deleted with a tombstone HLC so deletions propagate across P2P peers.
    /// </summary>
    public bool DeleteNote(string path)
    {
        var normalizedPath = VaultNoteSnapshot.NormalizeVaultPath(path);
        if (!_notes.TryGetValue(normalizedPath, out var existing) || existing.IsDeleted)
        {
            return false;
        }

        var hlc = AdvanceClock();
        var tombstone = VaultNoteSnapshot.FromMarkdown(normalizedPath, string.Empty, hlc, existing.Tags, isDeleted: true);
        _notes[normalizedPath] = tombstone;
        return true;
    }

    public VaultNoteSnapshot? GetNote(string path, bool includeDeleted = false)
    {
        var normalizedPath = VaultNoteSnapshot.NormalizeVaultPath(path);
        if (_notes.TryGetValue(normalizedPath, out var note) && (includeDeleted || !note.IsDeleted))
        {
            return note;
        }
        return null;
    }

    public IReadOnlyList<VaultNoteSnapshot> GetAllNotes(bool includeDeleted = false) =>
        [.. _notes.Values
            .Where(n => includeDeleted || !n.IsDeleted)
            .OrderBy(n => n.Path, StringComparer.Ordinal)];

    /// <summary>
    /// Computes all incoming backlinks pointing to <paramref name="targetPath"/>.
    /// </summary>
    public IReadOnlyList<string> GetBacklinks(string targetPath)
    {
        var normalizedTarget = VaultNoteSnapshot.NormalizeVaultPath(targetPath);
        return [.. _notes.Values
            .Where(n => !n.IsDeleted && n.OutgoingLinks.Contains(normalizedTarget, StringComparer.OrdinalIgnoreCase))
            .Select(n => n.Path)
            .OrderBy(p => p, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Mutates a spatial canvas (<c>.canvas</c>) by adding/updating a node or edge.
    /// </summary>
    public (IReadOnlyList<SpatialCanvasNode> Nodes, IReadOnlyList<SpatialCanvasEdge> Edges) ApplyCanvasOperation(
        string canvasPath,
        string operation,
        SpatialCanvasNode? node = null,
        SpatialCanvasEdge? edge = null)
    {
        var normPath = canvasPath.Replace('\\', '/').TrimStart('/');
        var nodes = _canvasNodes.GetOrAdd(normPath, _ => []);
        var edges = _canvasEdges.GetOrAdd(normPath, _ => []);

        lock (nodes)
        {
            switch (operation.ToLowerInvariant())
            {
                case "upsert_node" when node is not null:
                    nodes.RemoveAll(n => string.Equals(n.NodeId, node.NodeId, StringComparison.Ordinal));
                    nodes.Add(node);
                    break;
                case "delete_node" when node is not null:
                    nodes.RemoveAll(n => string.Equals(n.NodeId, node.NodeId, StringComparison.Ordinal));
                    edges.RemoveAll(e =>
                        string.Equals(e.FromNodeId, node.NodeId, StringComparison.Ordinal) ||
                        string.Equals(e.ToNodeId, node.NodeId, StringComparison.Ordinal));
                    break;
                case "upsert_edge" when edge is not null:
                    edges.RemoveAll(e => string.Equals(e.EdgeId, edge.EdgeId, StringComparison.Ordinal));
                    edges.Add(edge);
                    break;
            }

            AdvanceClock();
            return ([.. nodes], [.. edges]);
        }
    }

    /// <summary>
    /// Registers or updates a peer Rhyolite installation in the P2P mesh directory.
    /// </summary>
    public PeerNode RegisterOrUpdatePeer(
        string nodeId,
        string displayName,
        string endpoint,
        HlcTimestamp? remoteHlc = null,
        string? remoteMerkleRoot = null,
        PeerNodeStatus status = PeerNodeStatus.Authenticated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var effectiveHlc = remoteHlc ?? HlcTimestamp.Zero(nodeId);
        if (remoteHlc.HasValue)
        {
            ObserveRemoteClock(remoteHlc.Value);
        }

        var peer = new PeerNode(
            NodeId: nodeId.Trim(),
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? nodeId.Trim() : displayName.Trim(),
            Endpoint: endpoint.Trim(),
            LastKnownHlc: effectiveHlc,
            LastRootMerkleHash: remoteMerkleRoot ?? ComputeVaultMerkleRoot(),
            Status: status,
            LastSeenUtc: DateTimeOffset.UtcNow);

        _peers[peer.NodeId] = peer;
        return peer;
    }

    public IReadOnlyList<PeerNode> GetPeers() =>
        [.. _peers.Values.OrderBy(p => p.NodeId, StringComparer.Ordinal)];

    /// <summary>
    /// Computes a canonical binary SHA-256 Merkle Tree root hash over sorted vault notes.
    /// </summary>
    public string ComputeVaultMerkleRoot(IEnumerable<VaultNoteSnapshot>? notes = null)
    {
        var sortedNotes = (notes ?? _notes.Values)
            .OrderBy(n => n.Path, StringComparer.Ordinal)
            .ToList();

        if (sortedNotes.Count == 0)
        {
            return Convert.ToHexStringLower(SHA256.HashData([]));
        }

        var currentLayer = new List<byte[]>(sortedNotes.Count);
        foreach (var note in sortedNotes)
        {
            var leafString = $"{note.Path}\0{note.Sha256}\0{(note.IsDeleted ? '1' : '0')}";
            currentLayer.Add(SHA256.HashData(Encoding.UTF8.GetBytes(leafString)));
        }

        while (currentLayer.Count > 1)
        {
            var nextLayer = new List<byte[]>((currentLayer.Count + 1) / 2);
            for (var i = 0; i < currentLayer.Count; i += 2)
            {
                if (i + 1 < currentLayer.Count)
                {
                    var combined = new byte[64];
                    Buffer.BlockCopy(currentLayer[i], 0, combined, 0, 32);
                    Buffer.BlockCopy(currentLayer[i + 1], 0, combined, 32, 32);
                    nextLayer.Add(SHA256.HashData(combined));
                }
                else
                {
                    nextLayer.Add(SHA256.HashData(currentLayer[i]));
                }
            }
            currentLayer = nextLayer;
        }

        return Convert.ToHexStringLower(currentLayer[0]);
    }

    /// <summary>
    /// Computes an O(N) delta reconciliation plan between the local vault and a remote peer's note manifest.
    /// </summary>
    public SyncPlan ComputeSyncPlan(
        string remoteNodeId,
        IReadOnlyCollection<VaultNoteSnapshot> remoteManifest,
        string? token = null,
        RhyoliteAuthEngine? authEngine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteNodeId);
        ArgumentNullException.ThrowIfNull(remoteManifest);

        if (authEngine is not null)
        {
            var decision = authEngine.CheckAccess(token, VaultOperation.SyncPeer);
            if (!decision.IsAllowed)
            {
                throw new UnauthorizedAccessException($"P2P sync rejected: {decision.Reason}");
            }
        }

        var localNotes = GetAllNotes(includeDeleted: true);
        var localRoot = ComputeVaultMerkleRoot(localNotes);
        var remoteRoot = ComputeVaultMerkleRoot(remoteManifest);

        if (string.Equals(localRoot, remoteRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new SyncPlan(
                LocalNodeId: LocalNodeId,
                RemoteNodeId: remoteNodeId,
                LocalMerkleRoot: localRoot,
                RemoteMerkleRoot: remoteRoot,
                IsIdentical: true,
                Deltas: [],
                ComputedAtUtc: DateTimeOffset.UtcNow);
        }

        var localByPath = localNotes.ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
        var remoteByPath = remoteManifest.ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
        var allPaths = localByPath.Keys
            .Union(remoteByPath.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal);

        var deltas = new List<SyncDeltaItem>();

        foreach (var path in allPaths)
        {
            var hasLocal = localByPath.TryGetValue(path, out var local);
            var hasRemote = remoteByPath.TryGetValue(path, out var remote);

            if (hasLocal && !hasRemote)
            {
                deltas.Add(new SyncDeltaItem(
                    path,
                    SyncActionType.PushToPeer,
                    local!.Hlc,
                    null,
                    local.Sha256,
                    null,
                    "Note exists only on local node."));
            }
            else if (!hasLocal && hasRemote)
            {
                deltas.Add(new SyncDeltaItem(
                    path,
                    SyncActionType.PullFromPeer,
                    null,
                    remote!.Hlc,
                    null,
                    remote.Sha256,
                    "Note exists only on remote peer."));
            }
            else if (hasLocal && hasRemote)
            {
                if (string.Equals(local!.Sha256, remote!.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    local.IsDeleted == remote.IsDeleted)
                {
                    continue;
                }

                // Check if physical times are close (concurrent edit window <= 5s from distinct nodes)
                var timeDiffMs = Math.Abs(local.Hlc.PhysicalTimeUnixMs - remote.Hlc.PhysicalTimeUnixMs);
                var isDistinctNode = !string.Equals(local.Hlc.NodeId, remote.Hlc.NodeId, StringComparison.Ordinal);

                if (isDistinctNode && timeDiffMs <= 5_000 && !local.IsDeleted && !remote.IsDeleted)
                {
                    var canAutoMerge = CanCleanlyLineMerge(local.Content, remote.Content);
                    deltas.Add(new SyncDeltaItem(
                        path,
                        canAutoMerge ? SyncActionType.AutoMerge : SyncActionType.ConflictBranch,
                        local.Hlc,
                        remote.Hlc,
                        local.Sha256,
                        remote.Sha256,
                        canAutoMerge
                            ? "Concurrent edits on non-overlapping lines; automatic line merge scheduled."
                            : "Concurrent overlapping edits detected; deterministic conflict branch scheduled."));
                }
                else if (local.Hlc.CompareTo(remote.Hlc) > 0)
                {
                    deltas.Add(new SyncDeltaItem(
                        path,
                        SyncActionType.PushToPeer,
                        local.Hlc,
                        remote.Hlc,
                        local.Sha256,
                        remote.Sha256,
                        "Local HLC is causally newer than remote HLC."));
                }
                else
                {
                    deltas.Add(new SyncDeltaItem(
                        path,
                        SyncActionType.PullFromPeer,
                        local.Hlc,
                        remote.Hlc,
                        local.Sha256,
                        remote.Sha256,
                        "Remote HLC is causally newer than local HLC."));
                }
            }
        }

        return new SyncPlan(
            LocalNodeId: LocalNodeId,
            RemoteNodeId: remoteNodeId,
            LocalMerkleRoot: localRoot,
            RemoteMerkleRoot: remoteRoot,
            IsIdentical: deltas.Count == 0,
            Deltas: deltas,
            ComputedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Resolves and applies a merge between a local note and a remote peer note,
    /// performing either line-level union merge or creating a conflict branch note so zero data is lost.
    /// </summary>
    public NoteMergeOutcome ResolveAndMergeNote(
        VaultNoteSnapshot localNote,
        VaultNoteSnapshot remoteNote,
        VaultNoteSnapshot? commonAncestor = null)
    {
        ArgumentNullException.ThrowIfNull(localNote);
        ArgumentNullException.ThrowIfNull(remoteNote);

        var mergedHlc = ObserveRemoteClock(remoteNote.Hlc);

        if (string.Equals(localNote.Sha256, remoteNote.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _notes[localNote.Path] = localNote;
            return new NoteMergeOutcome(localNote, null, WasAutoMerged: true, CreatedConflictBranch: false);
        }

        if (CanCleanlyLineMerge(localNote.Content, remoteNote.Content, commonAncestor?.Content, out var mergedContent))
        {
            var mergedTags = localNote.Tags.Union(remoteNote.Tags, StringComparer.OrdinalIgnoreCase);
            var winning = VaultNoteSnapshot.FromMarkdown(localNote.Path, mergedContent, mergedHlc, mergedTags);
            _notes[winning.Path] = winning;
            return new NoteMergeOutcome(winning, null, WasAutoMerged: true, CreatedConflictBranch: false);
        }

        // Deterministic winner by HLC total order; loser preserved as a conflict branch file
        var localWins = localNote.Hlc.CompareTo(remoteNote.Hlc) >= 0;
        var primary = localWins ? localNote : remoteNote;
        var secondary = localWins ? remoteNote : localNote;

        var primaryUpdated = primary with { Hlc = mergedHlc };
        _notes[primaryUpdated.Path] = primaryUpdated;

        var extIdx = primaryUpdated.Path.LastIndexOf(".md", StringComparison.OrdinalIgnoreCase);
        var stem = extIdx > 0 ? primaryUpdated.Path[..extIdx] : primaryUpdated.Path;
        var conflictPath = $"{stem}.conflict-{secondary.Hlc.NodeId}-{secondary.Hlc.PhysicalTimeUnixMs}.md";

        var conflictNote = VaultNoteSnapshot.FromMarkdown(
            conflictPath,
            secondary.Content,
            secondary.Hlc,
            secondary.Tags);
        _notes[conflictNote.Path] = conflictNote;

        return new NoteMergeOutcome(
            WinningNote: primaryUpdated,
            ConflictBranchNote: conflictNote,
            WasAutoMerged: false,
            CreatedConflictBranch: true);
    }

    private static bool CanCleanlyLineMerge(string localContent, string remoteContent) =>
        CanCleanlyLineMerge(localContent, remoteContent, null, out _);

    private static bool CanCleanlyLineMerge(
        string localContent,
        string remoteContent,
        string? baseContent,
        out string mergedContent)
    {
        var localLines = localContent.Replace("\r\n", "\n").Split('\n');
        var remoteLines = remoteContent.Replace("\r\n", "\n").Split('\n');

        if (baseContent is not null)
        {
            var baseLines = baseContent.Replace("\r\n", "\n").Split('\n');
            if (localLines.Length == baseLines.Length && remoteLines.Length == baseLines.Length)
            {
                var result = new string[baseLines.Length];
                for (var i = 0; i < baseLines.Length; i++)
                {
                    var localChanged = !string.Equals(localLines[i], baseLines[i], StringComparison.Ordinal);
                    var remoteChanged = !string.Equals(remoteLines[i], baseLines[i], StringComparison.Ordinal);

                    if (localChanged && remoteChanged && !string.Equals(localLines[i], remoteLines[i], StringComparison.Ordinal))
                    {
                        mergedContent = string.Empty;
                        return false;
                    }

                    result[i] = localChanged ? localLines[i] : remoteLines[i];
                }

                mergedContent = string.Join('\n', result);
                return true;
            }
        }

        // Fast prefix/append check: if one document is a clean prefix of the other, auto-merge by taking the superset
        if (remoteContent.StartsWith(localContent.TrimEnd(), StringComparison.Ordinal))
        {
            mergedContent = remoteContent;
            return true;
        }

        if (localContent.StartsWith(remoteContent.TrimEnd(), StringComparison.Ordinal))
        {
            mergedContent = localContent;
            return true;
        }

        mergedContent = string.Empty;
        return false;
    }
}
