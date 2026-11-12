using System.Text;
using System.Text.Json;

namespace Prompty.Services;

public sealed record AnalysisSuggestion(string Section, string Action, string Title, string Reason, string Text);

/// <summary>Prompt recipes for the local model. Kept short and explicit: small models do best with tight instructions.</summary>
public sealed class PromptAssistant(LocalModelClient model)
{
    const string Role =
        "You are Prompty, an expert prompt engineer built into a prompt editor. The user is drafting the NEXT " +
        "message they will send to Claude, an AI coding assistant, in an ongoing conversation. You are given a " +
        "digest of that conversation and the current draft.";

    const string Grounding =
        "Only use facts that appear in the conversation or the draft: file names, errors, decisions, constraints, " +
        "goals. Never invent file names, APIs, numbers or requirements. Keep the user's intent and voice. Be concise " +
        "and concrete; write instructions addressed to Claude.";

    public async Task<List<AnalysisSuggestion>> AnalyzeAsync(string context, string draft, CancellationToken ct)
    {
        var sections = PromptDocument.Parse(draft);
        var outline = new StringBuilder();
        foreach (var s in sections)
            outline.AppendLine($"- \"{s.Title}\" ({(s.IsEmpty ? "EMPTY" : $"{s.WordCount} words")})");
        if (sections.Count == 0) outline.AppendLine("- (no sections; the whole draft is free text, use section \"Prompt\")");

        var system = $$"""
            {{Role}}
            Suggest the most valuable improvements to the draft:
            - "fill": write content for an EMPTY section using facts from the conversation.
            - "rewrite": replace a vague, ambiguous or incomplete section with a sharper version.
            - "add": a missing section that would clearly help (e.g. Constraints, Acceptance criteria, Relevant files).
            {{Grounding}}
            Return at most 4 suggestions, best first. Skip sections that are already good.
            Respond with JSON only, in this exact shape:
            {"suggestions":[{"section":"<exact section title, or new title for add>","action":"fill|rewrite|add","title":"<label, max 8 words>","reason":"<one short sentence>","text":"<complete new body for the section, markdown, without the heading>"}]}
            If nothing would meaningfully improve the draft, respond {"suggestions":[]}.
            """;

        var user = $"""
            ## Conversation digest
            {Or(context, "(no conversation available)")}

            ## Draft sections
            {outline}
            ## Current draft
            <draft>
            {draft}
            </draft>
            """;

        var raw = await model.CompleteAsync([new("system", system), new("user", user)], json: true, ct);
        return ParseSuggestions(raw);
    }

    public IAsyncEnumerable<string> StreamSectionAsync(string context, string draft, PromptSection section, string? instruction, CancellationToken ct)
    {
        var verb = section.IsEmpty ? "Write the content for" : "Rewrite";
        var system = $"""
            {Role}
            {verb} ONE section of the draft: "{section.Title}".
            {Grounding}
            Output ONLY the new body text of that section in markdown: no heading, no tags, no preamble, no quotes.
            """;
        var user = $"""
            ## Conversation digest
            {Or(context, "(no conversation available)")}

            ## Current draft
            <draft>
            {draft}
            </draft>

            ## Section to {(section.IsEmpty ? "fill" : "rewrite")}: "{section.Title}"
            {(section.IsEmpty ? "(currently empty)" : section.Body.Trim())}
            {(string.IsNullOrWhiteSpace(instruction) ? "" : $"\n## Extra instruction from the user\n{instruction}")}
            """;
        return model.StreamAsync([new("system", system), new("user", user)], json: false, ct);
    }

    public IAsyncEnumerable<string> StreamRewriteAsync(string context, string draft, string selection, string? instruction, CancellationToken ct)
    {
        var system = $"""
            {Role}
            Rewrite the SELECTED passage of the draft{(string.IsNullOrWhiteSpace(instruction) ? " to be clearer, more specific and better grounded in the conversation" : " following the user's instruction")}.
            {Grounding}
            Output ONLY the replacement text for the selection. No preamble, no quotes, no explanation.
            """;
        var user = $"""
            ## Conversation digest
            {Or(context, "(no conversation available)")}

            ## Full draft (for reference)
            <draft>
            {draft}
            </draft>

            ## Selected passage
            <selection>
            {selection}
            </selection>
            {(string.IsNullOrWhiteSpace(instruction) ? "" : $"\n## Instruction\n{instruction}")}
            """;
        return model.StreamAsync([new("system", system), new("user", user)], json: false, ct);
    }

    /// <summary>
    /// Prompt generator: expands a short request into a complete, well-structured prompt for Claude,
    /// grounded in the conversation and the project. Uses the larger generator model.
    /// </summary>
    public IAsyncEnumerable<string> StreamGenerateAsync(string context, string project, string request, string existingDraft,
        string generatorModel, int contextTokens, CancellationToken ct)
    {
        var system = $"""
            You are Prompty, an expert prompt engineer. Turn the user's short request into a complete, detailed
            prompt they will send to Claude Code, an AI coding agent working in their project. You are given the
            recent conversation with Claude and an overview of the project (instruction files and file tree).

            Write the prompt in the user's voice, addressed to Claude, as markdown. Use these headings, each alone on
            its own line ("## Goal", "## Context", ...) with the content on the lines below it:
            - Goal: what to build or change and why, in 1-3 sentences.
            - Context: the relevant state from the conversation and project: what exists, what was tried, what is broken.
            - Requirements: a numbered list of specific, testable behaviors.
            - Relevant files: a bullet list of paths that appear in the project overview or conversation, each with why it matters. Omit the section if none apply.
            - Constraints: things to preserve or avoid (conventions, other features, things the user said not to touch).
            - Done when: concrete checks that prove the work is finished (build, test, how to verify by hand).

            Rules:
            - Expand the request into the details a senior engineer would spell out: edge cases, error states, UI details, verification.
            - Ground every fact in the conversation or project overview. Never invent file names, APIs, versions or numbers.
              Where something is unknown, tell Claude to find out (e.g. "find where X is handled") instead of guessing.
            - Keep everything the user asked for; do not change their intent or add unrelated features.
            - Output ONLY the prompt. No preamble, no closing remarks, no code fences around the whole prompt.
            """;
        var user = $"""
            ## Project overview
            {Or(project, "(no project information)")}

            ## Conversation digest
            {Or(context, "(no conversation available)")}
            {(string.IsNullOrWhiteSpace(existingDraft) ? "" : $"\n## The user's current draft (merge anything useful from it)\n<draft>\n{existingDraft}\n</draft>\n")}
            ## Request to expand into a full prompt
            <request>
            {request}
            </request>
            """;
        return model.StreamAsync([new("system", system), new("user", user)], json: false, ct, generatorModel, contextTokens);
    }

    public IAsyncEnumerable<string> StreamContinueAsync(string context, string before, string after, CancellationToken ct)
    {
        var system = $"""
            {Role}
            Continue the draft at the cursor position marked <CURSOR/>. Write the next sentence or two (at most 60 words)
            that the user most likely wants, consistent with the surrounding text.
            {Grounding}
            Output ONLY the text to insert at the cursor.
            """;
        var user = $"""
            ## Conversation digest
            {Or(context, "(no conversation available)")}

            ## Draft
            {before}<CURSOR/>{after}
            """;
        return model.StreamAsync([new("system", system), new("user", user)], json: false, ct);
    }

    public async Task<Dictionary<string, string>> FillPlaceholdersAsync(string context, string draft, IReadOnlyList<string> names, CancellationToken ct)
    {
        var system = $$$"""
            {{{Role}}}
            The draft contains placeholders written as {{name}}. Provide a value for each placeholder using the conversation.
            {{{Grounding}}}
            If the conversation does not contain the information, use an empty string for that placeholder.
            Respond with JSON only: {"values":{"<placeholder name>":"<value>"}}
            """;
        var user = $"""
            ## Conversation digest
            {Or(context, "(no conversation available)")}

            ## Draft
            {draft}

            ## Placeholders
            {string.Join("\n", names.Select(n => "- " + n))}
            """;
        var raw = await model.CompleteAsync([new("system", system), new("user", user)], json: true, ct);
        var result = new Dictionary<string, string>();
        using var doc = TryParseObject(raw);
        if (doc?.RootElement.TryGetProperty("values", out var values) == true && values.ValueKind == JsonValueKind.Object)
            foreach (var p in values.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) result[p.Name] = p.Value.GetString()!;
        return result;
    }

    static List<AnalysisSuggestion> ParseSuggestions(string raw)
    {
        var list = new List<AnalysisSuggestion>();
        using var doc = TryParseObject(raw);
        if (doc == null) return list;

        var root = doc.RootElement;
        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (!root.TryGetProperty("suggestions", out arr) || arr.ValueKind != JsonValueKind.Array) return list;

        foreach (var s in arr.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            string Get(string name) => s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
            var text = Get("text");
            var section = Get("section");
            if (text.Length == 0) continue;
            var action = Get("action").ToLowerInvariant() switch { "fill" => "fill", "add" => "add", _ => "rewrite" };
            list.Add(new(section.Length > 0 ? section : "Prompt", action, Or(Get("title"), $"{action} {section}"), Get("reason"), text));
        }
        return list;
    }

    /// <summary>Small models sometimes wrap JSON in prose or code fences; take the outermost object.</summary>
    static JsonDocument? TryParseObject(string raw)
    {
        var s = LocalModelClient.StripThinking(raw);
        var start = s.IndexOfAny(['{', '[']);
        var end = s.LastIndexOfAny(['}', ']']);
        if (start < 0 || end <= start) return null;
        try { return JsonDocument.Parse(s[start..(end + 1)]); }
        catch (JsonException) { return null; }
    }

    static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
}
