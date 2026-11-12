using System.Text.Json;
using ICSharpCode.AvalonEdit.Document;

namespace Prompty.Services;

/// <summary>
/// The prompt being written for one Claude session: its editor document (with its own undo history),
/// the generator request, its suggestion cards and any file it was opened from. Saved to
/// %APPDATA%\Prompty\drafts\&lt;sessionId&gt;.md (+ .request.txt for the generator box).
/// </summary>
public sealed class DraftState
{
    /// <summary>Placeholder before the first session loads; never saved.</summary>
    public const string NoKey = "\0";

    public static string Dir { get; } = Path.Combine(AppSettings.Dir, "drafts");

    public required string Key { get; init; }
    public required TextDocument Doc { get; init; }
    public string GeneratorText { get; set; } = "";
    public List<SuggestionVm> Suggestions { get; set; } = [];
    public CancellationTokenSource? GenerateCts { get; set; }
    public string LastAnalyzed { get; set; } = "";
    public string? FilePath { get; set; }
    public bool Dirty { get; set; }

    /// <summary>"[Image #N]" tokens in this draft → the saved image files. Numbers are never reused.</summary>
    public Dictionary<int, string> Images { get; set; } = [];
    public int NextImageNumber { get; set; } = 1;

    public int AddImage(string path)
    {
        var n = NextImageNumber++;
        Images[n] = path;
        Dirty = true;
        return n;
    }

    public string? ImagePath(int number) => Images.GetValueOrDefault(number);

    /// <summary>Image tokens in the text, in order, with their files (tokens whose file is unknown are skipped).</summary>
    public List<(int Number, string Path)> ImagesIn(string text) =>
        PromptImages.TokenRegex().Matches(text)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Where(n => Images.ContainsKey(n)).Distinct()
            .Select(n => (n, Images[n])).ToList();

    /// <summary>"" is the draft shown when no session is selected.</summary>
    static string FileStem(string key) =>
        key.Length == 0 ? "_no-session" : string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public static string PathFor(string key) => Path.Combine(Dir, FileStem(key) + ".md");
    public static string RequestPathFor(string key) => Path.Combine(Dir, FileStem(key) + ".request.txt");
    public static string ImagesPathFor(string key) => Path.Combine(Dir, FileStem(key) + ".images.json");

    sealed record ImageIndex(int Next, Dictionary<int, string> Images);

    public void LoadImages()
    {
        try
        {
            if (!File.Exists(ImagesPathFor(Key))) return;
            var index = JsonSerializer.Deserialize<ImageIndex>(File.ReadAllText(ImagesPathFor(Key)));
            if (index == null) return;
            Images = index.Images;
            NextImageNumber = Math.Max(index.Next, Images.Count == 0 ? 1 : Images.Keys.Max() + 1);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    public void Save()
    {
        if (Key == NoKey) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var text = Doc.Text;
            // An untouched template with no request isn't worth a file.
            if (GeneratorText.Length == 0 && Templates.All.Any(t => t.Body == text))
            {
                File.Delete(PathFor(Key));
                File.Delete(RequestPathFor(Key));
                File.Delete(ImagesPathFor(Key));
            }
            else
            {
                File.WriteAllText(PathFor(Key), text);
                if (GeneratorText.Length > 0) File.WriteAllText(RequestPathFor(Key), GeneratorText);
                else File.Delete(RequestPathFor(Key));
                if (Images.Count > 0) File.WriteAllText(ImagesPathFor(Key), JsonSerializer.Serialize(new ImageIndex(NextImageNumber, Images)));
                else File.Delete(ImagesPathFor(Key));
            }
            Dirty = false;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
