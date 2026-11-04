using System.Globalization;
using Rhyolite.Core.Models;

namespace Rhyolite.Core.Bases;

public sealed record BaseDatabaseRow(
    string NoteRelativePath,
    string Title,
    string Folder,
    string Status,
    string Subsystem,
    string Priority,
    int BacklinkCount,
    int WordCount,
    string WriteLatencyLabel,
    IReadOnlyList<string> Tags,
    string FolderIconEmoji = "📁",
    string ObjectType = "Architecture ADR",
    string ObjectIconEmoji = "🏛️",
    string CollectionName = "Core Architecture",
    string CoverColorHex = "#0071E3",
    string SummarySnippet = "",
    string DateBadge = "2026-11-03");

public sealed record BaseKanbanColumn(
    string ColumnName,
    string AccentColorHex,
    IReadOnlyList<BaseDatabaseRow> Cards);

/// <summary>
/// Obsidian Bases + Capacities Object Studio engine: turns any hierarchical folder/subfolder, #tag,
/// Capacities Object Type, or Collection into live Table, Kanban Board, Gallery/Wall Card, and Calendar projections.
/// </summary>
public static class BasesQueryEngine
{
    public static List<BaseDatabaseRow> QueryRows(
        IEnumerable<VaultNote> notes,
        string? searchFilter = null,
        string? folderFilter = null,
        string? tagFilter = null,
        string? objectTypeFilter = null,
        string? collectionFilter = null)
    {
        var query = notes.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(folderFilter) && !string.Equals(folderFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedFolder = folderFilter.Replace('\\', '/').Trim('/');
            query = query.Where(n =>
                string.Equals(n.FolderPath, normalizedFolder, StringComparison.OrdinalIgnoreCase) ||
                n.FolderPath.StartsWith(normalizedFolder + "/", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tagFilter) && !string.Equals(tagFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedTag = tagFilter.StartsWith('#') ? tagFilter : "#" + tagFilter;
            query = query.Where(n => n.Tags.Any(t => string.Equals(t, normalizedTag, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(objectTypeFilter) && !string.Equals(objectTypeFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(n =>
                string.Equals(n.ObjectType, objectTypeFilter, StringComparison.OrdinalIgnoreCase) ||
                n.ObjectType.Contains(objectTypeFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(collectionFilter) && !string.Equals(collectionFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(n =>
                string.Equals(n.CollectionName, collectionFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            query = query.Where(n =>
                n.Title.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) ||
                n.ObjectType.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) ||
                n.CollectionName.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) ||
                n.RawMarkdown.Contains(searchFilter, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderByDescending(n => n.IsPinned)
            .ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .Select(n => new BaseDatabaseRow(
                n.RelativePath,
                n.Title,
                string.IsNullOrEmpty(n.FolderPath) ? "Root" : n.FolderPath,
                n.GetProperty("status", "Active"),
                n.GetProperty("subsystem", "Rhyolite.Core"),
                n.GetProperty("priority", "P1 High"),
                n.Backlinks.Count,
                n.WordCount,
                $"{n.LastWriteMilliseconds:0.00} ms",
                n.Tags,
                n.FolderIconEmoji,
                n.ObjectType,
                n.ObjectIconEmoji,
                n.CollectionName,
                n.CoverColorHex,
                BuildSummarySnippet(n.BodyWithoutFrontmatter, n.RawMarkdown),
                ResolveDateBadge(n)))
            .ToList();
    }

    public static List<BaseKanbanColumn> BuildKanbanBoard(
        IEnumerable<VaultNote> notes,
        string? searchFilter = null,
        string? folderFilter = null,
        string? objectTypeFilter = null,
        string? collectionFilter = null)
    {
        var rows = QueryRows(notes, searchFilter, folderFilter, null, objectTypeFilter, collectionFilter);

        var planned = rows.Where(r =>
            r.Status.Contains("Plan", StringComparison.OrdinalIgnoreCase) ||
            r.Status.Contains("Todo", StringComparison.OrdinalIgnoreCase) ||
            r.Status.Contains("Backlog", StringComparison.OrdinalIgnoreCase)).ToList();

        var inProgress = rows.Where(r =>
            r.Status.Contains("Progress", StringComparison.OrdinalIgnoreCase) ||
            r.Status.Contains("Active", StringComparison.OrdinalIgnoreCase) ||
            r.Status.Contains("Review", StringComparison.OrdinalIgnoreCase)).ToList();

        var shipped = rows.Where(r =>
            !planned.Contains(r) && !inProgress.Contains(r)).ToList();

        return
        [
            new BaseKanbanColumn("Planned", "#eab308", planned),
            new BaseKanbanColumn("In Progress", "#0071e3", inProgress),
            new BaseKanbanColumn("Verified & Shipped", "#16a34a", shipped)
        ];
    }

    public static List<CapacitiesCollection> GetCollections(IEnumerable<VaultNote> notes)
    {
        var noteList = notes.ToList();
        var groupedCounts = noteList
            .GroupBy(n => n.CollectionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var defaults = new (string Id, string Name, string Icon, string Color, string Desc)[]
        {
            ("core-architecture", "Core Architecture", "🏛️", "#0071E3", "System architecture ADRs, storage specs, and rendering pipelines"),
            ("launch-roadmap", "Launch Roadmap", "🚀", "#8B5CF6", "Capacities Studio projects, release milestones, and engineering reviews"),
            ("daily-journal", "Daily Journal", "📅", "#EAB308", "Chronological daily engineering logs, meeting notes, and quick captures"),
            ("spaced-repetition", "Spaced Repetition", "🧠", "#10B981", "FSRS v4 memory cards, cloze deletions, and research syntheses")
        };

        var result = new List<CapacitiesCollection>(defaults.Length + 4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in defaults)
        {
            groupedCounts.TryGetValue(d.Name, out int count);
            result.Add(new CapacitiesCollection(d.Id, d.Name, d.Icon, d.Color, d.Desc, count));
            seen.Add(d.Name);
        }

        foreach (var kvp in groupedCounts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Contains(kvp.Key))
            {
                continue;
            }

            string id = kvp.Key.ToLowerInvariant().Replace(' ', '-');
            result.Add(new CapacitiesCollection(
                id,
                kvp.Key,
                "🗂️",
                "#06B6D4",
                $"Custom Capacities collection for {kvp.Key}",
                kvp.Value));
        }

        return result;
    }

    public static List<CapacitiesCalendarEntry> BuildCalendarTimeline(IEnumerable<VaultNote> notes)
    {
        return notes
            .Select(n =>
            {
                string dateIso = ResolveDateBadge(n);
                string dayLabel = DateTime.TryParse(dateIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate)
                    ? parsedDate.ToString("ddd, MMM d", CultureInfo.InvariantCulture)
                    : "Timeline";

                return new CapacitiesCalendarEntry(
                    DateIso: dateIso,
                    DayOfWeekLabel: dayLabel,
                    NoteRelativePath: n.RelativePath,
                    Title: n.Title,
                    ObjectType: n.ObjectType,
                    ObjectIconEmoji: n.ObjectIconEmoji,
                    FolderIconEmoji: n.FolderIconEmoji,
                    FolderPath: string.IsNullOrEmpty(n.FolderPath) ? "Root" : n.FolderPath,
                    SummaryText: BuildSummarySnippet(n.BodyWithoutFrontmatter, n.RawMarkdown),
                    TaskCount: n.Tasks.Count);
            })
            .OrderByDescending(e => e.DateIso, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveDateBadge(VaultNote note)
    {
        string explicitDate = note.GetProperty("date", string.Empty);
        if (!string.IsNullOrWhiteSpace(explicitDate))
        {
            return explicitDate;
        }

        if (note.Title.Length == 10 &&
            DateTime.TryParseExact(note.Title, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return note.Title;
        }

        return note.LastWriteTimeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string BuildSummarySnippet(string bodyWithoutFrontmatter, string rawMarkdown)
    {
        string source = !string.IsNullOrWhiteSpace(bodyWithoutFrontmatter) ? bodyWithoutFrontmatter : rawMarkdown;
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var lines = source
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("---") && !l.StartsWith('#') && !l.StartsWith("> [!"))
            .ToList();

        string combined = string.Join(" ", lines).Replace("[[", "").Replace("]]", "").Trim();
        return combined.Length <= 120 ? combined : combined[..117] + "...";
    }
}
