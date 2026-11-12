using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Prompty.Services;

namespace Prompty;

/// <summary>
/// Draws each "[Image #N]" token in the prompt editor as a small thumbnail chip, in place in the text.
/// The token stays plain text in the document, so copy, undo and saving all keep working; the caret
/// treats the chip as one character. Hover for a larger preview, double-click to open the image.
/// </summary>
sealed class ImageTokenGenerator(Func<int, string?> resolve) : VisualLineElementGenerator
{
    readonly Dictionary<(string, int), ImageSource?> _cache = [];

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var doc = CurrentContext.Document;
        var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        if (startOffset >= end) return -1;
        var m = PromptImages.TokenRegex().Match(doc.GetText(startOffset, end - startOffset));
        return m.Success ? startOffset + m.Index : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var doc = CurrentContext.Document;
        var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        var m = PromptImages.TokenRegex().Match(doc.GetText(offset, end - offset));
        if (!m.Success || m.Index != 0) return null;
        var number = int.Parse(m.Groups[1].Value);
        var path = resolve(number);
        return new InlineObjectElement(m.Length, Chip(number, path));
    }

    ImageSource? Cached(string path, int height)
    {
        if (!_cache.TryGetValue((path, height), out var img)) _cache[(path, height)] = img = PromptImages.Load(path, height);
        return img;
    }

    UIElement Chip(int number, string? path)
    {
        var missing = path == null || !File.Exists(path);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (!missing && Cached(path!, 56) is { } thumb)
            panel.Children.Add(new Image { Source = thumb, Height = 28, MaxWidth = 64, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 5, 0) });
        panel.Children.Add(new TextBlock
        {
            Text = missing ? $"Image #{number} (missing)" : $"Image #{number}",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = missing ? new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75)) : new SolidColorBrush(Color.FromRgb(0xD9, 0xA2, 0x7A)),
        });
        var chip = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x35)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x52)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(3, 1, 6, 1),
            Margin = new Thickness(1, 0, 1, 0),
            Cursor = missing ? Cursors.Arrow : Cursors.Hand,
        };
        if (!missing)
        {
            chip.ToolTip = new ToolTip
            {
                Content = new StackPanel
                {
                    Children =
                    {
                        new Image { Source = Cached(path!, 360), MaxHeight = 360, MaxWidth = 560, Stretch = Stretch.Uniform },
                        new TextBlock { Text = $"{Path.GetFileName(path)}  ·  double-click to open", Margin = new Thickness(0, 4, 0, 0), FontSize = 11 },
                    },
                },
            };
            chip.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true });
                e.Handled = true;
            };
        }
        return chip;
    }
}
