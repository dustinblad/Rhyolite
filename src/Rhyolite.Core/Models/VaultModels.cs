using System.Text.Json.Serialization;

namespace Rhyolite.Core.Models;

public enum FrontmatterPropertyType
{
    Text,
    Number,
    Checkbox,
    Date,
    DateTime,
    List
}

public sealed record FrontmatterProperty(
    string Key,
    string Value,
    FrontmatterPropertyType Type = FrontmatterPropertyType.Text);

public sealed record WikiLinkRef(
    string SourceNotePath,
    string TargetTitle,
    string DisplayLabel,
    string? HeadingAnchor,
    string? BlockId,
    bool IsEmbed,
    int LineNumber,
    string ContextSnippet,
    bool IsResolved = true);

public sealed record UnlinkedMentionRef(
    string SourceNotePath,
    string SourceNoteTitle,
    string TargetNoteTitle,
    int LineNumber,
    string ContextSnippet);

public sealed record MarkdownHeading(
    int Level,
    string Text,
    int LineNumber);

public sealed record MarkdownCallout(
    string CalloutType,
    string Title,
    string Body,
    bool IsFoldable,
    bool IsDefaultExpanded,
    int LineNumber);

public enum TaskPriority
{
    Normal,
    Low,
    Medium,
    High,
    Critical
}

public sealed class VaultTaskItem
{
    public required string Id { get; init; }
    public required string NotePath { get; init; }
    public required string NoteTitle { get; init; }
    public required string Text { get; set; }
    public bool IsCompleted { get; set; }
    public string? DueDate { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public List<string> Tags { get; init; } = [];
    public int LineNumber { get; init; }
}

public enum FlashcardType
{
    Basic,
    Bidirectional,
    Cloze
}

public enum FsrsRating
{
    Again = 1,
    Hard = 2,
    Good = 3,
    Easy = 4
}

public sealed class FlashcardItem
{
    public required string Id { get; init; }
    public required string NotePath { get; init; }
    public required string NoteTitle { get; init; }
    public required string Question { get; init; }
    public required string Answer { get; init; }
    public FlashcardType CardType { get; init; } = FlashcardType.Basic;
    public double StabilityDays { get; set; } = 2.5;
    public double Difficulty { get; set; } = 5.0;
    public int Reps { get; set; }
    public DateTime NextReviewUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastReviewUtc { get; set; }
}

public sealed record NoteSnapshot(
    string SnapshotId,
    string NotePath,
    DateTime CreatedUtc,
    string Label,
    string RawMarkdown,
    string ContentSha256);

public enum DiffLineKind
{
    Unchanged,
    Added,
    Removed
}

public sealed record DiffLine(
    int LineNumber,
    DiffLineKind Kind,
    string Prefix,
    string Text);

public sealed class VaultNote
{
    public required string Id { get; init; }
    public required string RelativePath { get; set; }
    public required string FullPath { get; set; }
    public required string Title { get; set; }
    public required string FolderPath { get; set; }
    public required string RawMarkdown { get; set; }
    public string BodyWithoutFrontmatter { get; set; } = string.Empty;
    public DateTime LastWriteTimeUtc { get; set; } = DateTime.UtcNow;
    public string ContentSha256 { get; set; } = string.Empty;
    public double LastWriteMilliseconds { get; set; } = 0.14;
    public double LastParseMilliseconds { get; set; } = 0.08;
    public bool IsPinned { get; set; }

    public List<FrontmatterProperty> Properties { get; set; } = [];
    public List<WikiLinkRef> OutgoingLinks { get; set; } = [];
    public List<WikiLinkRef> Backlinks { get; set; } = [];
    public List<UnlinkedMentionRef> UnlinkedMentions { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<MarkdownHeading> Headings { get; set; } = [];
    public List<MarkdownCallout> Callouts { get; set; } = [];
    public List<VaultTaskItem> Tasks { get; set; } = [];
    public List<FlashcardItem> Flashcards { get; set; } = [];

    public int WordCount { get; set; }
    public int ReadingTimeMinutes { get; set; } = 1;
    public double PageRankScore { get; set; } = 0.15;

    public string GetProperty(string key, string fallback = "")
    {
        var prop = Properties.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        return prop?.Value ?? fallback;
    }
}
