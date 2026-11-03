using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Rhyolite.Core.Models;

namespace Rhyolite.Core.Parsing;

/// <summary>
/// High-speed Obsidian-flavored Markdown AST &amp; metadata parser.
/// Parses YAML frontmatter properties, [[WikiLinks]], ![[Embeds]], #tags, Callouts, Tasks, and FSRS Flashcards in &lt; 0.5 ms.
/// </summary>
public static partial class MarkdownAstEngine
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    [GeneratedRegex(@"(!?)\[\[([^\]\|#\^]+)(?:#([^\]\|\^]+))?(?:\^([^\]\|]+))?(?:\|([^\]]+))?\]\]", RegexOptions.Compiled)]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"(?:^|\s)#([a-zA-Z0-9_\-/]+)", RegexOptions.Compiled)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\s*-\s*\[([ xX])\]\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex TaskLineRegex();

    [GeneratedRegex(@"📅\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled)]
    private static partial Regex DueDateRegex();

    [GeneratedRegex(@"^\s*>\s*\[!([a-zA-Z0-9_-]+)\]([+-])?\s*(.*)$", RegexOptions.Compiled)]
    private static partial Regex CalloutHeaderRegex();

    [GeneratedRegex(@"\{\{c\d+::([^}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex ClozeCardRegex();

    public static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public static void ParseNoteMetadata(VaultNote note)
    {
        var sw = Stopwatch.StartNew();
        var raw = note.RawMarkdown ?? string.Empty;
        note.ContentSha256 = ComputeSha256(raw);

        var (properties, body, bodyStartLine) = ExtractFrontmatter(raw);
        note.Properties = properties;
        note.BodyWithoutFrontmatter = body;

        var outgoingLinks = new List<WikiLinkRef>();
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headings = new List<MarkdownHeading>();
        var callouts = new List<MarkdownCallout>();
        var tasks = new List<VaultTaskItem>();
        var flashcards = new List<FlashcardItem>();

        // Pull tags from frontmatter if present
        foreach (var prop in properties)
        {
            if (string.Equals(prop.Key, "tags", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var rawTag in prop.Value.Split([',', ' ', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var normalized = rawTag.TrimStart('#');
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        tags.Add("#" + normalized);
                    }
                }
            }
            else if (string.Equals(prop.Key, "pinned", StringComparison.OrdinalIgnoreCase))
            {
                note.IsPinned = string.Equals(prop.Value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        var lines = raw.Replace("\r\n", "\n").Split('\n');
        bool inCodeFence = false;
        string? pendingQuestion = null;

        for (int i = bodyStartLine; i < lines.Length; i++)
        {
            var line = lines[i];
            int lineNum = i + 1;

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                continue;
            }

            // 1. Headings
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                int level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                {
                    level++;
                }
                if (level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' ')
                {
                    headings.Add(new MarkdownHeading(level, trimmed[(level + 1)..].Trim(), lineNum));
                }
            }

            // 2. WikiLinks & Embeds
            foreach (Match match in WikiLinkRegex().Matches(line))
            {
                bool isEmbed = match.Groups[1].Value == "!";
                string target = match.Groups[2].Value.Trim();
                string? headingAnchor = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
                string? blockId = match.Groups[4].Success ? match.Groups[4].Value.Trim() : null;
                string display = match.Groups[5].Success ? match.Groups[5].Value.Trim() : target;

                outgoingLinks.Add(new WikiLinkRef(
                    note.RelativePath,
                    target,
                    display,
                    headingAnchor,
                    blockId,
                    isEmbed,
                    lineNum,
                    line.Trim()));
            }

            // 3. Inline #tags
            foreach (Match tagMatch in TagRegex().Matches(line))
            {
                tags.Add("#" + tagMatch.Groups[1].Value);
            }

            // 4. Callouts
            var calloutMatch = CalloutHeaderRegex().Match(line);
            if (calloutMatch.Success)
            {
                string cType = calloutMatch.Groups[1].Value.ToUpperInvariant();
                string foldToken = calloutMatch.Groups[2].Value;
                string cTitle = string.IsNullOrWhiteSpace(calloutMatch.Groups[3].Value)
                    ? cType
                    : calloutMatch.Groups[3].Value.Trim();

                var bodyBuilder = new StringBuilder();
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('>'))
                {
                    i++;
                    var quoteContent = lines[i].TrimStart()[1..].TrimStart();
                    if (bodyBuilder.Length > 0) bodyBuilder.AppendLine();
                    bodyBuilder.Append(quoteContent);
                }

                callouts.Add(new MarkdownCallout(
                    cType,
                    cTitle,
                    bodyBuilder.ToString(),
                    !string.IsNullOrEmpty(foldToken),
                    foldToken != "-",
                    lineNum));
                continue;
            }

            // 5. Tasks (- [ ] or - [x])
            var taskMatch = TaskLineRegex().Match(line);
            if (taskMatch.Success)
            {
                bool isDone = !string.IsNullOrWhiteSpace(taskMatch.Groups[1].Value);
                string taskText = taskMatch.Groups[2].Value.Trim();
                var dueMatch = DueDateRegex().Match(taskText);
                string? dueDate = dueMatch.Success ? dueMatch.Groups[1].Value : null;

                var priority = TaskPriority.Normal;
                if (taskText.Contains("⏫") || taskText.Contains("[priority:: high]", StringComparison.OrdinalIgnoreCase))
                    priority = TaskPriority.High;
                else if (taskText.Contains("🔺") || taskText.Contains("[priority:: critical]", StringComparison.OrdinalIgnoreCase))
                    priority = TaskPriority.Critical;
                else if (taskText.Contains("🔼"))
                    priority = TaskPriority.Medium;
                else if (taskText.Contains("🔽"))
                    priority = TaskPriority.Low;

                var taskTags = TagRegex().Matches(taskText).Select(m => "#" + m.Groups[1].Value).ToList();

                tasks.Add(new VaultTaskItem
                {
                    Id = $"{note.RelativePath}:L{lineNum}",
                    NotePath = note.RelativePath,
                    NoteTitle = note.Title,
                    Text = taskText,
                    IsCompleted = isDone,
                    DueDate = dueDate,
                    Priority = priority,
                    Tags = taskTags,
                    LineNumber = lineNum
                });
            }

            // 6. Native FSRS Flashcards (Q:/A:, inline ::, or {{c1::cloze}})
            if (trimmed.StartsWith("Q:", StringComparison.OrdinalIgnoreCase))
            {
                pendingQuestion = trimmed[2..].Trim();
            }
            else if (pendingQuestion is not null && trimmed.StartsWith("A:", StringComparison.OrdinalIgnoreCase))
            {
                string answer = trimmed[2..].Trim();
                flashcards.Add(new FlashcardItem
                {
                    Id = $"{note.RelativePath}:QA:{lineNum}",
                    NotePath = note.RelativePath,
                    NoteTitle = note.Title,
                    Question = pendingQuestion,
                    Answer = answer,
                    CardType = FlashcardType.Basic
                });
                pendingQuestion = null;
            }
            else if (line.Contains(" :: ") && !line.TrimStart().StartsWith('>'))
            {
                var parts = line.Split(" :: ", 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].Length > 2 && parts[1].Length > 1)
                {
                    flashcards.Add(new FlashcardItem
                    {
                        Id = $"{note.RelativePath}:BI:{lineNum}",
                        NotePath = note.RelativePath,
                        NoteTitle = note.Title,
                        Question = parts[0].TrimStart('-', '*', ' '),
                        Answer = parts[1],
                        CardType = FlashcardType.Bidirectional
                    });
                }
            }
            else if (ClozeCardRegex().IsMatch(line))
            {
                var clozeMatch = ClozeCardRegex().Match(line);
                string hiddenTerm = clozeMatch.Groups[1].Value;
                string prompt = ClozeCardRegex().Replace(line.Trim(), "[...]");
                flashcards.Add(new FlashcardItem
                {
                    Id = $"{note.RelativePath}:CLOZE:{lineNum}",
                    NotePath = note.RelativePath,
                    NoteTitle = note.Title,
                    Question = prompt,
                    Answer = hiddenTerm,
                    CardType = FlashcardType.Cloze
                });
            }
        }

        note.OutgoingLinks = outgoingLinks;
        note.Tags = tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        note.Headings = headings;
        note.Callouts = callouts;
        note.Tasks = tasks;
        note.Flashcards = flashcards;

        // Word count & reading time
        var wordTokens = body.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        note.WordCount = wordTokens.Length;
        note.ReadingTimeMinutes = Math.Max(1, (int)Math.Ceiling(note.WordCount / 220.0));

        sw.Stop();
        note.LastParseMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 3);
    }

    public static (List<FrontmatterProperty> Properties, string Body, int BodyStartLine) ExtractFrontmatter(string rawMarkdown)
    {
        var props = new List<FrontmatterProperty>();
        if (string.IsNullOrEmpty(rawMarkdown))
        {
            return (props, string.Empty, 0);
        }

        var normalized = rawMarkdown.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (props, rawMarkdown, 0);
        }

        var lines = normalized.Split('\n');
        int endFenceIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() is "---" or "...")
            {
                endFenceIndex = i;
                break;
            }
        }

        if (endFenceIndex == -1)
        {
            return (props, rawMarkdown, 0);
        }

        for (int i = 1; i < endFenceIndex; i++)
        {
            var line = lines[i];
            int colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;

            string key = line[..colonIdx].Trim();
            string val = line[(colonIdx + 1)..].Trim().Trim('"', '\'');
            var propType = InferPropertyType(key, val);
            props.Add(new FrontmatterProperty(key, val, propType));
        }

        var body = string.Join('\n', lines[(endFenceIndex + 1)..]).TrimStart('\n');
        return (props, body, endFenceIndex + 1);
    }

    public static string UpsertFrontmatterProperty(string rawMarkdown, string key, string newValue)
    {
        var (props, body, _) = ExtractFrontmatter(rawMarkdown);
        int existingIdx = props.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        var propType = InferPropertyType(key, newValue);

        if (existingIdx >= 0)
        {
            props[existingIdx] = new FrontmatterProperty(key, newValue, propType);
        }
        else
        {
            props.Add(new FrontmatterProperty(key, newValue, propType));
        }

        var sb = new StringBuilder();
        sb.AppendLine("---");
        foreach (var p in props)
        {
            sb.AppendLine($"{p.Key}: {p.Value}");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(body);
        return sb.ToString();
    }

    public static string ConvertToHtmlPreview(string rawMarkdown)
    {
        var (_, body, _) = ExtractFrontmatter(rawMarkdown);
        return Markdown.ToHtml(body, Pipeline);
    }

    private static FrontmatterPropertyType InferPropertyType(string key, string value)
    {
        if (string.Equals(key, "tags", StringComparison.OrdinalIgnoreCase) || value.StartsWith('['))
            return FrontmatterPropertyType.List;
        if (bool.TryParse(value, out _))
            return FrontmatterPropertyType.Checkbox;
        if (double.TryParse(value, out _))
            return FrontmatterPropertyType.Number;
        if (DateOnly.TryParse(value, out _))
            return FrontmatterPropertyType.Date;
        if (DateTime.TryParse(value, out _))
            return FrontmatterPropertyType.DateTime;
        return FrontmatterPropertyType.Text;
    }
}
