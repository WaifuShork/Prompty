using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Prompty.Services;

/// <summary>A signed-in ChatGPT account (stored encrypted via <see cref="CredentialStore"/>).</summary>
public sealed record ChatGptAccount(
    string Email, string Subject, string ClientId, string HostId, string IdToken,
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string Scopes, DateTimeOffset SavedAt)
{
    /// <summary>The grant must include this scope before the user's plan may pay for inference.</summary>
    public bool HasPlanUsage => Scopes.Split(' ').Contains("chatgpt.tokens.use.direct");
}

public sealed class ChatGptAuthException(string message, bool signInAgain = false) : Exception(message)
{
    public bool SignInAgain { get; } = signInAgain;
}

/// <summary>
/// OpenAI's official "Sign in with ChatGPT" for open-source/local apps (token sharing with dynamic client
/// registration), so the user's ChatGPT plan pays for Prompty's model calls instead of API billing:
/// <list type="bullet">
/// <item>OAuth 2 authorization code + PKCE (S256) in the system browser, loopback redirect on 127.0.0.1.</item>
/// <item>First sign-in registers with client_id=dynamic_agent_client; the issued client_id is kept and reused.</item>
/// <item>A stable, opaque per-install host id (urn:uuid) is sent as ext_agent_host_id.</item>
/// <item>The ID token is verified (RS256 signature against OpenAI's JWKS, issuer, audience, expiry, nonce).</item>
/// <item>Access tokens last an hour and are refreshed on demand; each refresh rotates the refresh token.</item>
/// </list>
/// Docs: https://developers.openai.com/siwc/token-sharing-open-source/sign-in
/// </summary>
public static class ChatGptAuth
{
    const string Issuer = "https://auth.openai.com";
    const string AuthorizeUrl = Issuer + "/api/accounts/authorize";
    const string TokenUrl = Issuer + "/api/accounts/oauth/token";
    const string RevokeUrl = Issuer + "/api/accounts/oauth/revoke";
    const string JwksUrl = Issuer + "/.well-known/jwks.json";
    public const string Resource = "https://api.openai.com/v1";
    const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    const string CallbackPath = "/auth/callback";
    const int PreferredPort = 1455;
    public const string ManageUsageUrl = "https://chatgpt.com/settings/usage";

    // CredentialStore keys: the account, plus registration state that survives sign-out.
    const string AccountKey = "chatgpt.account"; // distinct from provider-id API key slots
    const string ClientKey = "chatgpt.client_id";
    const string HostKey = "chatgpt.host_id";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly SemaphoreSlim RefreshGate = new(1, 1);

    public static ChatGptAccount? Account
    {
        get
        {
            var json = CredentialStore.GetRaw(AccountKey);
            if (json == null) return null;
            try { return JsonSerializer.Deserialize<ChatGptAccount>(json); }
            catch (JsonException) { return null; }
        }
    }

    public static bool IsSignedIn => Account != null;

    // ───────────────────────────── sign-in ─────────────────────────────

    public static async Task<ChatGptAccount> SignInAsync(CancellationToken ct)
    {
        var hostId = CredentialStore.GetRaw(HostKey);
        if (hostId == null)
        {
            hostId = $"urn:uuid:{Guid.NewGuid()}"; // chosen once per install, before the first sign-in
            CredentialStore.Set(HostKey, hostId);
        }
        var savedClientId = CredentialStore.GetRaw(ClientKey);
        var previous = Account;

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));

        // Start listening before opening the browser. 1455 is the documented example; any free port works.
        var listener = StartListener(out var port);
        try
        {
            var redirectUri = $"http://127.0.0.1:{port}{CallbackPath}";
            var query = new List<(string, string)>
            {
                ("client_id", savedClientId ?? "dynamic_agent_client"),
                ("ext_agent_host_id", hostId),
                ("response_type", "code"),
                ("redirect_uri", redirectUri),
                ("scope", Scope),
                ("resource", Resource),
                ("state", state),
                ("nonce", nonce),
                ("code_challenge_method", "S256"),
                ("code_challenge", challenge),
            };
            if (savedClientId == null) query.Add(("agent_name_hint", "Prompty"));
            else
            {
                if (previous?.IdToken is { Length: > 0 } hint) query.Add(("id_token_hint", hint));
                if (previous?.Email is { Length: > 0 } email) query.Add(("login_hint", email));
            }
            var url = AuthorizeUrl + "?" + string.Join("&", query.Select(q => $"{q.Item1}={Uri.EscapeDataString(q.Item2)}"));
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var callback = await WaitForCallbackAsync(listener, timeout.Token);

            if (callback.GetValueOrDefault("state") != state)
                throw new ChatGptAuthException("Sign-in failed: the response didn't match this sign-in attempt. Try again.");
            if (callback.GetValueOrDefault("error") is { } error)
                throw error == "access_denied"
                    ? new OperationCanceledException("You cancelled the ChatGPT sign-in.")
                    : new ChatGptAuthException($"ChatGPT sign-in failed: {callback.GetValueOrDefault("error_description") ?? error}");
            var code = callback.GetValueOrDefault("code") ?? throw new ChatGptAuthException("ChatGPT sign-in returned no authorization code.");

            var issued = callback.GetValueOrDefault("client_id");
            string clientId;
            if (savedClientId == null)
                clientId = issued ?? throw new ChatGptAuthException("ChatGPT didn't finish registering Prompty (no client_id returned). Try again.");
            else if (issued != null && issued != savedClientId)
                throw new ChatGptAuthException("ChatGPT returned a different app registration than expected; sign-in was stopped.");
            else clientId = savedClientId;
            CredentialStore.Set(ClientKey, clientId);

            JsonObject tokens;
            try
            {
                tokens = await PostTokenAsync(new()
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = clientId,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                    ["redirect_uri"] = redirectUri,
                    ["resource"] = Resource,
                }, ct);
            }
            catch (ChatGptAuthException ex) when (ex.SignInAgain)
            {
                // OpenAI answers a rejected code with a bare invalid_grant and no reason. The docs say to discard
                // the code and start over; plan usage also requires a ChatGPT Plus or Pro subscription.
                throw new ChatGptAuthException(
                    "OpenAI rejected this sign-in (invalid_grant) without saying why. Using your ChatGPT plan in other apps " +
                    "requires ChatGPT Plus or Pro; Free accounts can't use it. If you have Plus or Pro, click Continue with ChatGPT " +
                    "to try again. Otherwise pick another provider: Ollama (free, local), OpenRouter, or an API key.");
            }

            var idToken = tokens["id_token"]?.GetValue<string>() ?? throw new ChatGptAuthException("ChatGPT returned no ID token.");
            var claims = await ValidateIdTokenAsync(idToken, clientId, nonce, ct);
            var account = new ChatGptAccount(
                Email: claims["email"]?.GetValue<string>() ?? "",
                Subject: claims["sub"]?.GetValue<string>() ?? throw new ChatGptAuthException("ID token has no subject."),
                ClientId: clientId, HostId: hostId, IdToken: idToken,
                AccessToken: tokens["access_token"]!.GetValue<string>(),
                RefreshToken: tokens["refresh_token"]?.GetValue<string>() ?? "",
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(tokens["expires_in"]?.GetValue<int>() ?? 3600),
                Scopes: tokens["scope"]?.GetValue<string>() ?? callback.GetValueOrDefault("scope") ?? "",
                SavedAt: DateTimeOffset.UtcNow);
            Save(account);
            return account;
        }
        finally { listener.Stop(); }
    }

    static TcpListener StartListener(out int port)
    {
        foreach (var candidate in new[] { PreferredPort, 0 })
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, candidate);
                l.Start();
                port = ((IPEndPoint)l.LocalEndpoint).Port;
                return l;
            }
            catch (SocketException) when (candidate != 0) { } // 1455 busy: take any free port
        }
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Minimal HTTP: accept connections until one hits the callback path, answer it, return its query.</summary>
    static async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { } // skip headers

            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "";
            var path = target.Split('?')[0];
            if (parts[0] != "GET" || path != CallbackPath)
            {
                await RespondAsync(stream, 404, "Not found");
                continue;
            }
            var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
            var ok = query.ContainsKey("code") && !query.ContainsKey("error");
            await RespondAsync(stream, 200, ok
                ? "Signed in with ChatGPT. You can close this tab and return to Prompty."
                : "ChatGPT sign-in didn't complete. You can close this tab and try again from Prompty.");
            return query;
        }
    }

    static Dictionary<string, string> ParseQuery(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(kv => Uri.UnescapeDataString(kv[0].Replace('+', ' ')))
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString((g.First().ElementAtOrDefault(1) ?? "").Replace('+', ' ')));

    static async Task RespondAsync(NetworkStream stream, int status, string message)
    {
        var html = "<!doctype html><meta charset=utf-8><title>Prompty</title>" +
                   "<body style=\"font-family:Segoe UI,sans-serif;background:#16171b;color:#e4e4e7;display:grid;place-items:center;height:90vh\">" +
                   $"<p style=\"font-size:18px\">{WebUtility.HtmlEncode(message)}</p>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    // ───────────────────────────── tokens ─────────────────────────────

    static async Task<JsonObject> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonObject? json = null;
        try { json = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        if (resp.IsSuccessStatusCode && json != null) return json;

        var error = json?["error"]?.ToString();
        var detail = json?["error_description"]?.ToString() ?? error ?? $"HTTP {(int)resp.StatusCode}";
        // invalid_grant: the code or refresh token is no longer usable; only a fresh sign-in helps.
        throw new ChatGptAuthException($"ChatGPT sign-in token request failed: {detail}", signInAgain: error == "invalid_grant");
    }

    /// <summary>A valid access token, refreshed first if it expires within two minutes.</summary>
    public static async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var account = Account ?? throw new ChatGptAuthException("Not signed in with ChatGPT. Open ⚙ Settings to sign in.", signInAgain: true);
        if (account.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2)) return account.AccessToken;

        await RefreshGate.WaitAsync(ct); // refreshes rotate the refresh token, so never run two at once
        try
        {
            account = Account ?? throw new ChatGptAuthException("Not signed in with ChatGPT.", signInAgain: true);
            if (account.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2)) return account.AccessToken;
            if (account.RefreshToken.Length == 0)
                throw new ChatGptAuthException("Your ChatGPT session expired. Sign in again in ⚙ Settings.", signInAgain: true);

            JsonObject tokens;
            try
            {
                tokens = await PostTokenAsync(new()
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = account.ClientId,
                    ["refresh_token"] = account.RefreshToken,
                    ["resource"] = Resource,
                }, ct);
            }
            catch (ChatGptAuthException ex) when (ex.SignInAgain)
            {
                ClearAccount(); // terminal: revoked, expired after 30 days, or disconnected in ChatGPT settings
                throw new ChatGptAuthException("Your ChatGPT sign-in is no longer valid (it may have been disconnected in ChatGPT settings). Sign in again in ⚙ Settings.", signInAgain: true);
            }
            // Network errors and 5xx propagate as-is: keep the credentials and try again later.

            account = account with
            {
                AccessToken = tokens["access_token"]!.GetValue<string>(),
                RefreshToken = tokens["refresh_token"]?.GetValue<string>() ?? account.RefreshToken,
                IdToken = tokens["id_token"]?.GetValue<string>() ?? account.IdToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens["expires_in"]?.GetValue<int>() ?? 3600),
                Scopes = tokens["scope"]?.GetValue<string>() ?? account.Scopes,
                SavedAt = DateTimeOffset.UtcNow,
            };
            Save(account);
            return account.AccessToken;
        }
        finally { RefreshGate.Release(); }
    }

    /// <summary>Revokes the refresh token with OpenAI, then forgets the account. The app registration is kept.</summary>
    public static async Task<bool> SignOutAsync(CancellationToken ct = default)
    {
        var account = Account;
        if (account == null) return true;
        var revoked = false;
        if (account.RefreshToken.Length > 0)
        {
            try
            {
                using var resp = await Http.PostAsync(RevokeUrl, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = account.RefreshToken,
                    ["token_type_hint"] = "refresh_token",
                    ["client_id"] = account.ClientId,
                }), ct);
                revoked = resp.IsSuccessStatusCode; // 200 also for an already-invalid token
            }
            catch (HttpRequestException) { }
        }
        ClearAccount();
        return revoked;
    }

    static void Save(ChatGptAccount account) => CredentialStore.Set(AccountKey, JsonSerializer.Serialize(account));
    static void ClearAccount() => CredentialStore.Set(AccountKey, null);

    // ───────────────────────────── ID token ─────────────────────────────

    static async Task<JsonObject> ValidateIdTokenAsync(string jwt, string clientId, string nonce, CancellationToken ct)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new ChatGptAuthException("ChatGPT returned a malformed ID token.");
        var header = JsonNode.Parse(FromBase64Url(parts[0]))!.AsObject();
        var claims = JsonNode.Parse(FromBase64Url(parts[1]))!.AsObject();

        if (header["alg"]?.GetValue<string>() != "RS256") throw new ChatGptAuthException("Unexpected ID token algorithm.");
        var kid = header["kid"]?.GetValue<string>();
        var jwks = JsonNode.Parse(await Http.GetStringAsync(JwksUrl, ct))!["keys"]!.AsArray();
        var key = jwks.FirstOrDefault(k => k?["kid"]?.GetValue<string>() == kid && k?["kty"]?.GetValue<string>() == "RSA")
                  ?? throw new ChatGptAuthException("ChatGPT's signing key for this ID token wasn't found.");
        using var rsa = RSA.Create(new RSAParameters
        {
            Modulus = FromBase64Url(key["n"]!.GetValue<string>()),
            Exponent = FromBase64Url(key["e"]!.GetValue<string>()),
        });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new ChatGptAuthException("The ChatGPT ID token signature is invalid; sign-in was stopped.");

        if (claims["iss"]?.GetValue<string>() != Issuer) throw new ChatGptAuthException("ID token issuer mismatch.");
        var aud = claims["aud"];
        var audOk = aud is JsonArray arr ? arr.Any(a => a?.GetValue<string>() == clientId) : aud?.GetValue<string>() == clientId;
        if (!audOk) throw new ChatGptAuthException("ID token audience mismatch.");
        var exp = claims["exp"]?.GetValue<long>() ?? 0;
        if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow.AddMinutes(-2)) throw new ChatGptAuthException("ID token expired.");
        if (claims["nonce"]?.GetValue<string>() != nonce) throw new ChatGptAuthException("ID token nonce mismatch; sign-in was stopped.");
        return claims;
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
