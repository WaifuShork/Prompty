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

    /// <summary>"" is the draft shown when no session is selected.</summary>
    static string FileStem(string key) =>
        key.Length == 0 ? "_no-session" : string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public static string PathFor(string key) => Path.Combine(Dir, FileStem(key) + ".md");
    public static string RequestPathFor(string key) => Path.Combine(Dir, FileStem(key) + ".request.txt");

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
            }
            else
            {
                File.WriteAllText(PathFor(Key), text);
                if (GeneratorText.Length > 0) File.WriteAllText(RequestPathFor(Key), GeneratorText);
                else File.Delete(RequestPathFor(Key));
            }
            Dirty = false;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
