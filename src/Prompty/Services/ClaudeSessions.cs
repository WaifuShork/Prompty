using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Prompty.Services;

/// <param name="Pid">Set when the session is a running interactive Claude Code process we can type into.</param>
public sealed record SessionInfo(
    string FilePath, string SessionId, string Project, string Title, DateTime LastWrite,
    int? Pid = null, string? ProcStart = null, string? Status = null)
{
    public bool IsLive => Pid != null;
    public bool IsBusy => Status == "busy";
    public string Detail => IsLive
        ? $"{Project}  ·  ● {(IsBusy ? "working" : "idle")}  ·  pid {Pid}"
        : $"{Project}  ·  {Ago(LastWrite)}  ·  ended";

    public override string ToString() => $"{Title}  ·  {Detail}";

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}

public sealed record ConversationTurn(string Role, string Text);

public sealed class ConversationSnapshot
{
    public required SessionInfo Session { get; init; }
    public string? Cwd { get; init; }
    public List<ConversationTurn> Turns { get; init; } = [];

    /// <summary>
    /// Most recent turns, newest kept first when trimming, rendered oldest-to-newest so a
    /// small model reads it as a normal transcript.
    /// </summary>
    public string ToDigest(int maxChars)
    {
        var header = $"Session: {Session.Title}\nProject: {Cwd ?? Session.Project}\n\n";
        var parts = new List<string>();
        var used = header.Length;
        for (var i = Turns.Count - 1; i >= 0; i--)
        {
            var t = Turns[i];
            // Keep the head (intent) and the tail (latest state) of long turns.
            var cap = t.Role == "user" ? 2500 : 1500;
            if (i >= Turns.Count - 2) cap *= 2; // the latest exchange matters most
            var body = t.Text.Length > cap
                ? t.Text[..(cap / 3)] + "\n…[truncated]…\n" + t.Text[^(cap * 2 / 3)..]
                : t.Text;
            var block = $"### {(t.Role == "user" ? "User" : "Claude")}\n{body}\n\n";
            if (used + block.Length > maxChars && parts.Count > 0) break;
            parts.Add(block);
            used += block.Length;
        }
        parts.Reverse();
        return header + string.Concat(parts);
    }
}

/// <summary>Reads Claude Code session transcripts from ~/.claude/projects/*/*.jsonl.</summary>
public static partial class ClaudeSessions
{
    public static string ProjectsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>Claude Code's registry of running sessions: ~/.claude/sessions/&lt;pid&gt;.json.</summary>
    public static string RegistryDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    sealed record LiveEntry(int Pid, string SessionId, string Cwd, string? Name, string? Status, string? ProcStart, long UpdatedAt);

    /// <summary>Running sessions first (most recently active first), then recent ended transcripts.</summary>
    public static List<SessionInfo> ListRecent(int max = 30)
    {
        var files = new List<FileInfo>();
        if (Directory.Exists(ProjectsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
            {
                try { files.AddRange(new DirectoryInfo(dir).EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        var byId = files.ToDictionary(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase);

        var result = new List<SessionInfo>();
        foreach (var live in ListLive().OrderByDescending(l => l.UpdatedAt))
        {
            byId.TryGetValue(live.SessionId, out var f);
            result.Add(new SessionInfo(
                f?.FullName ?? "",
                live.SessionId,
                Path.GetFileName(live.Cwd.TrimEnd('\\', '/')),
                (f != null ? CachedTitle(f) : null) ?? live.Name ?? "(new session)",
                f?.LastWriteTime ?? DateTime.Now,
                live.Pid, live.ProcStart, live.Status));
        }

        var liveIds = result.Select(r => r.SessionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.AddRange(files
            .Where(f => !liveIds.Contains(Path.GetFileNameWithoutExtension(f.Name)))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(Math.Max(0, max - result.Count))
            .Select(f => new SessionInfo(
                f.FullName,
                Path.GetFileNameWithoutExtension(f.Name),
                PrettyProject(f.Directory!.Name),
                CachedTitle(f) ?? "(untitled session)",
                f.LastWriteTime)));
        return result;
    }

    static List<LiveEntry> ListLive()
    {
        var list = new List<LiveEntry>();
        if (!Directory.Exists(RegistryDir)) return list;
        foreach (var file in Directory.EnumerateFiles(RegistryDir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (S("kind") is { } kind && kind != "interactive") continue;
                if (!r.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid)) continue;
                var sessionId = S("sessionId");
                if (sessionId == null) continue;
                var procStart = S("procStart");
                if (!IsSameProcess(pid, procStart)) continue; // stale file or PID reused
                var updated = r.TryGetProperty("updatedAt", out var u) && u.TryGetInt64(out var ms) ? ms : 0;
                list.Add(new LiveEntry(pid, sessionId, S("cwd") ?? "", S("name"), S("status"), procStart, updated));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>
    /// True when <paramref name="pid"/> is still the Claude Code process the registry describes.
    /// procStart is the process creation time as a FILETIME, which guards against PID reuse.
    /// </summary>
    public static bool IsSameProcess(int pid, string? procStart)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (!long.TryParse(procStart, out var ft)) return p.ProcessName.Contains("claude", StringComparison.OrdinalIgnoreCase);
            return Math.Abs(p.StartTime.ToFileTimeUtc() - ft) < 20_000_000; // within 2s
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Titles come from scanning a transcript tail; don't redo that on every file-change event.
    static readonly Dictionary<string, (DateTime Read, string? Title)> TitleCache = new();

    static string? CachedTitle(FileInfo f)
    {
        lock (TitleCache)
        {
            if (TitleCache.TryGetValue(f.FullName, out var c) && (DateTime.Now - c.Read).TotalSeconds < 30) return c.Title;
        }
        var title = ReadTitle(f.FullName);
        lock (TitleCache) TitleCache[f.FullName] = (DateTime.Now, title);
        return title;
    }

    /// <summary>Directory names encode the cwd, e.g. "E--dev-workspace-foo" → "foo".</summary>
    static string PrettyProject(string encoded)
    {
        var parts = encoded.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? encoded : parts[^1];
    }

    static string? ReadTitle(string path)
    {
        string? title = null, lastPrompt = null;
        foreach (var line in ReadTailLines(path, 512 * 1024))
        {
            if (!line.Contains("\"ai-title\"") && !line.Contains("\"last-prompt\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "ai-title" && root.TryGetProperty("aiTitle", out var t)) title = t.GetString();
                if (type == "last-prompt" && root.TryGetProperty("lastPrompt", out var p)) lastPrompt = p.GetString();
            }
            catch (JsonException) { }
        }
        var result = title ?? lastPrompt;
        return result is { Length: > 70 } ? result[..70] + "…" : result;
    }

    public static ConversationSnapshot Load(SessionInfo session)
    {
        var turns = new List<ConversationTurn>();
        string? cwd = null;

        foreach (var line in ReadTailLines(session.FilePath, 6 * 1024 * 1024))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();
                if (type is not ("user" or "assistant")) continue;
                if (root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) continue;
                if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;
                if (root.TryGetProperty("cwd", out var cwdEl)) cwd = cwdEl.GetString();
                if (!root.TryGetProperty("message", out var msg)) continue;

                if (type == "user")
                {
                    var text = ExtractUserText(msg);
                    if (!string.IsNullOrWhiteSpace(text))
                        turns.Add(new("user", text));
                }
                else
                {
                    var text = ExtractAssistantText(msg);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    // One API message is streamed as several transcript lines; merge them.
                    if (turns.Count > 0 && turns[^1].Role == "assistant")
                        turns[^1] = turns[^1] with { Text = turns[^1].Text + "\n" + text };
                    else
                        turns.Add(new("assistant", text));
                }
            }
        }

        return new ConversationSnapshot { Session = session, Cwd = cwd, Turns = turns };
    }

    static bool IsTranscriptEntry(JsonElement root, out string? type, out JsonElement content)
    {
        content = default;
        type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is not ("user" or "assistant")) return false;
        if (root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return false;
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return false;
        return root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out content);
    }

    static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    static string ToolArg(JsonElement input, string? cwd)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in new[] { "file_path", "notebook_path", "path", "command", "pattern", "url", "query", "description", "skill", "prompt" })
        {
            if (!input.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) continue;
            var s = v.GetString()!.ReplaceLineEndings(" ");
            if (key.EndsWith("path") && cwd != null && s.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
                s = s[cwd.Length..].TrimStart('\\', '/');
            return s.Length > 100 ? s[..100] + "…" : s;
        }
        return "";
    }

    static string ToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
            if (part.TryGetProperty("text", out var t)) sb.AppendLine(t.GetString());
            else if (part.TryGetProperty("type", out var pt)) sb.AppendLine($"[{pt.GetString()}]");
        return sb.ToString();
    }

    static string ExtractUserText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content)) return "";
        var sb = new StringBuilder();
        if (content.ValueKind == JsonValueKind.String)
        {
            sb.Append(content.GetString());
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var bt = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                // tool_result blocks are tool output, not the user's words; skip them.
                if (bt == "text" && block.TryGetProperty("text", out var tx)) sb.AppendLine(tx.GetString());
            }
        }
        return CleanUserText(sb.ToString());
    }

    static string CleanUserText(string raw)
    {
        var text = SystemReminderRegex().Replace(raw, "").Trim();
        if (text.StartsWith("<local-command-caveat>") || text.StartsWith("<local-command-stdout>")
            || text.StartsWith("<local-command-stderr>"))
            return "";

        var cmd = CommandNameRegex().Match(text);
        if (cmd.Success)
        {
            var args = CommandArgsRegex().Match(text);
            return $"(ran command {cmd.Groups[1].Value} {(args.Success ? args.Groups[1].Value : "")})".Trim();
        }
        return text;
    }

    static string ExtractAssistantText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            var bt = block.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (bt == "text" && block.TryGetProperty("text", out var tx))
            {
                sb.AppendLine(tx.GetString());
            }
            else if (bt == "tool_use")
            {
                var name = block.TryGetProperty("name", out var n) ? n.GetString() : "tool";
                sb.AppendLine($"[used {name}{DescribeToolInput(block)}]");
            }
        }
        return sb.ToString().Trim();
    }

    static string DescribeToolInput(JsonElement toolUse)
    {
        if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in new[] { "file_path", "path", "command", "pattern", "description", "url", "query" })
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()!.ReplaceLineEndings(" ");
                return ": " + (s.Length > 120 ? s[..120] + "…" : s);
            }
        }
        return "";
    }

    /// <summary>Reads complete lines from the last <paramref name="maxBytes"/> of a file that may be open for writing.</summary>
    static IEnumerable<string> ReadTailLines(string path, long maxBytes)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) yield break; // live session with no transcript yet
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        using (fs)
        {
            var skipFirst = false;
            if (fs.Length > maxBytes)
            {
                fs.Seek(-maxBytes, SeekOrigin.End);
                skipFirst = true; // probably landed mid-line
            }
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (skipFirst) { skipFirst = false; continue; }
                if (line.Length > 0) yield return line;
            }
        }
    }

    [GeneratedRegex(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex SystemReminderRegex();

    [GeneratedRegex(@"<command-name>(.*?)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandNameRegex();

    [GeneratedRegex(@"<command-args>(.*?)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgsRegex();
}
