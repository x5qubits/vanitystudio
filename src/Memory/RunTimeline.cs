using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VanityStudio.Memory;

/// <summary>One tool call of a turn and the start of its result. <paramref name="Number"/> counts from 1 in call order.</summary>
public sealed record RunStep(int Number, string Tool, string ArgsJson, string Result, bool IsError);

/// <summary>A turn's steps as numbered lines one model call can read: each tool call with its main argument and the
/// start of what came back. Built in code, never by a model, so the step numbers the analyzer cites are real.</summary>
public static class RunTimeline
{
    private const int Budget = 60_000;
    private const int MinResult = 120, MaxResult = 600, MaxArg = 200;

    private static readonly string[] MainArgs =
        ["command", "action", "path", "file_path", "paths", "pattern", "glob", "url", "query", "persona", "id", "name", "title", "text", "task", "names"];

    /// <summary>Tools whose result is the agent's own bookkeeping - stored memory, a loaded skill or tool, its notes -
    /// not something the project showed. Cited as proof, they would let an old note prove itself.</summary>
    private static readonly HashSet<string> Bookkeeping = new(StringComparer.OrdinalIgnoreCase) { "memory", "skill_view", "load_tools", "task_scratchpad", "list_tools", "date_time" };

    private static readonly HashSet<string> FileWriters = new(StringComparer.OrdinalIgnoreCase) { "edit_file", "write_file", "write_files" };

    public static bool IsEvidence(RunStep step) => !Bookkeeping.Contains(step.Tool);

    public static string Build(IReadOnlyList<RunStep> steps, string? workspace = null)
    {
        if (steps.Count == 0) return "";
        var changedLater = ChangedLater(steps, workspace);
        var heads = steps.Select((s, i) => ($"#{s.Number} {s.Tool} {Args(s.ArgsJson)}".TrimEnd()
            + (IsEvidence(s) ? "" : " [not evidence]")
            + (changedLater.TryGetValue(i, out var later) ? $" [file changed later by #{later}]" : ""))).ToList();
        int headChars = heads.Sum(h => h.Length + 4);
        int per = Math.Clamp((Budget - headChars) / steps.Count, MinResult, MaxResult);
        var seen = new Dictionary<string, (int Number, string Result)>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var result = Flat(s.Result);
            var key = s.Tool + "\n" + s.ArgsJson;
            sb.Append(heads[i]);
            if (seen.TryGetValue(key, out var first) && first.Result == result)
            {
                sb.Append(" -> same result as #").Append(first.Number).Append('\n');
                continue;
            }
            seen[key] = (s.Number, result);
            var cap = s.IsError ? per * 2 : per;
            sb.Append(s.IsError ? " -> ERROR: " : " -> ")
              .Append(result.Length == 0 ? "(empty)" : result.Length > cap ? result[..cap] + "…" : result)
              .Append('\n');
        }
        return sb.ToString();
    }

    private static Dictionary<int, int> ChangedLater(IReadOnlyList<RunStep> steps, string? workspace)
    {
        var marks = new Dictionary<int, int>();
        var files = steps.Select(s => Files(s, workspace)).ToList();
        for (int j = 0; j < steps.Count; j++)
        {
            if (!FileWriters.Contains(steps[j].Tool) || steps[j].IsError) continue;
            foreach (var changed in files[j])
                for (int i = 0; i < j; i++)
                    if (!marks.ContainsKey(i) && !FileWriters.Contains(steps[i].Tool) && files[i].Contains(changed))
                        marks[i] = steps[j].Number;
        }
        return marks;
    }

    private static readonly HashSet<string> PathArgs = new(StringComparer.OrdinalIgnoreCase) { "file_path", "path", "paths" };
    private static readonly Regex Token = new(@"[^\s""'`;|&<>(),=]+", RegexOptions.Compiled);
    private static readonly Regex FileName = new(@"[\\/][^\\/]+\.[A-Za-z0-9]{1,6}$", RegexOptions.Compiled);

    private static HashSet<string> Files(RunStep step, string? workspace)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(step.ArgsJson);
            Walk(doc.RootElement, null);
        }
        catch (JsonException) { }
        return found;

        void Walk(JsonElement e, string? name)
        {
            if (e.ValueKind == JsonValueKind.Object) { foreach (var p in e.EnumerateObject()) Walk(p.Value, p.Name); return; }
            if (e.ValueKind == JsonValueKind.Array) { foreach (var v in e.EnumerateArray()) Walk(v, name); return; }
            if (e.ValueKind != JsonValueKind.String || name is null) return;
            var text = e.GetString() ?? "";
            if (PathArgs.Contains(name)) Add(text);
            else if (name.Equals("command", StringComparison.OrdinalIgnoreCase))
                foreach (Match m in Token.Matches(text))
                    if (FileName.IsMatch(m.Value)) Add(m.Value);
        }

        void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains("://")) return;
            try
            {
                var full = Path.IsPathRooted(path) || string.IsNullOrEmpty(workspace) ? path : Path.Combine(workspace, path);
                found.Add(Path.GetFullPath(full).Replace('\\', '/').TrimEnd('/'));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
    }

    private static string Args(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Cap(Flat(json), MaxArg);
            var props = doc.RootElement.EnumerateObject().ToList();
            var chosen = MainArgs.Select(k => props.FirstOrDefault(p => p.Name.Equals(k, StringComparison.OrdinalIgnoreCase)))
                .Where(p => p.Value.ValueKind != JsonValueKind.Undefined).ToList();
            if (chosen.Count == 0) chosen = props.Take(3).ToList();
            return string.Join(" ", chosen.Select(p => p.Name + "=" + Cap(Flat(Value(p.Value)), MaxArg)));
        }
        catch (JsonException) { return Cap(Flat(json), MaxArg); }
    }

    private static string Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Array  => string.Join(", ", v.EnumerateArray().Select(Value)),
        _                    => v.GetRawText(),
    };

    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);
    private static string Flat(string text) => Space.Replace(Regex.Replace(text ?? "", @"\s*\r?\n\s*", " | "), " ").Trim(' ', '|');
    private static string Cap(string text, int max) => text.Length > max ? text[..max] + "…" : text;
}
