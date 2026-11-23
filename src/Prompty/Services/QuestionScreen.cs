using System.Text.RegularExpressions;

namespace Prompty.Services;

public enum QuestionRowKind { Option, TypeSomething, Submit }

/// <summary>One row of Claude Code's question widget.</summary>
public sealed record QuestionRow(QuestionRowKind Kind, int Number, string Label, string Description, bool Checked, bool HasCursor);

/// <summary>A tab in the widget's header: a question's short header, or Submit.</summary>
public sealed record QuestionTab(string Title, bool Answered);

/// <summary>
/// What Claude Code's AskUserQuestion widget currently shows, parsed from the console screen: either one question
/// (with its options) or the "Review your answers" step. Claude Code doesn't write a pending question to the
/// transcript, so the screen is the only place to read it. Layouts handled (Claude Code 2.1.330):
/// <code>
///   ←  ☐ Color  ☒ Toppings  ✔ Submit  →        (several questions / multi-select)   or   ☐ Size   (one question)
///   Pick toppings
///   ❯ 1. [✔] Cheese
///            Cheese                              (description)
///     4. [ ] Type something                      (free-text row; shows the typed text once entered)
///        Submit                                  (multi-select only)
///   ──────
///     5. Chat about this
///   Enter to select · Tab/Arrow keys to navigate · Esc to cancel
/// </code>
/// The review step shows "Review your answers", "● question / → answer" pairs and "1. Submit answers / 2. Cancel".
/// </summary>
public sealed partial record QuestionScreen(
    bool IsReview,
    IReadOnlyList<QuestionTab> Tabs,
    string Question,
    bool MultiSelect,
    IReadOnlyList<QuestionRow> Rows,
    IReadOnlyList<(string Question, string Answer)> Review)
{
    public IEnumerable<QuestionRow> Options => Rows.Where(r => r.Kind == QuestionRowKind.Option);
    public QuestionRow? TypeRow => Rows.FirstOrDefault(r => r.Kind == QuestionRowKind.TypeSomething);
    public int CursorIndex => Rows.ToList().FindIndex(r => r.HasCursor);

    /// <summary>Identifies what's on screen, so a card can be kept while the same step is showing.</summary>
    public string Key => IsReview ? "review:" + string.Join("|", Review.Select(r => r.Question)) : "q:" + Question;

    [GeneratedRegex(@"^\s*(?:←\s+)?(?:[☐☒✔]\s+[^☐☒✔→]+?\s*){1,}(?:→)?\s*$")]
    private static partial Regex TabLineRegex();
    [GeneratedRegex(@"([☐☒✔])\s+([^☐☒✔→]+?)\s*(?=[☐☒✔→]|$)")]
    private static partial Regex TabRegex();
    [GeneratedRegex(@"^(?<cur>❯)?\s*(?<n>\d+)\.\s+(?:\[(?<chk>[✔ ])\]\s+)?(?<label>.*?)\s*$")]
    private static partial Regex RowRegex();
    [GeneratedRegex(@"^\s*─{8,}")]
    private static partial Regex SeparatorRegex();

    public static QuestionScreen? Parse(string screen)
    {
        var lines = screen.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
        var footer = lines.FindLastIndex(l => l.StartsWith("Enter to select", StringComparison.Ordinal));
        var review = lines.FindLastIndex(l => l.Trim() == "Review your answers");
        if (review > footer) return ParseReview(lines, review);
        if (footer < 0) return null;

        // The header (tab strip or single chip) is the last one above the footer.
        var header = -1;
        for (var i = footer - 1; i >= 0 && i >= footer - 60; i--)
            if (TabLineRegex().IsMatch(lines[i])) { header = i; break; }
        if (header < 0) return null;
        var tabs = ParseTabs(lines[header]);

        var firstRow = -1;
        for (var i = header + 1; i < footer; i++)
            if (RowRegex().IsMatch(lines[i])) { firstRow = i; break; }
        if (firstRow < 0) return null;
        var question = string.Join(" ", lines.Skip(header + 1).Take(firstRow - header - 1).Select(l => l.Trim()).Where(l => l.Length > 0));

        var rows = new List<QuestionRow>();
        var multi = false;
        for (var i = firstRow; i < footer; i++)
        {
            var line = lines[i];
            if (SeparatorRegex().IsMatch(line)) break; // "Chat about this" sits below the separator; not offered
            var m = RowRegex().Match(line);
            if (m.Success)
            {
                if (m.Groups["chk"].Success) multi = true;
                rows.Add(new QuestionRow(QuestionRowKind.Option, int.Parse(m.Groups["n"].Value), m.Groups["label"].Value,
                    "", m.Groups["chk"].Value == "✔", m.Groups["cur"].Success));
                continue;
            }
            var trimmed = line.Trim();
            if (trimmed.TrimStart('❯').Trim() == "Submit")
            {
                rows.Add(new QuestionRow(QuestionRowKind.Submit, 0, "Submit", "", false, trimmed.StartsWith('❯')));
                continue;
            }
            if (rows.Count > 0 && trimmed.Length > 0 && rows[^1].Kind == QuestionRowKind.Option)
            {
                var last = rows[^1];
                rows[^1] = last with { Description = (last.Description + " " + trimmed).Trim() };
            }
        }
        var numbered = rows.Where(r => r.Kind == QuestionRowKind.Option).ToList();
        if (numbered.Count < 2) return null;
        // The last numbered row is the free-text one ("Type something", or the text typed so far).
        var typeRow = numbered[^1];
        rows[rows.IndexOf(typeRow)] = typeRow with { Kind = QuestionRowKind.TypeSomething, Description = "" };
        return new QuestionScreen(false, tabs, question, multi, rows, []);
    }

    static QuestionScreen? ParseReview(List<string> lines, int review)
    {
        var tabs = review > 0 && TabLineRegex().IsMatch(lines[review - 1]) ? ParseTabs(lines[review - 1]) : [];
        var pairs = new List<(string, string)>();
        string? q = null;
        for (var i = review + 1; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("● ", StringComparison.Ordinal)) q = t[2..].Trim();
            else if (t.StartsWith("→ ", StringComparison.Ordinal) && q != null) { pairs.Add((q, t[2..].Trim())); q = null; }
            else if (RowRegex().IsMatch(lines[i])) break;
        }
        return pairs.Count == 0 ? null : new QuestionScreen(true, tabs, "", false, [], pairs);
    }

    static List<QuestionTab> ParseTabs(string line) =>
        TabRegex().Matches(line).Select(m => new QuestionTab(m.Groups[2].Value.Trim(), m.Groups[1].Value == "☒"))
            .Where(t => t.Title != "Submit").ToList();
}

/// <summary>An answer to the question on screen: the option numbers picked, and/or a typed answer.</summary>
public sealed record QuestionAnswer(IReadOnlyList<int> Selected, string? Custom);
