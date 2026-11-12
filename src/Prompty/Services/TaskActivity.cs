using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Prompty.Services;

public enum ClaudeTaskKind { Shell, BackgroundShell, Monitor, Agent, Workflow }
public enum ClaudeTaskStatus { Running, Completed, Failed, Stopped }

/// <summary>A shell, monitor, agent or workflow Claude started, as of the last transcript read.</summary>
public sealed record ClaudeTaskView(
    string Id, ClaudeTaskKind Kind, string Title, string Command, ClaudeTaskStatus Status, int? ExitCode,
    DateTime StartedAt, DateTime? EndedAt, string Output, bool OutputTruncated, bool OutputIsLive, string? Summary);

/// <summary>
/// Tracks the processes Claude Code starts, derived from the session transcript plus the task output files
/// Claude Code writes while they run. What the transcript contains (confirmed against real sessions):
/// <list type="bullet">
/// <item>assistant <c>tool_use</c> Bash / PowerShell / Monitor / Agent / Workflow, with <c>input.command</c>,
///   <c>input.description</c>, <c>input.run_in_background</c>;</item>
/// <item>the matching <c>tool_result</c>: foreground shells carry <c>toolUseResult.{stdout, stderr, interrupted}</c>,
///   failures start with "Exit code N"; background shells carry <c>backgroundTaskId</c> and the text
///   "Output is being written to: …\tasks\&lt;id&gt;.output"; async agents carry <c>isAsync, agentId, outputFile</c>;</item>
/// <item><c>&lt;task-notification&gt;</c> blocks (queue-operation entries, queued_command attachments, user messages) with
///   task-id, tool-use-id, output-file, status and a summary like "… completed (exit code 0)";</item>
/// <item><c>TaskStop</c> tool calls whose successful result means that task was stopped.</item>
/// </list>
/// Foreground shells also stream into a tasks\&lt;id&gt;.output file while running (deleted when they finish), but
/// that id never appears in the transcript, so a running shell is matched to the first new output file after it started.
/// </summary>
public static partial class ClaudeSessions
{
    const int MaxOutputChars = 48_000;
    static readonly HashSet<string> TaskToolNames = ["Bash", "PowerShell", "Monitor", "Agent", "Task", "Workflow"];
    static readonly HashSet<string> StopToolNames = ["TaskStop", "KillShell", "KillBash"];

    sealed class TaskRecord
    {
        public required string ToolUseId;
        public ClaudeTaskKind Kind;
        public string Title = "";
        public string Command = "";
        public string? TaskId;
        public string? OutputFile;
        public ClaudeTaskStatus Status = ClaudeTaskStatus.Running;
        public int? ExitCode;
        public string FinalOutput = "";
        public string? Summary;
        public DateTime StartedAt;
        public DateTime? EndedAt;
        public bool Background;
    }

    sealed class TaskTracker
    {
        public readonly List<TaskRecord> Ordered = [];
        public readonly Dictionary<string, TaskRecord> ByToolUse = [];
        public readonly Dictionary<string, string> StopCalls = []; // TaskStop tool_use id → task id
        public TaskRecord? ByTaskId(string id) => Ordered.LastOrDefault(t => t.TaskId == id);
    }

    /// <summary>Cheap pre-filter: only lines that can mention a task are parsed a second time.</summary>
    static void ScanForTasks(string line, TranscriptState st)
    {
        var maybeUse = line.Contains("\"tool_use\"", StringComparison.Ordinal);
        var maybeResult = line.Contains("\"tool_result\"", StringComparison.Ordinal);
        var maybeNotice = line.Contains("<task-notification>", StringComparison.Ordinal);
        if (!maybeUse && !maybeResult && !maybeNotice) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is not ("assistant" or "user" or "queue-operation" or "attachment")) return; // skips api-request copies
            if (root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return;
            var at = root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt) ? dt.ToLocalTime() : DateTime.Now;
            var tracker = st.Tasks;

            if (type == "assistant" && maybeUse) ScanToolUses(root, at, tracker);
            if (type == "user" && maybeResult) ScanToolResults(root, at, tracker);
            if (maybeNotice)
            {
                string? text = type switch
                {
                    "queue-operation" => root.TryGetProperty("content", out var qc) && qc.ValueKind == JsonValueKind.String ? qc.GetString() : null,
                    "attachment" => root.TryGetProperty("attachment", out var a) && a.TryGetProperty("prompt", out var ap)
                                    && ap.ValueKind == JsonValueKind.String ? ap.GetString() : null,
                    "user" => root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var uc)
                              && uc.ValueKind == JsonValueKind.String ? uc.GetString() : null,
                    _ => null,
                };
                if (text != null) foreach (Match n in NotificationRegex().Matches(text)) ApplyNotification(n.Value, at, tracker);
            }
        }
        catch (JsonException) { }
    }

    static void ScanToolUses(JsonElement root, DateTime at, TaskTracker tracker)
    {
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return;
        foreach (var b in content.EnumerateArray())
        {
            if (!b.TryGetProperty("type", out var bt) || bt.GetString() != "tool_use") continue;
            var name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var id = b.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var input = b.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object ? inp : default;
            string? Str(string key) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v)
                                       && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            if (StopToolNames.Contains(name))
            {
                if ((Str("task_id") ?? Str("shell_id") ?? Str("bash_id")) is { } target) tracker.StopCalls[id] = target;
                continue;
            }
            if (!TaskToolNames.Contains(name) || id.Length == 0 || tracker.ByToolUse.ContainsKey(id)) continue;

            var background = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("run_in_background", out var rb)
                             && rb.ValueKind == JsonValueKind.True;
            var command = Str("command") ?? Str("prompt") ?? "";
            var kind = name switch
            {
                "Monitor" => ClaudeTaskKind.Monitor,
                "Agent" or "Task" => ClaudeTaskKind.Agent,
                "Workflow" => ClaudeTaskKind.Workflow,
                _ => background ? ClaudeTaskKind.BackgroundShell : ClaudeTaskKind.Shell,
            };
            var title = Str("description") is { Length: > 0 } d ? d
                : command.Split('\n')[0].Trim() is { Length: > 0 } first ? first
                : Str("name") ?? name;
            if (kind == ClaudeTaskKind.Agent && Str("subagent_type") is { Length: > 0 } sub) command = $"[{sub}] {command}";
            var rec = new TaskRecord
            {
                ToolUseId = id, Kind = kind, Title = Truncate(title, 160), Command = Truncate(command, 4000),
                StartedAt = at, Background = background || kind is ClaudeTaskKind.Monitor,
            };
            tracker.ByToolUse[id] = rec;
            tracker.Ordered.Add(rec);
        }
    }

    static void ScanToolResults(JsonElement root, DateTime at, TaskTracker tracker)
    {
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return;
        var results = content.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "tool_result").ToList();
        // toolUseResult describes the result only when the entry holds exactly one.
        JsonElement? meta = results.Count == 1 && root.TryGetProperty("toolUseResult", out var tur) ? tur : null;

        foreach (var block in results)
        {
            var id = block.TryGetProperty("tool_use_id", out var i) ? i.GetString() ?? "" : "";
            var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var text = ToolResultText(block);

            if (tracker.StopCalls.TryGetValue(id, out var stoppedId))
            {
                if (!isError && tracker.ByTaskId(stoppedId) is { } target && target.Status == ClaudeTaskStatus.Running)
                {
                    target.Status = ClaudeTaskStatus.Stopped;
                    target.EndedAt ??= at;
                }
                continue;
            }
            if (!tracker.ByToolUse.TryGetValue(id, out var rec)) continue;

            var m = meta is { ValueKind: JsonValueKind.Object } mm ? mm : default;
            string? MetaStr(string key) => m.ValueKind == JsonValueKind.Object && m.TryGetProperty(key, out var v)
                                           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var isAsync = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("isAsync", out var ia) && ia.ValueKind == JsonValueKind.True;
            // Background shells: backgroundTaskId. Monitors: taskId. Async agents: agentId (with isAsync).
            var taskId = MetaStr("backgroundTaskId") ?? MetaStr("taskId") ?? (isAsync ? MetaStr("agentId") : null)
                         ?? (BackgroundIdRegex().Match(text) is { Success: true } bm ? bm.Groups[1].Value : null);

            if (!isError && (taskId != null || isAsync))
            {
                // Still running in the background; a task-notification or TaskStop will finish it.
                rec.Background = true;
                rec.TaskId = taskId;
                rec.OutputFile = MetaStr("outputFile")
                                 ?? (OutputFileRegex().Match(text.Length > 600 ? text[..600] : text) is { Success: true } om ? om.Groups[1].Value.Trim() : null);
                if (rec.Kind == ClaudeTaskKind.Shell) rec.Kind = ClaudeTaskKind.BackgroundShell;
                continue;
            }

            // Finished in the foreground.
            var interrupted = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("interrupted", out var intr) && intr.ValueKind == JsonValueKind.True;
            var exit = ExitCodeRegex().Match(text);
            rec.ExitCode = exit.Success ? int.Parse(exit.Groups[1].Value)
                : !isError && rec.Kind is ClaudeTaskKind.Shell or ClaudeTaskKind.BackgroundShell ? 0 : null;
            rec.Status = interrupted || text.Contains("[Request interrupted", StringComparison.Ordinal) ? ClaudeTaskStatus.Stopped
                : isError ? ClaudeTaskStatus.Failed : ClaudeTaskStatus.Completed;
            var stdout = MetaStr("stdout");
            var stderr = MetaStr("stderr");
            rec.FinalOutput = TailText(stdout != null
                ? (stdout + (string.IsNullOrEmpty(stderr) ? "" : (stdout.Length > 0 ? "\n" : "") + stderr))
                : text, out _);
            rec.EndedAt = at;
        }
    }

    static void ApplyNotification(string block, DateTime at, TaskTracker tracker)
    {
        string? Tag(string name) => Regex.Match(block, $"<{name}>(.*?)</{name}>", RegexOptions.Singleline) is { Success: true } m
            ? m.Groups[1].Value.Trim() : null;
        var toolUseId = Tag("tool-use-id");
        var taskId = Tag("task-id");
        var rec = (toolUseId != null ? tracker.ByToolUse.GetValueOrDefault(toolUseId) : null)
                  ?? (taskId != null ? tracker.ByTaskId(taskId) : null);
        if (rec == null) return;

        rec.TaskId ??= taskId;
        rec.OutputFile ??= Tag("output-file");
        rec.Background = true;
        rec.Summary = Tag("summary") ?? rec.Summary;
        var exit = rec.Summary != null ? NotificationExitRegex().Match(rec.Summary) : Match.Empty;
        if (exit.Success) rec.ExitCode = int.Parse(exit.Groups[1].Value);
        rec.Status = (Tag("status") ?? "").ToLowerInvariant() switch
        {
            "completed" => rec.ExitCode is > 0 ? ClaudeTaskStatus.Failed : ClaudeTaskStatus.Completed,
            "failed" or "error" => ClaudeTaskStatus.Failed,
            "killed" or "stopped" or "cancelled" or "canceled" => ClaudeTaskStatus.Stopped,
            _ => rec.Status,
        };
        if (rec.Status != ClaudeTaskStatus.Running) rec.EndedAt ??= at;
    }

    /// <summary>
    /// The session's tasks, newest last, with their latest output. Reads only what was appended to the
    /// transcript since the last call, plus the tail of each running task's output file. Call off the UI thread.
    /// </summary>
    public static List<ClaudeTaskView> GetTasks(string path, string sessionId)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return [];
        ReadTranscript(path);
        TranscriptState st;
        lock (Transcripts) if (!Transcripts.TryGetValue(path, out st!)) return [];

        List<TaskRecord> records;
        lock (st) records = st.Tasks.Ordered.Select(r => new TaskRecord
        {
            ToolUseId = r.ToolUseId, Kind = r.Kind, Title = r.Title, Command = r.Command, TaskId = r.TaskId,
            OutputFile = r.OutputFile, Status = r.Status, ExitCode = r.ExitCode, FinalOutput = r.FinalOutput,
            Summary = r.Summary, StartedAt = r.StartedAt, EndedAt = r.EndedAt, Background = r.Background,
        }).ToList();

        var tasksDir = FindTasksDir(sessionId, records);
        var liveFiles = MatchForegroundOutputFiles(records, tasksDir);
        var views = new List<ClaudeTaskView>(records.Count);
        foreach (var r in records)
        {
            string output = r.FinalOutput;
            var truncated = false;
            var live = false;
            // Monitors don't report their output path; it's <tasks>\<taskId>.output like the others.
            var file = r.OutputFile ?? liveFiles.GetValueOrDefault(r.ToolUseId)
                       ?? (r.TaskId != null && tasksDir != null ? Path.Combine(tasksDir, r.TaskId + ".output") : null);
            if (file != null && (r.Status == ClaudeTaskStatus.Running || r.Background) && File.Exists(file))
            {
                var tail = r.Kind == ClaudeTaskKind.Agent ? AgentActivity(file, out truncated) : ReadTail(file, out truncated);
                if (tail.Length > 0 || r.Status == ClaudeTaskStatus.Running) { output = tail; live = r.Status == ClaudeTaskStatus.Running; }
            }
            if (output.Length == 0 && r.Summary != null) output = r.Summary;
            views.Add(new ClaudeTaskView(r.ToolUseId, r.Kind, r.Title, r.Command, r.Status, r.ExitCode,
                r.StartedAt, r.EndedAt, output, truncated || r.FinalOutput.StartsWith('…'), live, r.Summary));
        }
        return views;
    }

    static readonly Dictionary<string, string?> TasksDirs = [];

    /// <summary>%TEMP%\claude\&lt;hash&gt;\&lt;sessionId&gt;\tasks, from a known output file or by searching once.</summary>
    static string? FindTasksDir(string sessionId, List<TaskRecord> records)
    {
        if (records.FirstOrDefault(r => r.OutputFile != null)?.OutputFile is { } known && Path.GetDirectoryName(known) is { } d)
            return d;
        lock (TasksDirs)
        {
            if (TasksDirs.TryGetValue(sessionId, out var cached) && (cached == null || Directory.Exists(cached))) return cached;
            string? found = null;
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "claude");
                if (Directory.Exists(root))
                    found = Directory.EnumerateDirectories(root)
                        .Select(dir => Path.Combine(dir, sessionId, "tasks"))
                        .FirstOrDefault(Directory.Exists);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            TasksDirs[sessionId] = found;
            return found;
        }
    }

    /// <summary>
    /// Running foreground shells don't record their output file's id, so pair them, oldest first, with the
    /// unclaimed shell output files (b… ids) created after they started.
    /// </summary>
    static Dictionary<string, string> MatchForegroundOutputFiles(List<TaskRecord> records, string? tasksDir)
    {
        var map = new Dictionary<string, string>();
        var pending = records.Where(r => r.Status == ClaudeTaskStatus.Running && !r.Background
                                         && r.Kind == ClaudeTaskKind.Shell).OrderBy(r => r.StartedAt).ToList();
        if (pending.Count == 0 || tasksDir == null || !Directory.Exists(tasksDir)) return map;
        var claimed = records.Select(r => r.TaskId).Where(id => id != null).ToHashSet();
        List<FileInfo> files;
        try
        {
            var since = pending[0].StartedAt.AddSeconds(-3).ToUniversalTime();
            files = new DirectoryInfo(tasksDir).EnumerateFiles("b*.output")
                .Where(f => f.CreationTimeUtc >= since && !claimed.Contains(Path.GetFileNameWithoutExtension(f.Name)))
                .OrderBy(f => f.CreationTimeUtc).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return map; }

        var fi = 0;
        foreach (var r in pending)
        {
            while (fi < files.Count && files[fi].CreationTime < r.StartedAt.AddSeconds(-3)) fi++;
            if (fi >= files.Count) break;
            map[r.ToolUseId] = files[fi++].FullName;
        }
        return map;
    }

    /// <summary>The last ~48K characters of a growing text file (shared read, never blocks the writer).</summary>
    static string ReadTail(string path, out bool truncated)
    {
        truncated = false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var max = MaxOutputChars * 2L; // bytes; UTF-8 output is mostly ASCII
            if (fs.Length > max) { fs.Seek(-max, SeekOrigin.End); truncated = true; }
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd().Replace("\r\n", "\n");
            if (truncated && text.IndexOf('\n') is >= 0 and var nl) text = text[(nl + 1)..]; // drop the partial first line
            text = TailText(text, out var cut);
            truncated |= cut;
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>A sub-agent's output file is its own transcript; summarize its latest messages and tool calls.</summary>
    static string AgentActivity(string path, out bool truncated)
    {
        var raw = ReadTail(path, out truncated);
        var lines = new List<string>();
        foreach (var json in raw.Split('\n'))
        {
            if (json.Length == 0 || json[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "assistant") continue;
                if (!root.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in c.EnumerateArray())
                {
                    var bt = b.TryGetProperty("type", out var x) ? x.GetString() : null;
                    if (bt == "text" && b.TryGetProperty("text", out var tx) && tx.GetString() is { Length: > 0 } s)
                        lines.Add(Truncate(s.Trim().Replace('\n', ' '), 300));
                    else if (bt == "tool_use")
                        lines.Add($"● {(b.TryGetProperty("name", out var n) ? n.GetString() : "tool")}({ToolArg(b.TryGetProperty("input", out var inp) ? inp : default, null)})");
                }
            }
            catch (JsonException) { }
        }
        if (lines.Count > 60) { lines = lines[^60..]; truncated = true; }
        return string.Join("\n", lines);
    }

    static string TailText(string text, out bool truncated)
    {
        truncated = text.Length > MaxOutputChars;
        return truncated ? "…\n" + text[^MaxOutputChars..] : text;
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"<task-notification>.*?</task-notification>", RegexOptions.Singleline)]
    private static partial Regex NotificationRegex();
    // Anchored: a foreground command's own output may contain this sentence (it did, in testing).
    [GeneratedRegex(@"\A\s*Command running in background with ID:\s*([A-Za-z0-9_-]+)")]
    private static partial Regex BackgroundIdRegex();
    [GeneratedRegex(@"Output is being written to:\s*(.+?\.output)")]
    private static partial Regex OutputFileRegex();
    [GeneratedRegex(@"\A\s*(?:Error:\s*)?Exit code (-?\d+)")]
    private static partial Regex ExitCodeRegex();
    [GeneratedRegex(@"exit code (-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NotificationExitRegex();
}
