using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Prompty.Services;

/// <summary>
/// API keys per provider, encrypted with Windows DPAPI for the current user
/// (%APPDATA%\Prompty\credentials.dat; unreadable by other accounts or other machines).
/// Falls back to the provider's usual environment variable, e.g. OPENAI_API_KEY.
/// </summary>
public static class CredentialStore
{
    static string FilePath => Path.Combine(AppSettings.Dir, "credentials.dat");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Prompty.credentials.v1");
    static readonly object Gate = new();

    public enum KeySource { None, Saved, Environment }

    public static (string? Key, KeySource Source, string? EnvVar) Resolve(ProviderInfo provider)
    {
        if (Load().TryGetValue(provider.Id, out var saved) && saved.Length > 0) return (saved, KeySource.Saved, null);
        foreach (var name in provider.EnvVars ?? [])
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } env) return (env, KeySource.Environment, name);
        return (null, KeySource.None, null);
    }

    public static string? Get(ProviderInfo provider) => Resolve(provider).Key;

    /// <summary>Any stored secret by its own key (e.g. the ChatGPT sign-in record).</summary>
    public static string? GetRaw(string key) => Load().TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public static void Set(string providerId, string? key)
    {
        lock (Gate)
        {
            var all = Load();
            if (string.IsNullOrWhiteSpace(key)) all.Remove(providerId);
            else all[providerId] = key.Trim();
            Directory.CreateDirectory(AppSettings.Dir);
            var plain = JsonSerializer.SerializeToUtf8Bytes(all);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
    }

    static Dictionary<string, string> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return [];
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or JsonException) { return []; }
        }
    }

    /// <summary>"sk-or-…a1b2" style hint for showing a saved key without revealing it.</summary>
    public static string Mask(string key) => key.Length <= 10 ? new string('•', key.Length) : $"{key[..6]}…{key[^4..]}";
}
