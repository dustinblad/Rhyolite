using Rhyolite.Core.Models;

namespace Rhyolite.Core.Graph;

public sealed class GraphNode2D
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Folder { get; init; }
    public required string ColorHex { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public double Radius { get; set; } = 12.0;
    public double PageRank { get; set; } = 0.15;
    public int Degree { get; set; }
    public bool IsOrphan { get; set; }
    public bool IsGhost { get; set; }
}

public sealed record GraphEdge2D(
    string SourceId,
    string TargetId,
    bool IsBidirectional);

public sealed record GraphTopologyMetrics(
    int TotalNotes,
    int TotalEdges,
    int OrphanCount,
    int GhostLinkCount,
    double ClusteringCoefficient,
    string TopHubTitle,
    double TopHubPageRank);

/// <summary>
/// Builds bidirectional link indexes, unlinked mention discovery, PageRank centrality,
/// and 60fps Fruchterman-Reingold force-directed 2D graph layouts.
/// </summary>
public static class KnowledgeGraphEngine
{
    private static readonly string[] AppleFolderPalette =
    [
        "#0071e3", // Apple Action Blue
        "#16a34a", // Apple Semantic Green
        "#eab308", // Apple Amber
        "#dc2626", // Apple Coral Red
        "#2997ff", // High-Luminance Link Blue
        "#86868b"  // Stone Meta Gray
    ];

    public static GraphTopologyMetrics RebuildGraphRelationships(IReadOnlyCollection<VaultNote> notes)
    {
        var byTitle = notes
            .GroupBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var n in notes)
        {
            n.Backlinks.Clear();
            n.UnlinkedMentions.Clear();
        }

        int totalEdges = 0;
        int ghostLinks = 0;

        // 1. Resolve outgoing links & populate backlinks
        foreach (var source in notes)
        {
            for (int i = 0; i < source.OutgoingLinks.Count; i++)
            {
                var link = source.OutgoingLinks[i];
                if (byTitle.TryGetValue(link.TargetTitle, out var targetNote))
                {
                    source.OutgoingLinks[i] = link with { IsResolved = true };
                    targetNote.Backlinks.Add(link);
                    totalEdges++;
                }
                else
                {
                    source.OutgoingLinks[i] = link with { IsResolved = false };
                    ghostLinks++;
                }
            }
        }

        // 2. Discover Unlinked Mentions
        foreach (var target in notes)
        {
            if (target.Title.Length < 4) continue;

            foreach (var candidate in notes)
            {
                if (string.Equals(candidate.RelativePath, target.RelativePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool alreadyLinked = candidate.OutgoingLinks.Any(l =>
                    string.Equals(l.TargetTitle, target.Title, StringComparison.OrdinalIgnoreCase));
                if (alreadyLinked) continue;

                var lines = candidate.BodyWithoutFrontmatter.Replace("\r\n", "\n").Split('\n');
                for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
                {
                    if (lines[lineIdx].Contains(target.Title, StringComparison.OrdinalIgnoreCase))
                    {
                        target.UnlinkedMentions.Add(new UnlinkedMentionRef(
                            candidate.RelativePath,
                            candidate.Title,
                            target.Title,
                            lineIdx + 1,
                            lines[lineIdx].Trim()));
                        break;
                    }
                }
            }
        }

        // 3. Compute iterative PageRank (12 iterations, damping = 0.85)
        int count = Math.Max(1, notes.Count);
        var ranks = notes.ToDictionary(n => n.Title, _ => 1.0 / count, StringComparer.OrdinalIgnoreCase);
        const double damping = 0.85;

        for (int iter = 0; iter < 12; iter++)
        {
            var nextRanks = notes.ToDictionary(n => n.Title, _ => (1.0 - damping) / count, StringComparer.OrdinalIgnoreCase);
            foreach (var note in notes)
            {
                var resolvedTargets = note.OutgoingLinks
                    .Where(l => byTitle.ContainsKey(l.TargetTitle))
                    .Select(l => l.TargetTitle)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (resolvedTargets.Count == 0) continue;
                double share = damping * ranks[note.Title] / resolvedTargets.Count;
                foreach (var targetTitle in resolvedTargets)
                {
                    nextRanks[targetTitle] += share;
                }
            }
            ranks = nextRanks;
        }

        double maxRank = ranks.Values.DefaultIfEmpty(1.0).Max();
        foreach (var note in notes)
        {
            note.PageRankScore = Math.Round(ranks.GetValueOrDefault(note.Title, 0.15) / Math.Max(0.0001, maxRank), 2);
        }

        int orphans = notes.Count(n => n.OutgoingLinks.Count == 0 && n.Backlinks.Count == 0);
        var topHub = notes.OrderByDescending(n => n.PageRankScore).FirstOrDefault();

        // Approximate local clustering / connectivity density
        double clustering = count <= 1
            ? 1.0
            : Math.Min(0.99, Math.Round((double)(count - orphans) / count, 3));

        return new GraphTopologyMetrics(
            notes.Count,
            totalEdges,
            orphans,
            ghostLinks,
            clustering,
            topHub?.Title ?? "None",
            topHub?.PageRankScore ?? 0.0);
    }

    public static (List<GraphNode2D> Nodes, List<GraphEdge2D> Edges) CreateForceLayout(
        IReadOnlyCollection<VaultNote> notes,
        double width = 960,
        double height = 680,
        int iterations = 80,
        double repulsionStrength = 290.0,
        double linkDistance = 215.0,
        double centerGravity = 0.025)
    {
        var nodes = new List<GraphNode2D>();
        var edges = new List<GraphEdge2D>();
        var folderColorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int idx = 0;
        double cx = width / 2.0;
        double cy = height / 2.0;
        double ringRadius = Math.Min(width, height) * 0.32;

        foreach (var note in notes)
        {
            if (!folderColorMap.TryGetValue(note.FolderPath, out var color))
            {
                color = AppleFolderPalette[folderColorMap.Count % AppleFolderPalette.Length];
                folderColorMap[note.FolderPath] = color;
            }

            double angle = 2.0 * Math.PI * idx / Math.Max(1, notes.Count);
            int degree = note.OutgoingLinks.Count + note.Backlinks.Count;

            nodes.Add(new GraphNode2D
            {
                Id = note.Title,
                Title = note.Title,
                Folder = string.IsNullOrEmpty(note.FolderPath) ? "Root" : note.FolderPath,
                ColorHex = color,
                X = cx + Math.Cos(angle) * ringRadius,
                Y = cy + Math.Sin(angle) * ringRadius,
                Radius = Math.Clamp(10.0 + degree * 1.4, 10.0, 20.0),
                PageRank = note.PageRankScore,
                Degree = degree,
                IsOrphan = degree == 0
            });
            idx++;
        }

        var nodeById = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var edgeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var note in notes)
        {
            foreach (var link in note.OutgoingLinks)
            {
                if (!nodeById.ContainsKey(link.TargetTitle)) continue;
                string keyA = $"{note.Title}->{link.TargetTitle}";
                string keyB = $"{link.TargetTitle}->{note.Title}";
                if (edgeSet.Contains(keyA) || edgeSet.Contains(keyB)) continue;

                bool bi = notes.Any(n =>
                    string.Equals(n.Title, link.TargetTitle, StringComparison.OrdinalIgnoreCase) &&
                    n.OutgoingLinks.Any(l => string.Equals(l.TargetTitle, note.Title, StringComparison.OrdinalIgnoreCase)));

                edges.Add(new GraphEdge2D(note.Title, link.TargetTitle, bi));
                edgeSet.Add(keyA);
            }
        }

        // Run Fruchterman-Reingold relaxation steps
        for (int step = 0; step < iterations; step++)
        {
            StepPhysics(nodes, edges, width, height, repulsionStrength, linkDistance, centerGravity);
        }

        return (nodes, edges);
    }

    public static void StepPhysics(
        IList<GraphNode2D> nodes,
        IReadOnlyList<GraphEdge2D> edges,
        double width,
        double height,
        double repulsionStrength = 180.0,
        double linkDistance = 115.0,
        double centerGravity = 0.04)
    {
        double cx = width / 2.0;
        double cy = height / 2.0;
        var map = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        // 1. Repulsive forces between all node pairs
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                var a = nodes[i];
                var b = nodes[j];
                double dx = a.X - b.X;
                double dy = a.Y - b.Y;
                double distSq = Math.Max(64.0, dx * dx + dy * dy);
                double dist = Math.Sqrt(distSq);
                double force = (repulsionStrength * repulsionStrength) / distSq;

                double fx = (dx / dist) * force;
                double fy = (dy / dist) * force;

                a.Vx += fx;
                a.Vy += fy;
                b.Vx -= fx;
                b.Vy -= fy;
            }
        }

        // 2. Attractive spring forces along edges
        foreach (var e in edges)
        {
            if (!map.TryGetValue(e.SourceId, out var a) || !map.TryGetValue(e.TargetId, out var b))
                continue;

            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double dist = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
            double displacement = (dist - linkDistance) * 0.045;

            double fx = (dx / dist) * displacement;
            double fy = (dy / dist) * displacement;

            a.Vx += fx;
            a.Vy += fy;
            b.Vx -= fx;
            b.Vy -= fy;
        }

        // 3. Center gravity + velocity damping + bounds clamping
        foreach (var n in nodes)
        {
            n.Vx += (cx - n.X) * centerGravity;
            n.Vy += (cy - n.Y) * centerGravity;

            n.Vx *= 0.65;
            n.Vy *= 0.65;

            n.X = Math.Clamp(n.X + n.Vx, 60.0, width - 60.0);
            n.Y = Math.Clamp(n.Y + n.Vy, 50.0, height - 50.0);
        }
    }
}
