namespace Prompty.Services;

public enum ApiKind { Ollama, OpenAi, Anthropic }

/// <summary>How the API key is sent.</summary>
public enum AuthStyle { Bearer, AzureApiKey, AnthropicKey }

/// <summary>
/// A model platform Prompty can talk to. Most cloud platforms speak the OpenAI chat-completions protocol;
/// Anthropic has its own Messages API; Ollama's native API is used locally for num_ctx and think control.
/// The default models are starting points: "Test connection" in Settings loads the provider's real list.
/// </summary>
public sealed record ProviderInfo(
    string Id,
    string Name,
    ApiKind Api,
    string DefaultUrl,
    bool IsCloud,
    string FastModel,
    string GeneratorModel,
    string? KeyUrl = null,
    string[]? EnvVars = null,
    AuthStyle Auth = AuthStyle.Bearer,
    bool SupportsSignIn = false,
    string? Note = null)
{
    public string Display => IsCloud ? $"☁  {Name}" : $"⌂  {Name}";
    public bool NeedsKey => IsCloud;
    public override string ToString() => Display;
}

public static class Providers
{
    public static readonly IReadOnlyList<ProviderInfo> All =
    [
        // ── local ──
        new("ollama", "Ollama (local)", ApiKind.Ollama, "http://localhost:11434", false, "qwen2.5:3b", "qwen3:8b"),
        new("openai-local", "Local OpenAI-compatible server (LM Studio, llama.cpp, vLLM)", ApiKind.OpenAi,
            "http://localhost:1234/v1", false, "", ""),

        // ── cloud ──
        new("anthropic", "Anthropic (Claude)", ApiKind.Anthropic, "https://api.anthropic.com", true,
            "claude-haiku-5-5", "claude-sonnet-5-5", "https://console.anthropic.com/settings/keys", ["ANTHROPIC_API_KEY"],
            AuthStyle.AnthropicKey),
        new("openai", "OpenAI", ApiKind.OpenAi, "https://api.openai.com/v1", true,
            "gpt-5-mini", "gpt-5", "https://platform.openai.com/api-keys", ["OPENAI_API_KEY"]),
        new("gemini", "Google Gemini", ApiKind.OpenAi, "https://generativelanguage.googleapis.com/v1beta/openai", true,
            "gemini-2.5-flash", "gemini-2.5-pro", "https://aistudio.google.com/apikey", ["GEMINI_API_KEY", "GOOGLE_API_KEY"]),
        new("openrouter", "OpenRouter", ApiKind.OpenAi, "https://openrouter.ai/api/v1", true,
            "openai/gpt-5-mini", "anthropic/claude-sonnet-5.5", "https://openrouter.ai/settings/keys", ["OPENROUTER_API_KEY"],
            SupportsSignIn: true, Note: "One account for hundreds of models from every major lab. Sign in with your browser."),
        new("mistral", "Mistral AI", ApiKind.OpenAi, "https://api.mistral.ai/v1", true,
            "mistral-small-latest", "mistral-large-latest", "https://console.mistral.ai/api-keys", ["MISTRAL_API_KEY"]),
        new("groq", "Groq", ApiKind.OpenAi, "https://api.groq.com/openai/v1", true,
            "llama-3.1-8b-instant", "llama-3.3-70b-versatile", "https://console.groq.com/keys", ["GROQ_API_KEY"]),
        new("deepseek", "DeepSeek", ApiKind.OpenAi, "https://api.deepseek.com/v1", true,
            "deepseek-chat", "deepseek-chat", "https://platform.deepseek.com/api_keys", ["DEEPSEEK_API_KEY"]),
        new("xai", "xAI (Grok)", ApiKind.OpenAi, "https://api.x.ai/v1", true,
            "grok-3-mini", "grok-4", "https://console.x.ai", ["XAI_API_KEY"]),
        new("together", "Together AI", ApiKind.OpenAi, "https://api.together.xyz/v1", true,
            "meta-llama/Llama-3.3-70B-Instruct-Turbo", "meta-llama/Llama-3.3-70B-Instruct-Turbo",
            "https://api.together.ai/settings/api-keys", ["TOGETHER_API_KEY"]),
        new("fireworks", "Fireworks AI", ApiKind.OpenAi, "https://api.fireworks.ai/inference/v1", true,
            "accounts/fireworks/models/llama-v3p1-8b-instruct", "accounts/fireworks/models/llama-v3p3-70b-instruct",
            "https://fireworks.ai/account/api-keys", ["FIREWORKS_API_KEY"]),
        new("cerebras", "Cerebras", ApiKind.OpenAi, "https://api.cerebras.ai/v1", true,
            "llama3.1-8b", "llama-3.3-70b", "https://cloud.cerebras.ai", ["CEREBRAS_API_KEY"]),
        new("huggingface", "Hugging Face Inference Providers", ApiKind.OpenAi, "https://router.huggingface.co/v1", true,
            "meta-llama/Llama-3.1-8B-Instruct", "Qwen/Qwen3.8-27B", "https://huggingface.co/settings/tokens",
            ["HF_TOKEN", "HUGGINGFACE_API_KEY"]),
        new("azure", "Azure OpenAI", ApiKind.OpenAi, "https://YOUR-RESOURCE.openai.azure.com/openai/v1", true,
            "", "", "https://portal.azure.com", ["AZURE_OPENAI_API_KEY"], AuthStyle.AzureApiKey,
            Note: "Put your resource name in the URL and use your deployment names as the models."),
        new("custom", "Other cloud API (OpenAI-compatible)", ApiKind.OpenAi, "https://", true, "", "",
            Note: "Any service with an OpenAI-compatible /chat/completions endpoint."),
    ];

    public static ProviderInfo Get(string? id)
    {
        // Settings from before cloud support stored "Ollama" / "OpenAI" (the latter meant a local server).
        if (string.Equals(id, "OpenAI", StringComparison.Ordinal)) id = "openai-local";
        return All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }
}
