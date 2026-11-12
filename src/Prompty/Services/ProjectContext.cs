using System.Text;

namespace Prompty.Services;

/// <summary>
/// A compact picture of the project a Claude session works in: its instruction and readme files and a
/// shallow file tree. Feeds the prompt generator, so it can name real files instead of guessing.
/// </summary>
public static class ProjectContext
{
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "packages", "dist", "build", "out",
        "target", "__pycache__", ".venv", "venv", "Library", "Temp", "Logs", "UserSettings", ".gradle", ".next",
    };

    static readonly string[] DocFiles = ["CLAUDE.md", ".claude/CLAUDE.md", "README.md", "readme.md", "README.txt"];

    static readonly Dictionary<string, (DateTime At, string Text)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Build(string? dir, int maxChars = 6000)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
        lock (Cache)
            if (Cache.TryGetValue(dir, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(30))
                return hit.Text;

        var sb = new StringBuilder();
        sb.AppendLine($"Project folder: {dir}");

        var docBudget = maxChars / 2;
        foreach (var name in DocFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) || docBudget <= 200) continue;
            try
            {
                var text = File.ReadAllText(path).Trim();
                if (text.Length > docBudget) text = text[..docBudget] + "\n…";
                docBudget -= text.Length;
                sb.AppendLine().AppendLine($"### {name}").AppendLine(text);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        sb.AppendLine().AppendLine("### Files (depth 3)");
        var lines = 0;
        void Walk(string d, int depth)
        {
            if (depth > 3 || lines >= 150) return;
            string[] subdirs, files;
            try
            {
                subdirs = Directory.GetDirectories(d);
                files = Directory.GetFiles(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            var indent = new string(' ', (depth - 1) * 2);
            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (lines++ >= 150) { sb.AppendLine(indent + "…"); return; }
                sb.AppendLine(indent + Path.GetFileName(f));
            }
            foreach (var s in subdirs.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(s);
                if (SkipDirs.Contains(name) || name.StartsWith('.')) continue;
                if (lines++ >= 150) { sb.AppendLine(indent + "…"); return; }
                sb.AppendLine(indent + name + "/");
                Walk(s, depth + 1);
            }
        }
        Walk(dir, 1);

        var result = sb.Length > maxChars ? sb.ToString(0, maxChars) + "\n…" : sb.ToString();
        lock (Cache) Cache[dir] = (DateTime.UtcNow, result);
        return result;
    }
}
