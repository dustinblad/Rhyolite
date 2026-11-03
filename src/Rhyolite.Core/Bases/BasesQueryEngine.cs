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
    IReadOnlyList<string> Tags);

public sealed record BaseKanbanColumn(
    string ColumnName,
    string AccentColorHex,
    IReadOnlyList<BaseDatabaseRow> Cards);

/// <summary>
/// Obsidian Bases parity engine: turns any folder, #tag, or frontmatter query into
/// live Table, Kanban Board, and Gallery Card database projections over local Markdown files.
/// </summary>
public static class BasesQueryEngine
{
    public static List<BaseDatabaseRow> QueryRows(
        IEnumerable<VaultNote> notes,
        string? searchFilter = null,
        string? folderFilter = null,
        string? tagFilter = null)
    {
        var query = notes.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(folderFilter) && !string.Equals(folderFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(n => string.Equals(n.FolderPath, folderFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tagFilter) && !string.Equals(tagFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedTag = tagFilter.StartsWith('#') ? tagFilter : "#" + tagFilter;
            query = query.Where(n => n.Tags.Any(t => string.Equals(t, normalizedTag, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            query = query.Where(n =>
                n.Title.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) ||
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
                n.Tags))
            .ToList();
    }

    public static List<BaseKanbanColumn> BuildKanbanBoard(IEnumerable<VaultNote> notes, string? searchFilter = null)
    {
        var rows = QueryRows(notes, searchFilter);

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
}
