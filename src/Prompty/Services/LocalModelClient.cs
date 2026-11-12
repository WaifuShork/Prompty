using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Prompty.Services;

public sealed record LlmMessage(string Role, string Content);

/// <summary>
/// Talks to the configured model provider: Ollama's native API locally (it lets us set num_ctx, disable
/// "thinking" and request JSON), the OpenAI chat-completions protocol (OpenAI, Gemini, OpenRouter, Mistral,
/// Groq, local servers…), or Anthropic's Messages API. Optional request fields that a particular model
/// rejects (think, temperature, JSON mode) are dropped and the request retried.
/// </summary>
public sealed partial class LocalModelClient(AppSettings settings, string? apiKeyOverride = null)
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    ProviderInfo Provider => settings.ProviderInfo;
    string Base => settings.BaseUrl.TrimEnd('/');
    string OpenAiBase => Provider.Id == "openai-local" && !Base.EndsWith("/v1") ? Base + "/v1" : Base;
    string? ApiKey => apiKeyOverride ?? CredentialStore.Get(Provider);

    // Fields a model rejected once; not sent again for that model.
    readonly HashSet<string> _dropped = [];

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Provider.IsCloud ? 15 : 4));
        var url = Provider.Api switch
        {
            ApiKind.Ollama => $"{Base}/api/tags",
            ApiKind.Anthropic => $"{Base}/v1/models?limit=1000",
            _ => $"{OpenAiBase}/models",
        };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await Http.SendAsync(req, cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode) throw new LocalModelException(DescribeError(text, (int)resp.StatusCode, null));
        await ValidateKeyAsync(cts.Token);
        var json = JsonNode.Parse(text);
        var names = Provider.Api == ApiKind.Ollama
            ? json?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>())
            : json?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>());
        return names?.Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!.StartsWith("models/") ? s[7..] : s!) // Gemini lists "models/gemini-…"
            .Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    }

    /// <summary>
    /// OpenRouter and Hugging Face list models without checking the key, so ask them who the key belongs to.
    /// </summary>
    async Task ValidateKeyAsync(CancellationToken ct)
    {
        var url = Provider.Id switch
        {
            "openrouter" => $"{Base}/key",
            "huggingface" => "https://huggingface.co/api/whoami-v2",
            _ => null,
        };
        if (url == null) return;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new LocalModelException(DescribeError(await resp.Content.ReadAsStringAsync(ct), (int)resp.StatusCode, null));
    }

    public async Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, bool json, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in StreamAsync(messages, json, ct)) sb.Append(chunk);
        return StripThinking(sb.ToString());
    }

    /// <param name="model">Overrides the configured model (e.g. the larger prompt-generator model).</param>
    /// <param name="contextTokens">Overrides the configured context window (Ollama only).</param>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LlmMessage> messages, bool json, [EnumeratorCancellation] CancellationToken ct,
        string? model = null, int? contextTokens = null)
    {
        model ??= settings.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new LocalModelException($"No model selected for {Provider.Name}. Pick one in ⚙ Settings.");
        if (Provider.NeedsKey && string.IsNullOrEmpty(ApiKey))
            throw new LocalModelException($"Not signed in to {Provider.Name}. Open ⚙ Settings to sign in or paste an API key.");

        var stream = Provider.Api switch
        {
            ApiKind.Ollama => StreamOllamaAsync(messages, json, model, contextTokens ?? settings.ContextTokens, ct),
            ApiKind.Anthropic => StreamAnthropicAsync(messages, model, ct),
            _ => StreamOpenAiAsync(messages, json, model, ct),
        };

        // Hide <think>…</think> spans from reasoning models while streaming. Tags can be split
        // across chunks, so hold back any trailing text that could be the start of a tag.
        var inThink = false;
        var pending = "";
        await foreach (var raw in stream)
        {
            pending += raw;
            while (true)
            {
                var tag = inThink ? "</think>" : "<think>";
                var idx = pending.IndexOf(tag, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    if (!inThink && idx > 0) yield return pending[..idx];
                    pending = pending[(idx + tag.Length)..];
                    inThink = !inThink;
                    continue;
                }
                var keep = PartialTagSuffix(pending, tag);
                if (!inThink && pending.Length > keep) yield return pending[..^keep];
                pending = pending[^keep..];
                break;
            }
        }
        if (!inThink && pending.Length > 0) yield return pending;
    }

    /// <summary>Length of the longest suffix of <paramref name="s"/> that is a proper prefix of <paramref name="tag"/>.</summary>
    static int PartialTagSuffix(string s, string tag)
    {
        for (var n = Math.Min(tag.Length - 1, s.Length); n > 0; n--)
            if (s.EndsWith(tag[..n], StringComparison.Ordinal)) return n;
        return 0;
    }

    void AddAuth(HttpRequestMessage req)
    {
        var key = ApiKey;
        switch (Provider.Auth)
        {
            case AuthStyle.AnthropicKey:
                if (key != null) req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", "2023-06-01");
                break;
            case AuthStyle.AzureApiKey:
                if (key != null) req.Headers.Add("api-key", key);
                break;
            default:
                if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new("Bearer", key);
                break;
        }
        if (Provider.Id == "openrouter") req.Headers.Add("X-Title", "Prompty");
    }

    /// <summary>
    /// Sends the request; if the server rejects one of the optional fields (by naming it in a 400/422
    /// error), removes that field and retries. Each rejected field is remembered per model.
    /// </summary>
    async Task<HttpResponseMessage> SendAsync(string url, string model, JsonObject body,
        (string Field, string ErrorWord)[] optional, CancellationToken ct)
    {
        while (true)
        {
            foreach (var (field, _) in optional)
                if (_dropped.Contains($"{model}|{field}")) body.Remove(field);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            AddAuth(req);
            var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode) return resp;

            var err = await resp.Content.ReadAsStringAsync(ct);
            var status = (int)resp.StatusCode;
            resp.Dispose();
            var culprit = status is 400 or 422
                ? optional.FirstOrDefault(o => body.ContainsKey(o.Field) && err.Contains(o.ErrorWord, StringComparison.OrdinalIgnoreCase))
                : default;
            if (culprit.Field == null) throw new LocalModelException(DescribeError(err, status, model));
            _dropped.Add($"{model}|{culprit.Field}");
        }
    }

    static JsonArray ToJson(IEnumerable<LlmMessage> messages) =>
        new(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray());

    async IAsyncEnumerable<string> StreamOllamaAsync(
        IReadOnlyList<LlmMessage> messages, bool json, string model, int contextTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["keep_alive"] = "30m",
            ["messages"] = ToJson(messages),
            ["options"] = new JsonObject { ["num_ctx"] = contextTokens, ["temperature"] = settings.Temperature },
            ["think"] = false,
        };
        if (json) body["format"] = "json";

        using var resp = await SendAsync($"{Base}/api/chat", model, body, [("think", "think")], ct);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.Length == 0) continue;
            var node = JsonNode.Parse(line);
            if (node?["error"]?.GetValue<string>() is { } e) throw new LocalModelException(e);
            var content = node?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content)) yield return content;
            if (node?["done"]?.GetValue<bool>() == true) yield break;
        }
    }

    async IAsyncEnumerable<string> StreamOpenAiAsync(
        IReadOnlyList<LlmMessage> messages, bool json, string model, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["temperature"] = settings.Temperature,
            ["messages"] = ToJson(messages),
        };
        // Cloud APIs mostly support JSON mode; the prompts ask for JSON anyway, so it's dropped if refused.
        if (json && Provider.IsCloud) body["response_format"] = new JsonObject { ["type"] = "json_object" };

        using var resp = await SendAsync($"{OpenAiBase}/chat/completions", model, body,
            [("temperature", "temperature"), ("response_format", "response_format")], ct);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:")) continue; // blank lines and ": keep-alive" comments
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            var node = JsonNode.Parse(data);
            if (node?["error"] is { } error) throw new LocalModelException((error as JsonObject)?["message"]?.ToString() ?? error.ToJsonString());
            var content = node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content)) yield return content;
        }
    }

    async IAsyncEnumerable<string> StreamAnthropicAsync(
        IReadOnlyList<LlmMessage> messages, string model, [EnumeratorCancellation] CancellationToken ct)
    {
        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["stream"] = true,
            ["temperature"] = settings.Temperature,
            ["messages"] = ToJson(messages.Where(m => m.Role != "system")),
        };
        if (system.Length > 0) body["system"] = system;

        using var resp = await SendAsync($"{Base}/v1/messages", model, body, [("temperature", "temperature")], ct);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:")) continue;
            var node = JsonNode.Parse(line[5..].Trim());
            switch (node?["type"]?.GetValue<string>())
            {
                case "content_block_delta":
                    var delta = node["delta"];
                    if (delta?["type"]?.GetValue<string>() == "text_delta"
                        && delta["text"]?.GetValue<string>() is { Length: > 0 } text) yield return text;
                    break;
                case "error":
                    throw new LocalModelException(node["error"]?["message"]?.ToString() ?? "Anthropic API error");
                case "message_stop":
                    yield break;
            }
        }
    }

    string DescribeError(string body, int status, string? model)
    {
        var detail = ExtractMessage(body);
        // Some APIs (Gemini, xAI) report a bad key as 400.
        if (status is 401 or 403 || (status == 400 && detail.Contains("api key", StringComparison.OrdinalIgnoreCase)))
            return $"{Provider.Name} rejected the API key ({status}). Check it in ⚙ Settings. {detail}".Trim();
        if (status == 404 && model != null && body.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return Provider.Api == ApiKind.Ollama
                ? $"Model '{model}' is not installed. Run: ollama pull {model}"
                : $"{Provider.Name} has no model '{model}'. Pick one from the list in ⚙ Settings. {detail}".Trim();
        if (status == 429) return $"{Provider.Name} rate limit or quota reached (429). {detail}".Trim();
        return $"{Provider.Name} returned {status}: {detail}";
    }

    /// <summary>Pulls the human-readable message out of the usual JSON error shapes.</summary>
    static string ExtractMessage(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject n) return body;
            var error = n["error"];
            var m = (error as JsonObject)?["message"] ?? error ?? n["message"] ?? n["detail"];
            if (m is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            if (m != null) return m.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return body.Length > 300 ? body[..300] + "…" : body;
    }

    public static string StripThinking(string s) => ThinkRegex().Replace(s, "").Trim();

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex ThinkRegex();
}

public sealed class LocalModelException(string message) : Exception(message);
