using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Prompty.Services;

/// <summary>Runs an MCP server (Streamable HTTP) on loopback inside the app process.</summary>
public static class McpHost
{
    public static async Task<WebApplication> StartAsync(PromptBridge bridge, int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(bridge);
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "prompty", Version = "0.1.0" })
            .WithHttpTransport()
            .WithTools<PromptyTools>()
            .WithPrompts<PromptyPrompts>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
    }
}

[McpServerToolType]
public sealed class PromptyTools(PromptBridge bridge)
{
    [McpServerTool(Name = "share_context"), Description(
        "Share a concise summary of the current conversation with the user's Prompty prompt editor so its local " +
        "model can tailor autofill and rewrite suggestions. Include the goal, key decisions, relevant files, " +
        "constraints and open questions. Call this when the user mentions Prompty or before wait_for_prompt.")]
    public string ShareContext([Description("Markdown summary of the conversation so far.")] string summary)
    {
        bridge.ShareContext(summary);
        return "Context shared with Prompty.";
    }

    [McpServerTool(Name = "wait_for_prompt"), Description(
        "Ask the user to write their next instruction in the Prompty editor and wait until they press " +
        "'Send to Claude'. Returns the finished prompt, which you should treat as the user's next message.")]
    public async Task<string> WaitForPrompt(
        [Description("Optional: what you need from the user; shown as a banner in Prompty.")] string? request = null,
        [Description("Seconds to wait before giving up (default 600).")] int timeout_seconds = 600,
        CancellationToken cancellationToken = default)
    {
        var prompt = await bridge.WaitForPromptAsync(request, TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 5, 3600)), cancellationToken);
        return prompt ?? "The user has not sent a prompt from Prompty yet. Ask them in chat or call wait_for_prompt again.";
    }

    [McpServerTool(Name = "get_draft", ReadOnly = true), Description(
        "Read the prompt the user is currently drafting in Prompty (may be unfinished).")]
    public string GetDraft() =>
        string.IsNullOrWhiteSpace(bridge.CurrentDraft) ? "(The Prompty editor is empty.)" : bridge.CurrentDraft;

    [McpServerTool(Name = "seed_draft"), Description(
        "Propose a draft prompt in the Prompty editor for the user to refine and send back. Shown as a suggestion " +
        "the user can accept; it never overwrites their work silently.")]
    public string SeedDraft([Description("Draft prompt text (markdown).")] string text)
    {
        bridge.SeedDraft(text);
        return "Draft proposed in Prompty.";
    }
}

[McpServerPromptType]
public sealed class PromptyPrompts(PromptBridge bridge)
{
    // Claude Code exposes MCP prompts as slash commands: /mcp__prompty__send
    [McpServerPrompt(Name = "send"), Description("Insert the prompt you finished in Prompty as your message.")]
    public string Send() => bridge.TakeReadyPrompt();
}
