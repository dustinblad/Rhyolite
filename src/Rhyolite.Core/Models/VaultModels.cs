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

    public string FolderIconEmoji { get; set; } = "📁";
    public string ObjectType => GetProperty("type", GetProperty("object_type", InferDefaultObjectType()));
    public string ObjectIconEmoji => GetProperty("icon", InferDefaultObjectIcon(ObjectType));
    public string CollectionName => GetProperty("collection", InferDefaultCollection(FolderPath));
    public string CoverColorHex => GetProperty("cover", InferDefaultCoverColor(ObjectType));

    public string GetProperty(string key, string fallback = "")
    {
        var prop = Properties.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        return prop?.Value ?? fallback;
    }

    private string InferDefaultObjectType()
    {
        if (FolderPath.Contains("Daily", StringComparison.OrdinalIgnoreCase)) return "Daily Note";
        if (FolderPath.Contains("Meeting", StringComparison.OrdinalIgnoreCase)) return "Meeting";
        if (FolderPath.Contains("Project", StringComparison.OrdinalIgnoreCase)) return "Project";
        if (FolderPath.Contains("Study", StringComparison.OrdinalIgnoreCase) || FolderPath.Contains("FSRS", StringComparison.OrdinalIgnoreCase)) return "Study Card";
        if (FolderPath.Contains("People", StringComparison.OrdinalIgnoreCase) || FolderPath.Contains("Person", StringComparison.OrdinalIgnoreCase)) return "Person";
        return "Architecture ADR";
    }

    public static string InferDefaultObjectIcon(string objectType) => objectType.ToLowerInvariant() switch
    {
        var t when t.Contains("daily") => "📅",
        var t when t.Contains("meeting") => "🤝",
        var t when t.Contains("project") => "🚀",
        var t when t.Contains("study") || t.Contains("card") => "🧠",
        var t when t.Contains("person") || t.Contains("people") => "👤",
        var t when t.Contains("book") || t.Contains("link") => "📚",
        _ => "🏛️"
    };

    public static string InferDefaultCoverColor(string objectType) => objectType.ToLowerInvariant() switch
    {
        var t when t.Contains("daily") => "#EAB308",
        var t when t.Contains("meeting") => "#EC4899",
        var t when t.Contains("project") => "#8B5CF6",
        var t when t.Contains("study") || t.Contains("card") => "#10B981",
        var t when t.Contains("person") => "#06B6D4",
        _ => "#0071E3"
    };

    private static string InferDefaultCollection(string folderPath)
    {
        if (folderPath.Contains("Daily", StringComparison.OrdinalIgnoreCase)) return "Daily Journal";
        if (folderPath.Contains("Study", StringComparison.OrdinalIgnoreCase)) return "Spaced Repetition";
        if (folderPath.Contains("Capacities", StringComparison.OrdinalIgnoreCase) || folderPath.Contains("Project", StringComparison.OrdinalIgnoreCase)) return "Launch Roadmap";
        return "Core Architecture";
    }
}

public sealed class VaultFolderNode
{
    public required string FolderPath { get; init; }
    public required string DisplayName { get; init; }
    public string? ParentFolderPath { get; init; }
    public int Depth { get; init; }
    public string IconEmoji { get; set; } = "📁";
    public string AccentColorHex { get; set; } = "#0071E3";
    public bool IsExpanded { get; set; } = true;
    public int DirectNoteCount { get; set; }
    public int TotalNoteCount { get; set; }
    public List<VaultFolderNode> Subfolders { get; init; } = [];
    public List<VaultNote> Notes { get; init; } = [];
    public string IndentMargin => $"{Depth * 16},0,0,0";
    public string IndentedLabel => $"{new string(' ', Depth * 3)}{(IsExpanded ? "▾" : "▸")} {IconEmoji} {DisplayName}";
    public string ChevronGlyph => IsExpanded ? "▾" : "▸";
    public override string ToString() => $"{IconEmoji} {DisplayName} ({TotalNoteCount})";
}

public sealed class VaultTreeItem
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string RelativePath { get; init; }
    public string? ParentFolderPath { get; init; }
    public int Depth { get; init; }
    public bool IsFolder { get; init; }
    public bool IsNote => !IsFolder;
    public bool IsExpanded { get; set; } = true;
    public bool IsSelected { get; set; }
    public bool IsPinned { get; init; }
    public string IconEmoji { get; init; } = "📄";
    public string AccentColorHex { get; init; } = "#0071E3";
    public string RightBadgeText { get; init; } = string.Empty;
    public bool HasRightBadge => !string.IsNullOrEmpty(RightBadgeText);
    public string ChevronGlyph => IsFolder ? (IsExpanded ? "▾" : "▸") : " ";
    public string IndentMargin => $"{Depth * 16},0,0,0";
    public string GuideLineMargin => Depth > 0 ? $"{(Depth - 1) * 16 + 7},0,0,0" : "0";
    public bool HasTreeGuideLine => Depth > 0;
    public VaultFolderNode? FolderNode { get; init; }
    public VaultNote? Note { get; init; }
    public override string ToString() => IsFolder ? $"{ChevronGlyph} {IconEmoji} {DisplayName} ({RightBadgeText})" : $"{IconEmoji} {DisplayName}";
}

public sealed record CapacitiesObjectTypeSchema(
    string TypeId,
    string SingularName,
    string PluralName,
    string IconEmoji,
    string AccentColorHex,
    string DefaultFolderPath,
    IReadOnlyList<string> SchemaPropertyKeys,
    string Description,
    int ObjectCount = 0);

public sealed record CapacitiesCollection(
    string CollectionId,
    string Name,
    string IconEmoji,
    string AccentColorHex,
    string Description,
    int ItemCount);

public sealed record CapacitiesCalendarEntry(
    string DateIso,
    string DayOfWeekLabel,
    string NoteRelativePath,
    string Title,
    string ObjectType,
    string ObjectIconEmoji,
    string FolderIconEmoji,
    string FolderPath,
    string SummaryText,
    int TaskCount);

public sealed record UserProfileRecord(
    string UserId,
    string DisplayName,
    string Email,
    string WorkspaceSubtitle,
    string JobTitle,
    string Organization,
    string RoleBadge,
    string PresenceStatus,
    string AvatarMode,
    string AvatarInitialsOrEmoji,
    string AvatarAccentHex,
    string? AvatarImagePath,
    DateTimeOffset UpdatedAtUtc);

public sealed record AdminAuditLogEntry(
    string TimestampDisplay,
    string ActorEmail,
    string ActionTitle,
    string TargetResource,
    string SeverityBadge);
