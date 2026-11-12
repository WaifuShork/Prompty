using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Prompty.Services;

/// <summary>
/// "Sign in with OpenRouter": OAuth PKCE (S256) with a loopback callback. The browser opens OpenRouter,
/// the user approves, OpenRouter redirects to http://localhost:&lt;port&gt;/callback with a code, and the
/// code is exchanged for a user-controlled API key. No client secret is involved.
/// </summary>
public static class OpenRouterSignIn
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<string> SignInAsync(CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var port = FreePort();
        var callback = $"http://localhost:{port}/callback/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(callback);
        listener.Start();

        var url = "https://openrouter.ai/auth" +
                  $"?callback_url={Uri.EscapeDataString(callback)}" +
                  $"&code_challenge={challenge}&code_challenge_method=S256" +
                  $"&state={state}&key_label={Uri.EscapeDataString("Prompty")}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        string? code;
        while (true)
        {
            var ctxTask = listener.GetContextAsync();
            var done = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
            if (done != ctxTask) throw new OperationCanceledException("Sign-in timed out or was cancelled.");
            var ctx = await ctxTask;
            var q = ctx.Request.QueryString;
            if (q["code"] is null) { Respond(ctx, 404, "Not found"); continue; } // e.g. favicon requests
            if (q["state"] != state) { Respond(ctx, 400, "Sign-in failed: state mismatch. Try again from Prompty."); throw new InvalidOperationException("OpenRouter sign-in: state mismatch."); }
            code = q["code"];
            Respond(ctx, 200, "Signed in to OpenRouter. You can close this tab and return to Prompty.");
            break;
        }

        var resp = await Http.PostAsJsonAsync("https://openrouter.ai/api/v1/auth/keys",
            new { code, code_verifier = verifier, code_challenge_method = "S256" }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"OpenRouter key exchange failed ({(int)resp.StatusCode}): {body}");
        return JsonNode.Parse(body)?["key"]?.GetValue<string>()
               ?? throw new InvalidOperationException("OpenRouter returned no key.");
    }

    static void Respond(HttpListenerContext ctx, int status, string message)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>Prompty</title>" +
                   $"<body style=\"font-family:Segoe UI,sans-serif;background:#16171b;color:#e4e4e7;display:grid;place-items:center;height:90vh\">" +
                   $"<p style=\"font-size:18px\">{WebUtility.HtmlEncode(message)}</p>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
