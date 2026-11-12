using System.Text.Json;
using System.Text.Json.Serialization;
using Prompty.Services;

namespace Prompty;

public sealed class AppSettings
{
    /// <summary>Id from <see cref="Providers.All"/>: "ollama" (default), "openai-local", or a cloud platform.</summary>
    public string Provider { get; set; } = "ollama";

    [JsonIgnore] public ProviderInfo ProviderInfo => Providers.Get(Provider);

    /// <summary>URL and model choices remembered per provider, so switching back restores them.</summary>
    public Dictionary<string, ProviderProfile> Profiles { get; set; } = [];
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:3b";
    public int ContextTokens { get; set; } = 8192;
    public double Temperature { get; set; } = 0.3;

    /// <summary>Larger model used by the prompt generator, which writes a whole prompt from a short request.</summary>
    public string GeneratorModel { get; set; } = "qwen3:8b";
    public int GeneratorContextTokens { get; set; } = 16384;

    public bool AutoAnalyze { get; set; } = true;
    public int AutoAnalyzeDelayMs { get; set; } = 2500;

    /// <summary>Max characters of Claude conversation fed to the local model.</summary>
    public int ContextMaxChars { get; set; } = 14000;

    public int McpPort { get; set; } = 47821;

    // ── updates ──
    public bool CheckForUpdatesOnLaunch { get; set; } = true;
    /// <summary>"Skip this version": no launch prompt for exactly this version.</summary>
    public string? SkippedUpdateVersion { get; set; }
    /// <summary>"Don't ask until next major": no launch prompts for releases below this major version.</summary>
    public int MuteUpdatesUntilMajor { get; set; }

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Prompty");

    static string FilePath => Path.Combine(Dir, "settings.json");
    public static string DraftPath => Path.Combine(Dir, "draft.md");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                s.Provider = Providers.Get(s.Provider).Id; // "Ollama"/"OpenAI" from older versions
                return s;
            }
        }
        catch { /* corrupt settings: fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Switches provider, remembering the current one's URL and models.</summary>
    public void SwitchProvider(string id)
    {
        Profiles[Provider] = new ProviderProfile(BaseUrl, Model, GeneratorModel);
        var p = Providers.Get(id);
        Provider = p.Id;
        var prof = Profiles.GetValueOrDefault(p.Id) ?? new ProviderProfile(p.DefaultUrl, p.FastModel, p.GeneratorModel);
        (BaseUrl, Model, GeneratorModel) = (prof.BaseUrl, prof.Model, prof.GeneratorModel);
    }

    public string McpUrl => $"http://127.0.0.1:{McpPort}/mcp";
    public string ClaudeAddCommand => $"claude mcp add --transport http --scope user prompty {McpUrl}";
}

public sealed record ProviderProfile(string BaseUrl, string Model, string GeneratorModel);
