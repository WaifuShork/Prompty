using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Prompty.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl,
    string ExeUrl, long ExeSize, string? Sha256, string? Sha256Url);

/// <summary>
/// Self-update from GitHub Releases. Finds the latest release, downloads its Prompty.exe and checks the
/// SHA-256 against the published checksum, then swaps the file in place and restarts:
///   1. rename the running Prompty.exe → Prompty.exe.old (Windows allows renaming a running exe),
///   2. move the verified download to Prompty.exe,
///   3. start it with --after-update &lt;pid&gt;, then close this instance.
/// The new instance waits for the old one to exit (so the MCP port is free) and deletes the .old file.
/// If anything fails before the restart, the original exe is put back.
/// </summary>
public static class UpdateService
{
    public const string Repo = "WaifuShork/Prompty";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";
    const string ExeAssetName = "Prompty.exe";

    // Declared before Http: static initializers run in source order, and the client's User-Agent needs it.
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Prompty", CurrentVersionText));
        return c;
    }

    /// <summary>Path of the running exe.</summary>
    static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate Prompty.exe.");

    /// <summary>
    /// Updating only makes sense for the published single-file exe. A dev build (dotnet run / bin\Debug) is a
    /// Prompty.dll plus a launcher exe, and swapping the launcher would do nothing useful. (Assembly.Location
    /// can't tell them apart: the release bundle self-extracts, so its assemblies have real paths too.)
    /// </summary>
    public static bool CanSelfUpdate =>
        Environment.ProcessPath is { } exe
        && Path.GetFileName(exe).Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Prompty.dll"));

    static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var core = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core, out var v)) return false;
        version = Normalize(v);
        return true;
    }

    /// <summary>Latest published (non-draft, non-prerelease) release, or null if there is none or it isn't newer.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // PROMPTY_UPDATE_FEED points at a different "latest release" JSON (used to test updates end to end).
        var feed = Environment.GetEnvironmentVariable("PROMPTY_UPDATE_FEED") is { Length: > 0 } f
            ? f : $"https://api.github.com/repos/{Repo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, feed);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;

        var tag = json["tag_name"]?.GetValue<string>();
        if (!TryParseTag(tag, out var version) || version <= CurrentVersion) return null;

        var assets = json["assets"]?.AsArray() ?? [];
        JsonNode? Asset(string name) => assets.FirstOrDefault(a =>
            string.Equals(a?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
        var exe = Asset(ExeAssetName);
        if (exe == null) return null; // release without a Windows exe (shouldn't happen)

        // GitHub reports a sha256 digest per asset; the release also ships Prompty.exe.sha256.
        var digest = exe["digest"]?.GetValue<string>();
        var sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
        var shaUrl = Asset(ExeAssetName + ".sha256")?["browser_download_url"]?.GetValue<string>();

        return new UpdateInfo(version, tag!, json["body"]?.GetValue<string>() ?? "",
            json["html_url"]?.GetValue<string>() ?? ReleasesPage,
            exe["browser_download_url"]!.GetValue<string>(), exe["size"]?.GetValue<long>() ?? 0, sha, shaUrl);
    }

    /// <summary>Downloads and verifies the new exe next to the running one. Returns the staged file path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(ExePath)!;
        EnsureWritable(dir);

        var expected = update.Sha256;
        if (expected == null && update.Sha256Url != null)
            expected = (await Http.GetStringAsync(update.Sha256Url, ct)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidOperationException("This release has no published checksum, so the download can't be verified. Download it from the releases page instead.");

        // Staged in the same folder, so the final swap is a cheap rename on the same volume.
        var staged = Path.Combine(dir, $"Prompty-{update.Version}.download");
        try
        {
            using (var resp = await Http.GetAsync(update.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? update.ExeSize;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress.Report((double)done / total);
                }
            }

            string actual;
            await using (var fs = File.OpenRead(staged)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The download is corrupt or was tampered with (SHA-256 mismatch). Nothing was changed.");
            return staged;
        }
        catch
        {
            TryDelete(staged);
            throw;
        }
    }

    /// <summary>Swaps the staged exe in and starts it. The caller must then close this instance.</summary>
    public static void ApplyAndRestart(string staged)
    {
        var exe = ExePath;
        var old = exe + ".old";
        TryDelete(old);
        File.Move(exe, old);                 // a running exe can be renamed, not overwritten
        try
        {
            File.Move(staged, exe);
            Process.Start(new ProcessStartInfo(exe, $"--after-update {Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
        }
        catch
        {
            // Put the original back so the user isn't left without a working Prompty.
            if (File.Exists(exe)) TryDelete(exe);
            File.Move(old, exe);
            throw;
        }
    }

    /// <summary>
    /// Called at startup. After an update: waits for the previous instance to exit (it holds the MCP port)
    /// and removes the old exe. Also clears leftovers from interrupted updates.
    /// </summary>
    public static void OnStartup(string[] args)
    {
        var i = Array.IndexOf(args, "--after-update");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(20_000); }
            catch (ArgumentException) { } // already gone
            JustUpdated = true;
        }
        if (Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } dir)
        {
            for (var attempt = 0; attempt < 10 && File.Exists(exe + ".old"); attempt++)
            {
                if (TryDelete(exe + ".old")) break;
                Thread.Sleep(300);
            }
            foreach (var leftover in Directory.EnumerateFiles(dir, "Prompty-*.download")) TryDelete(leftover);
        }
    }

    /// <summary>True when this instance was started by an update.</summary>
    public static bool JustUpdated { get; private set; }

    static void EnsureWritable(string dir)
    {
        var probe = Path.Combine(dir, $".prompty-write-test-{Guid.NewGuid():N}");
        try { File.WriteAllText(probe, ""); File.Delete(probe); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"Prompty can't write to {dir}, so it can't update itself there. Move Prompty.exe to a folder you own " +
                $"(for example %LOCALAPPDATA%\\Programs\\Prompty) or download the update from the releases page.");
        }
    }

    static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
