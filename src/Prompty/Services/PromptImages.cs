using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Prompty.Services;

/// <summary>
/// Images pasted or dropped into a prompt. Each is saved as a file under %APPDATA%\Prompty\images and shows
/// up in the text as a Claude Code-style "[Image #N]" token; the draft keeps the N → file mapping.
/// </summary>
public static partial class PromptImages
{
    public static string Dir { get; } = Path.Combine(AppSettings.Dir, "images");

    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    [GeneratedRegex(@"\[Image #(\d+)\]")]
    public static partial Regex TokenRegex();

    public static string Token(int number) => $"[Image #{number}]";

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path);

    /// <summary>
    /// Image files to insert for this clipboard/drop data: dropped or copied image files are copied in;
    /// a bitmap (screenshot, copied picture) is saved as PNG. Empty when the data holds no image.
    /// </summary>
    public static List<string> SaveFrom(IDataObject data)
    {
        var saved = new List<string>();
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files.Where(IsImageFile)) saved.Add(CopyIn(f));
            return saved;
        }
        // Prefer a real PNG stream (browsers, Snipping Tool): WPF's bitmap conversion can lose transparency.
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
        {
            saved.Add(Write(png.ToArray(), ".png"));
            return saved;
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            saved.Add(SaveBitmap(bmp));
        return saved;
    }

    public static bool HasImage(IDataObject data) =>
        (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsImageFile))
        || data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap);

    static string SaveBitmap(BitmapSource bmp)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Write(ms.ToArray(), ".png");
    }

    static string CopyIn(string source)
    {
        var dest = NewPath(Path.GetExtension(source).ToLowerInvariant());
        File.Copy(source, dest);
        return dest;
    }

    static string Write(byte[] bytes, string ext)
    {
        var path = NewPath(ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static string NewPath(string ext)
    {
        Directory.CreateDirectory(Dir);
        return Path.Combine(Dir, $"img-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{ext}");
    }

    /// <summary>Loads a bitmap without keeping the file locked, scaled down for display.</summary>
    public static BitmapImage? Load(string path, int decodeHeight)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelHeight = decodeHeight;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException) { return null; }
    }
}
