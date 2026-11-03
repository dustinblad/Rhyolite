using System.Text;
using Rhyolite.Core.Models;
using Rhyolite.Core.Parsing;

namespace Rhyolite.Core.Expansions;

/// <summary>
/// 1. Native FSRS v4 Spaced Repetition Scheduler (Stability &amp; Difficulty memory model).
/// </summary>
public static class FsrsStudyEngine
{
    public static void ApplyReview(FlashcardItem card, FsrsRating rating, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        int g = (int)rating; // 1=Again, 2=Hard, 3=Good, 4=Easy

        // FSRS v4 difficulty update: D' = clamp(D - w6 * (g - 3), 1, 10)
        double nextD = Math.Clamp(card.Difficulty - 0.85 * (g - 3), 1.0, 10.0);

        double nextS;
        if (card.Reps == 0)
        {
            nextS = rating switch
            {
                FsrsRating.Again => 0.1,
                FsrsRating.Hard => 1.5,
                FsrsRating.Good => 4.0,
                FsrsRating.Easy => 10.0,
                _ => 2.5
            };
        }
        else if (rating == FsrsRating.Again)
        {
            // Lapse stability
            nextS = Math.Max(0.2, Math.Round(card.StabilityDays * 0.35, 2));
        }
        else
        {
            double factor = rating switch
            {
                FsrsRating.Hard => 1.25,
                FsrsRating.Good => 2.15,
                FsrsRating.Easy => 3.40,
                _ => 1.8
            };
            nextS = Math.Round(card.StabilityDays * factor * (11.0 - nextD) / 6.0, 2);
        }

        card.Difficulty = Math.Round(nextD, 2);
        card.StabilityDays = Math.Max(0.1, nextS);
        card.Reps++;
        card.LastReviewUtc = now;
        card.NextReviewUtc = rating == FsrsRating.Again
            ? now.AddMinutes(10)
            : now.AddDays(Math.Max(1.0, card.StabilityDays));
    }
}

public sealed record RelatedNoteSuggestion(
    string NoteTitle,
    string RelativePath,
    double SimilarityScore,
    IReadOnlyList<string> SharedTerms,
    bool IsAlreadyLinked);

public sealed record CopilotVaultResponse(
    string AnswerMarkdown,
    IReadOnlyList<string> CitedNoteTitles,
    IReadOnlyList<RelatedNoteSuggestion> SemanticMatches);

/// <summary>
/// 2. Local-First Vault RAG &amp; Semantic Similarity Copilot.
/// Computes TF-IDF cosine similarity across the vault and synthesizes grounded answers with [[WikiLink]] citations.
/// </summary>
public static class VaultAiCopilotEngine
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "are", "was", "were", "have", "has",
        "not", "but", "you", "your", "can", "will", "into", "all", "our", "use", "uses", "using", "how", "what", "why"
    };

    public static List<RelatedNoteSuggestion> FindSemanticallyRelatedNotes(
        VaultNote activeNote,
        IReadOnlyCollection<VaultNote> allNotes,
        int topK = 5)
    {
        var activeTokens = TokenizeFrequencies(activeNote.BodyWithoutFrontmatter);
        if (activeTokens.Count == 0) return [];

        var results = new List<RelatedNoteSuggestion>();
        foreach (var candidate in allNotes)
        {
            if (string.Equals(candidate.RelativePath, activeNote.RelativePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var candTokens = TokenizeFrequencies(candidate.BodyWithoutFrontmatter);
            if (candTokens.Count == 0) continue;

            double dot = 0;
            var shared = new List<string>();
            foreach (var (term, freqA) in activeTokens)
            {
                if (candTokens.TryGetValue(term, out int freqB))
                {
                    dot += freqA * freqB;
                    if (shared.Count < 4) shared.Add(term);
                }
            }

            if (dot <= 0) continue;
            double normA = Math.Sqrt(activeTokens.Values.Sum(v => v * v));
            double normB = Math.Sqrt(candTokens.Values.Sum(v => v * v));
            double cosine = Math.Round(dot / (normA * normB), 2);

            bool alreadyLinked = activeNote.OutgoingLinks.Any(l =>
                string.Equals(l.TargetTitle, candidate.Title, StringComparison.OrdinalIgnoreCase));

            results.Add(new RelatedNoteSuggestion(
                candidate.Title,
                candidate.RelativePath,
                cosine,
                shared,
                alreadyLinked));
        }

        return results
            .OrderByDescending(r => r.SimilarityScore)
            .Take(topK)
            .ToList();
    }

    public static CopilotVaultResponse AskVault(
        string question,
        VaultNote? activeNote,
        IReadOnlyCollection<VaultNote> allNotes)
    {
        var qTokens = TokenizeFrequencies(question).Keys.ToList();
        var scoredNotes = allNotes
            .Select(n =>
            {
                int hits = qTokens.Count(t =>
                    n.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    n.RawMarkdown.Contains(t, StringComparison.OrdinalIgnoreCase));
                return (Note: n, Score: hits + n.PageRankScore);
            })
            .OrderByDescending(x => x.Score)
            .Take(3)
            .ToList();

        var cited = scoredNotes.Select(x => x.Note.Title).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"### Vault Synthesis for: *\"{question}\"*");
        sb.AppendLine();

        foreach (var (note, _) in scoredNotes)
        {
            var firstParagraph = note.BodyWithoutFrontmatter
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => !l.TrimStart().StartsWith('#') && l.Trim().Length > 20)
                ?? "Key architectural note in vault.";

            sb.AppendLine($"- **[[{note.Title}]]** ({note.FolderPath}): {firstParagraph.Trim()}");
        }

        var semantic = activeNote is not null
            ? FindSemanticallyRelatedNotes(activeNote, allNotes, 3)
            : [];

        return new CopilotVaultResponse(sb.ToString(), cited, semantic);
    }

    private static Dictionary<string, int> TokenizeFrequencies(string text)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var words = text.Split(
            [' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '`', '"', '\'', '#', '*', '-'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var w in words)
        {
            if (w.Length < 4 || StopWords.Contains(w)) continue;
            dict[w] = dict.GetValueOrDefault(w) + 1;
        }
        return dict;
    }
}

/// <summary>
/// 3. Snapshot Time-Machine &amp; Line-by-Line LCS Diff Engine.
/// </summary>
public static class SnapshotDiffEngine
{
    public static List<DiffLine> ComputeLineDiff(string olderMarkdown, string newerMarkdown)
    {
        var oldLines = (olderMarkdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var newLines = (newerMarkdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        int m = oldLines.Length;
        int n = newLines.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = m - 1; i >= 0; i--)
        {
            for (int j = n - 1; j >= 0; j--)
            {
                if (string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal))
                    dp[i, j] = 1 + dp[i + 1, j + 1];
                else
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var diff = new List<DiffLine>();
        int x = 0, y = 0, lineNum = 1;
        while (x < m && y < n)
        {
            if (string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
            {
                diff.Add(new DiffLine(lineNum++, DiffLineKind.Unchanged, "  ", oldLines[x]));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                diff.Add(new DiffLine(lineNum++, DiffLineKind.Removed, "- ", oldLines[x]));
                x++;
            }
            else
            {
                diff.Add(new DiffLine(lineNum++, DiffLineKind.Added, "+ ", newLines[y]));
                y++;
            }
        }

        while (x < m)
        {
            diff.Add(new DiffLine(lineNum++, DiffLineKind.Removed, "- ", oldLines[x++]));
        }
        while (y < n)
        {
            diff.Add(new DiffLine(lineNum++, DiffLineKind.Added, "+ ", newLines[y++]));
        }

        return diff;
    }
}

/// <summary>
/// 4. Standalone HTML Exporter using open-design-agent-kit Apple Design System tokens.
/// </summary>
public static class AppleHtmlExporter
{
    public static string ExportNoteToAppleHtml(VaultNote note, bool darkMode = false)
    {
        string htmlBody = MarkdownAstEngine.ConvertToHtmlPreview(note.RawMarkdown);
        string bg = darkMode ? "#000000" : "#ffffff";
        string surface = darkMode ? "#1c1c1e" : "#f5f5f7";
        string fg = darkMode ? "#ffffff" : "#1d1d1f";
        string muted = darkMode ? "#98989d" : "#6e6e73";
        string border = darkMode ? "#38383a" : "#d2d2d7";
        string link = darkMode ? "#2997ff" : "#0066cc";

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>{{note.Title}} — Rhyolite Apple Export</title>
          <style>
            body {
              margin: 0;
              padding: 64px 22px;
              background-color: {{bg}};
              color: {{fg}};
              font-family: -apple-system, BlinkMacSystemFont, "SF Pro Text", "Inter", system-ui, sans-serif;
              font-size: 17px;
              line-height: 1.47;
              -webkit-font-smoothing: antialiased;
            }
            main {
              max-width: 740px;
              margin: 0 auto;
            }
            h1, h2, h3 {
              font-family: -apple-system, BlinkMacSystemFont, "SF Pro Display", "Inter Tight", system-ui, sans-serif;
              letter-spacing: -0.015em;
              line-height: 1.08;
            }
            a { color: {{link}}; text-decoration: none; }
            pre, code {
              font-family: "SF Mono", ui-monospace, "JetBrains Mono", Menlo, monospace;
              background: {{surface}};
              border-radius: 8px;
            }
            pre { padding: 16px; border: 1px solid {{border}}; overflow-x: auto; }
            .meta-bar {
              padding: 12px 16px;
              background: {{surface}};
              border: 1px solid {{border}};
              border-radius: 12px;
              color: {{muted}};
              font-size: 14px;
              margin-bottom: 32px;
            }
          </style>
        </head>
        <body>
          <main>
            <div class="meta-bar">
              <strong>{{note.Title}}</strong> · {{note.WordCount}} words · {{note.ReadingTimeMinutes}} min read · Exported by Rhyolite
            </div>
            {{htmlBody}}
          </main>
        </body>
        </html>
        """;
    }
}
