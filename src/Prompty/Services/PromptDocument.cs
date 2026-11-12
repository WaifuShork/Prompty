using System.Text.RegularExpressions;

namespace Prompty.Services;

/// <summary>
/// A section of a prompt: either a markdown heading ("## Context") or a line-level XML tag
/// block ("&lt;context&gt; … &lt;/context&gt;"). Offsets index into the document text.
/// </summary>
public sealed record PromptSection(string Title, int HeadingStart, int BodyStart, int BodyEnd, string Body, bool IsXml)
{
    public bool IsEmpty => PromptDocument.LooksEmpty(Body);
    public int WordCount => Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    public string Label => IsXml ? $"<{Title}>" : Title;
}

public static partial class PromptDocument
{
    public static List<PromptSection> Parse(string text)
    {
        var headings = new List<(string Title, int Start, int BodyStart, int Level, bool Xml, string? CloseTag)>();
        var inFence = false;
        var offset = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var lineStart = offset;
            offset += rawLine.Length + 1;
            var line = rawLine.TrimEnd('\r');
            var bodyStart = Math.Min(offset, text.Length);

            if (line.TrimStart().StartsWith("```")) { inFence = !inFence; continue; }
            if (inFence) continue;

            var h = HeadingRegex().Match(line);
            if (h.Success)
            {
                headings.Add((h.Groups[2].Value.Trim(), lineStart, bodyStart, h.Groups[1].Length, false, null));
                continue;
            }
            var open = XmlOpenRegex().Match(line);
            if (open.Success)
                headings.Add((open.Groups[1].Value, lineStart, bodyStart, 0, true, $"</{open.Groups[1].Value}>"));
        }

        var sections = new List<PromptSection>();
        for (var i = 0; i < headings.Count; i++)
        {
            var hd = headings[i];
            int end;
            if (hd.Xml)
            {
                var close = text.IndexOf(hd.CloseTag!, hd.BodyStart, StringComparison.Ordinal);
                if (close < 0) continue;
                end = close;
                // Skip any headings nested inside the tag block.
                while (i + 1 < headings.Count && headings[i + 1].Start < close) i++;
            }
            else
            {
                end = i + 1 < headings.Count ? headings[i + 1].Start : text.Length;
            }
            sections.Add(new PromptSection(hd.Title, hd.Start, hd.BodyStart, end, text[hd.BodyStart..end], hd.Xml));
        }
        return sections;
    }

    public static PromptSection? Find(string text, string title)
    {
        var norm = Normalize(title);
        return Parse(text).FirstOrDefault(s => Normalize(s.Title) == norm);
    }

    public static PromptSection? At(string text, int offset) =>
        Parse(text).LastOrDefault(s => s.HeadingStart <= offset && offset <= s.BodyEnd);

    static string Normalize(string s) => s.Trim().Trim('<', '>', '#', '*', ':').Trim().ToLowerInvariant();

    public static bool LooksEmpty(string body)
    {
        var t = CommentRegex().Replace(body, "").Trim();
        if (t.Length == 0) return true;
        if (PlaceholderRegex().Replace(t, "").Trim().Length == 0) return true;
        return t is "..." or "…" or "TODO" or "TBD" or "-" or "?";
    }

    public static List<string> Placeholders(string text) =>
        PlaceholderRegex().Matches(text).Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();

    public static string FillPlaceholders(string text, IReadOnlyDictionary<string, string> values) =>
        PlaceholderRegex().Replace(text, m =>
            values.TryGetValue(m.Groups[1].Value.Trim(), out var v) && !string.IsNullOrWhiteSpace(v) ? v : m.Value);

    /// <summary>Body text formatted to sit between a heading/tag and whatever follows it.</summary>
    public static string FormatBody(PromptSection s, string newBody, bool atDocumentEnd)
    {
        var b = newBody.Trim('\r', '\n');
        if (s.IsXml) return b + "\n";
        return atDocumentEnd ? "\n" + b + "\n" : "\n" + b + "\n\n";
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*<([A-Za-z_][\w\-]*)>\s*$")]
    private static partial Regex XmlOpenRegex();

    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();
}
