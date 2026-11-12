using System.Text;

namespace Prompty.Services;

/// <summary>A collapsible region: hides [Start, End) and shows a "+N lines" placeholder.</summary>
public sealed record OutputFold(int Start, int End, string Id, int LineCount);

public sealed class RenderedOutput
{
    public string Text { get; init; } = "";
    public List<OutputFold> Folds { get; init; } = [];

    /// <summary>Diff lines keyed by the document offset where the line starts; value is '+' or '-'.</summary>
    public Dictionary<int, char> DiffLines { get; init; } = [];

    public static RenderedOutput FromText(string text) => new() { Text = text };
}

sealed class OutputBuilder
{
    readonly StringBuilder _sb = new();
    readonly List<OutputFold> _folds = [];
    readonly Dictionary<int, char> _diff = [];

    public int Length => _sb.Length;

    public void Line(string text, char diff = '\0')
    {
        if (diff != '\0') _diff[_sb.Length] = diff;
        _sb.Append(text).Append('\n');
    }

    /// <summary>
    /// Appends lines that are collapsed by default. The fold starts at the newline ending the previous
    /// line, so when collapsed the placeholder sits at the end of the summary line.
    /// </summary>
    public void Fold(string id, IReadOnlyList<(string Text, char Diff)> lines)
    {
        if (lines.Count == 0 || _sb.Length == 0) return;
        var start = _sb.Length - 1;
        foreach (var (text, diff) in lines) Line(text, diff);
        _folds.Add(new OutputFold(start, _sb.Length - 1, id, lines.Count));
    }

    public RenderedOutput Build() => new() { Text = _sb.ToString(), Folds = _folds, DiffLines = _diff };
}
