using System.Text;
using System.Text.Json;

namespace Prompty.Services;

// Lightweight transcript model: only what the output view renders. Large payloads (images,
// originalFile, full Read contents) are dropped at parse time.
abstract record TItem;
sealed record TText(string Text) : TItem;
sealed record TToolUse(string Id, string Name, string Arg) : TItem;
sealed record THunk(int OldStart, int NewStart, string[] Lines);
sealed record TToolResult(string Id, string Text, int TotalLines, bool IsError,
    List<THunk>? Patch, string[]? Written, int WrittenTotal, int? ReadLines) : TItem;
sealed record TEntry(string Type, string Time, string UserText, List<TItem> Items,
    DateTime? At = null, string? MessageId = null, int OutputTokens = 0);

/// <summary>The latest turn: when the prompt went in, when Claude last wrote, and output tokens so far.</summary>
public sealed record TurnActivity(DateTime Start, DateTime LastActivity, int OutputTokens);

public static partial class ClaudeSessions
{
    const int MaxFoldLines = 400;
    const long InitialTailBytes = 8 * 1024 * 1024;

    sealed class TranscriptState
    {
        public long Position;
        public string? Cwd;
        public readonly List<TEntry> Entries = [];
        public readonly TaskTracker Tasks = new();
    }

    static readonly Dictionary<string, TranscriptState> Transcripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses only the bytes appended since the last call. The start of the view stays fixed, so the
    /// rendered text only grows at the end and the output pane can update it in place.
    /// </summary>
    static List<TEntry> ReadTranscript(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return [];
        TranscriptState st;
        lock (Transcripts)
            if (!Transcripts.TryGetValue(path, out st!)) Transcripts[path] = st = new TranscriptState();

        lock (st)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = fs.Length;
                if (length < st.Position) // file was rewritten
                {
                    st.Position = 0;
                    st.Entries.Clear();
                    st.Tasks.Ordered.Clear();
                    st.Tasks.ByToolUse.Clear();
                    st.Tasks.StopCalls.Clear();
                }
                var skipPartialLine = false;
                if (st.Position == 0 && length > InitialTailBytes)
                {
                    st.Position = length - InitialTailBytes;
                    skipPartialLine = true;
                }
                if (length > st.Position)
                {
                    fs.Seek(st.Position, SeekOrigin.Begin);
                    var buf = new byte[length - st.Position];
                    fs.ReadExactly(buf);
                    var end = Array.LastIndexOf(buf, (byte)'\n'); // only consume complete lines
                    if (end >= 0)
                    {
                        var start = skipPartialLine ? Array.IndexOf(buf, (byte)'\n') + 1 : 0;
                        while (start <= end)
                        {
                            var nl = Array.IndexOf(buf, (byte)'\n', start);
                            var line = Encoding.UTF8.GetString(buf, start, nl - start).TrimEnd('\r');
                            start = nl + 1;
                            if (line.Length == 0) continue;
                            ScanForTasks(line, st);
                            if (ParseEntry(line, st) is { } entry) st.Entries.Add(entry);
                        }
                        st.Position += end + 1;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return [.. st.Entries];
        }
    }

    static TEntry? ParseEntry(string line, TranscriptState st)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String) st.Cwd = c.GetString();
            if (!IsTranscriptEntry(root, out var type, out var content)) return null;
            DateTime? at = root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt)
                ? dt.ToLocalTime() : null;
            var time = at?.ToString("HH:mm") ?? "";
            var items = new List<TItem>();

            if (type == "user")
            {
                var userText = ExtractUserText(root.GetProperty("message"));
                if (content.ValueKind == JsonValueKind.Array)
                {
                    var results = content.EnumerateArray()
                        .Where(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "tool_result").ToList();
                    JsonElement? meta = results.Count == 1 && root.TryGetProperty("toolUseResult", out var tur)
                        && tur.ValueKind == JsonValueKind.Object ? tur : null;
                    foreach (var b in results) items.Add(ParseToolResult(b, meta));
                }
                return userText.Length == 0 && items.Count == 0 ? null : new TEntry("user", time, userText, items, at);
            }

            if (content.ValueKind != JsonValueKind.Array) return null;
            // One API message is split over several lines (one per content block); usage repeats on each.
            var msg = root.GetProperty("message");
            var messageId = msg.TryGetProperty("id", out var mid) ? mid.GetString() : null;
            var outputTokens = msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv) ? otv : 0;
            foreach (var b in content.EnumerateArray())
            {
                var bt = b.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (bt == "text" && b.TryGetProperty("text", out var tx) && tx.GetString() is { Length: > 0 } s)
                    items.Add(new TText(s.Trim()));
                else if (bt == "tool_use")
                    items.Add(new TToolUse(
                        b.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        b.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool",
                        ToolArg(b.TryGetProperty("input", out var inp) ? inp : default, st.Cwd)));
            }
            // Thinking-only entries render nothing but still count toward the turn's tokens and time.
            return items.Count == 0 && messageId == null ? null
                : new TEntry("assistant", time, "", items, at, messageId, outputTokens);
        }
    }

    /// <summary>
    /// Timing and token use of the latest turn, measured like the Claude Code status line: from the
    /// last prompt you typed, counting each API message's output tokens once.
    /// </summary>
    public static TurnActivity? GetTurnActivity(string path)
    {
        var entries = ReadTranscript(path);
        var startIndex = entries.FindLastIndex(e => e.Type == "user" && e.UserText.Length > 0 && e.At != null);
        if (startIndex < 0) return null;
        var start = entries[startIndex].At!.Value;
        var last = start;
        var tokens = new Dictionary<string, int>();
        foreach (var e in entries.Skip(startIndex + 1))
        {
            if (e.At is { } at && at > last) last = at;
            if (e.MessageId is { } id) tokens[id] = Math.Max(tokens.GetValueOrDefault(id), e.OutputTokens);
        }
        return new TurnActivity(start, last, tokens.Values.Sum());
    }

    static TToolResult ParseToolResult(JsonElement block, JsonElement? meta)
    {
        var id = block.TryGetProperty("tool_use_id", out var i) ? i.GetString() ?? "" : "";
        var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        var lines = SystemReminderRegex().Replace(ToolResultText(block), "").Replace("\r\n", "\n").Trim('\n').Split('\n');

        List<THunk>? patch = null;
        string[]? written = null;
        var writtenTotal = 0;
        int? readLines = null;
        if (meta is { } m)
        {
            if (m.TryGetProperty("structuredPatch", out var sp) && sp.ValueKind == JsonValueKind.Array && sp.GetArrayLength() > 0)
            {
                patch = [];
                foreach (var h in sp.EnumerateArray())
                {
                    patch.Add(new THunk(
                        h.TryGetProperty("oldStart", out var os) ? os.GetInt32() : 1,
                        h.TryGetProperty("newStart", out var ns) ? ns.GetInt32() : 1,
                        h.TryGetProperty("lines", out var ls) ? ls.EnumerateArray().Select(l => l.GetString() ?? "").ToArray() : []));
                }
            }
            else if (m.TryGetProperty("type", out var ty) && ty.GetString() == "create"
                     && m.TryGetProperty("content", out var wc) && wc.ValueKind == JsonValueKind.String)
            {
                var all = wc.GetString()!.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                written = all.Take(MaxFoldLines).ToArray();
                writtenTotal = all.Length;
            }
            if (m.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.Object
                && f.TryGetProperty("numLines", out var nl) && nl.TryGetInt32(out var count))
                readLines = count;
        }
        return new TToolResult(id, string.Join('\n', lines.Take(MaxFoldLines + 1)), lines.Length, isError,
            patch, written, writtenTotal, readLines);
    }

    /// <summary>
    /// Renders a transcript the way the Claude Code terminal shows it: prompts, Claude's text, and per
    /// tool call a header plus a one-line summary. Diffs and full tool output go into collapsible folds.
    /// </summary>
    public static RenderedOutput RenderOutput(string path)
    {
        var entries = ReadTranscript(path);
        var results = entries.SelectMany(e => e.Items).OfType<TToolResult>()
            .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());

        var o = new OutputBuilder();
        var last = "";
        var itemsInTurn = 0;
        void Header(string kind, string text)
        {
            if (last == kind && kind != "user") return;
            if (o.Length > 0) o.Line("");
            o.Line(text);
            last = kind;
            itemsInTurn = 0;
        }
        // Like the terminal: a blank line between Claude's text blocks and tool calls.
        void Separate() { if (itemsInTurn++ > 0) o.Line(""); }

        foreach (var e in entries)
        {
            if (e.Type == "user")
            {
                if (e.UserText.Length == 0) continue;
                Header("user", $"❯ You  ·  {e.Time}");
                foreach (var l in e.UserText.TrimEnd().Split('\n')) o.Line(l.TrimEnd('\r'));
                continue;
            }
            foreach (var item in e.Items)
            {
                Header("claude", $"● Claude  ·  {e.Time}");
                Separate();
                if (item is TText t) foreach (var l in t.Text.Split('\n')) o.Line(l.TrimEnd('\r'));
                else if (item is TToolUse u) RenderTool(o, u, results.GetValueOrDefault(u.Id));
            }
        }
        return o.Build();
    }

    static void RenderTool(OutputBuilder o, TToolUse tool, TToolResult? r)
    {
        var label = tool.Name is "Edit" or "MultiEdit" ? "Update" : tool.Name;
        o.Line($"  ● {label}({tool.Arg})");
        if (r == null)
        {
            o.Line("    ⎿  Running…");
            return;
        }

        var body = new List<(string Text, char Diff)>();
        string summary;
        var more = 0;

        if (r.Patch is { Count: > 0 } patch)
        {
            int added = 0, removed = 0;
            for (var h = 0; h < patch.Count; h++)
            {
                if (h > 0) body.Add(("         ⋮", '\0'));
                int oldLine = patch[h].OldStart, newLine = patch[h].NewStart;
                foreach (var raw in patch[h].Lines)
                {
                    var s = raw.TrimEnd('\r');
                    var sign = s.Length > 0 ? s[0] : ' ';
                    var rest = s.Length > 0 ? s[1..] : "";
                    switch (sign)
                    {
                        case '+': body.Add(($"      {newLine++,5} + {rest}", '+')); added++; break;
                        case '-': body.Add(($"      {oldLine++,5} - {rest}", '-')); removed++; break;
                        default: body.Add(($"      {newLine++,5}   {rest}", '\0')); oldLine++; break;
                    }
                }
            }
            summary = $"Added {Plural(added, "line")}, removed {Plural(removed, "line")}";
        }
        else if (r.Written is { } written)
        {
            for (var k = 0; k < written.Length; k++) body.Add(($"      {k + 1,5} + {written[k]}", '+'));
            more = r.WrittenTotal - written.Length;
            summary = $"Wrote {Plural(r.WrittenTotal, "line")} to {tool.Arg}";
        }
        else if (tool.Name == "Read")
        {
            summary = $"Read {Plural(r.ReadLines ?? r.TotalLines, "line")}";
        }
        else
        {
            var lines = r.Text.Split('\n');
            var first = lines[0].TrimEnd();
            summary = first.Length == 0 ? "(no output)" : first.Length > 200 ? first[..200] + "…" : first;
            foreach (var l in lines.Skip(1)) body.Add(("       " + l.TrimEnd(), '\0'));
            more = r.TotalLines - lines.Length;
        }

        if (r.IsError) summary = "Error: " + summary;
        o.Line("    ⎿  " + summary);

        if (body.Count == 0) return;
        if (body.Count > MaxFoldLines) { more += body.Count - MaxFoldLines; body = body.Take(MaxFoldLines).ToList(); }
        if (more > 0) body.Add(($"       … {more} more lines", '\0'));
        o.Fold(tool.Id, body);
    }
}
