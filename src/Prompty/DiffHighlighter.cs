using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Prompty;

/// <summary>Colors added/removed diff lines across the full width, like the Claude Code terminal.</summary>
sealed class DiffHighlighter : DocumentColorizingTransformer, IBackgroundRenderer
{
    static readonly Brush AddedBack = Frozen(0x1E, 0x3A, 0x26), RemovedBack = Frozen(0x42, 0x1F, 0x25);
    static readonly Brush AddedFore = Frozen(0xB8, 0xE6, 0xB4), RemovedFore = Frozen(0xF2, 0xB8, 0xBC);

    /// <summary>Line start offset → '+' or '-'.</summary>
    public Dictionary<int, char> Lines { get; set; } = [];

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        foreach (var vl in textView.VisualLines)
        {
            if (!Lines.TryGetValue(vl.FirstDocumentLine.Offset, out var kind)) continue;
            var y = vl.VisualTop - textView.VerticalOffset;
            drawingContext.DrawRectangle(kind == '+' ? AddedBack : RemovedBack, null,
                new Rect(0, y, textView.ActualWidth, vl.Height));
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!Lines.TryGetValue(line.Offset, out var kind) || line.Length == 0) return;
        var brush = kind == '+' ? AddedFore : RemovedFore;
        ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(brush));
    }

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
