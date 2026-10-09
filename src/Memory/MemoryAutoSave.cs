using System.Text.Json;
using System.Text.RegularExpressions;
using VanityStudio.Infra;

namespace VanityStudio.Memory;

/// <summary>The smart memory: after every turn that used tools, one model call reads the task, the reply and the
/// numbered steps and records what those steps' RESULTS established about the project (where things live, what
/// commands returned, what exists), so the next session starts from it instead of finding it again. Each fact must
/// cite a step whose result shows it; an instruction or an unproven conclusion is not saved. Saved facts go through a
/// judge: duplicates are skipped, contradicted automatic notes replaced, the operator's own notes never touched.
/// Ported from the original agent's step analyzer, without personas and the board.</summary>
public sealed class MemoryAutoSave
{
    private readonly JsonFileMemoryStore _store;
    private readonly Func<string, string, CancellationToken, Task<string>> _llm;
    private readonly Action<string>? _notify;

    private const string AnalyzeSystem =
        "You read the steps of one work turn of a command-line agent on a project and record what the turn established about the project, " +
        "so a later session on the same project starts from it instead of finding it again.\n" +
        "Input: the operator's request, the agent's final reply, and the numbered steps - each tool call with the start of its result.\n" +
        "Return a JSON array only, no markdown: [{\"title\":\"short label\",\"content\":\"one sentence\",\"steps\":[12]}]. " +
        "\"steps\" are the numbers of the steps whose RESULT shows the fact; [] only for a value the operator wrote in the request (a URL, a login, a path).\n" +
        "Record what a later session would otherwise have to rediscover or would get wrong:\n" +
        "- where something lives: the file, function, route, table or setting that owns a behaviour or a value;\n" +
        "- how the project is built, run, tested or deployed, as the commands and their results showed it;\n" +
        "- what exists or does not exist, and its format; versions and tools installed on the machine;\n" +
        "- what a command, request or page returned, including an error and the cause its result names;\n" +
        "- the operator's stated preferences about how work should be done.\n" +
        "Each content is ONE sentence stating what IS or what HAPPENED. Never an instruction or a procedure - no \"must\", \"should\", \"need to\", " +
        "\"make sure\": how to work is not a fact (a preference the operator stated is a fact: \"The operator prefers X\").\n" +
        "Only what a step's result shows. The request and the reply say what was attempted; they prove nothing, and a conclusion found only there " +
        "is not recorded - except a value the operator wrote in the request. A step marked [not evidence] is the agent's own bookkeeping and proves " +
        "nothing either. A step marked [file changed later by #n] shows the file before that change.\n" +
        "Never copy a file's contents. Leave out progress, the steps themselves, and anything that differs from run to run (counts, times, sizes). " +
        "Prefer few durable facts; a greeting or a question answered from general knowledge yields []. Return [] if nothing qualifies.";

    private static readonly Regex Instruction = new(@"(?i)\b(must|should|have to|has to|needs? to|make sure|remember to|ensure that)\b", RegexOptions.Compiled);
    private static readonly Regex FenceStrip = new(@"^```[a-z]*\n?|\n?```$", RegexOptions.Multiline | RegexOptions.Compiled);

    public MemoryAutoSave(JsonFileMemoryStore store, Func<string, string, CancellationToken, Task<string>> llm, Action<string>? notify = null)
    {
        _store = store; _llm = llm; _notify = notify;
    }

    /// <summary>Who the automatic save writes as; only these notes may be replaced by a later analysis.</summary>
    public const string AutoAuthor = "vanity";
    public static bool IsAutoSaved(MemoryEntry e) => string.Equals(e.AgentId, AutoAuthor, StringComparison.OrdinalIgnoreCase);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonFileMemoryStore, HashSet<Task>> Pending = new();

    /// <summary>Analyze a finished turn in the background. A turn without tool calls observed nothing and is skipped.</summary>
    public void Handle(string task, string reply, IReadOnlyList<RunStep> steps, string? workspace)
    {
        if (steps.Count == 0 || !steps.Any(RunTimeline.IsEvidence)) return;
        var work = HandleAsync(task, reply, steps, workspace);
        var pending = Pending.GetOrCreateValue(_store);
        lock (pending) pending.Add(work);
        _ = work.ContinueWith(done => { lock (pending) pending.Remove(done); }, TaskScheduler.Default);
    }

    /// <summary>Wait, at most <paramref name="max"/>, for analyses of earlier turns to land, so the next turn's prompt
    /// carries what the previous one learned.</summary>
    public static async Task WhenSavedAsync(JsonFileMemoryStore? store, TimeSpan max, CancellationToken ct)
    {
        if (store is null || !Pending.TryGetValue(store, out var pending)) return;
        Task[] running;
        lock (pending) running = pending.ToArray();
        if (running.Length == 0) return;
        try { await Task.WhenAll(running).WaitAsync(max, ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (Exception) { }
    }

    internal static string AnalyzerInput(string task, string reply, IReadOnlyList<RunStep> steps, string? workspace)
    {
        static string Clip(string s, int max, bool tail = false) =>
            string.IsNullOrWhiteSpace(s) ? "none" : s.Length <= max ? s.Trim() : tail ? "…" + s[^max..].Trim() : s[..max].Trim() + "…";
        return "REQUEST\n" + Clip(task, 4000) +
               "\n\nTHE AGENT'S FINAL REPLY (its own words, not evidence)\n" + Clip(reply, 2500, tail: true) +
               "\n\nSTEPS\n" + RunTimeline.Build(steps, workspace);
    }

    internal static List<int> CitedSteps(JsonElement item, IReadOnlyList<RunStep> steps)
    {
        var cited = new List<int>();
        if (!item.TryGetProperty("steps", out var s) && !item.TryGetProperty("step", out s)) return cited;
        IEnumerable<JsonElement> values = s.ValueKind == JsonValueKind.Array ? s.EnumerateArray() : [s];
        foreach (var v in values)
        {
            int n = v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
                  : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString()!.Trim().TrimStart('#'), out var j) ? j : 0;
            if (n >= 1 && n <= steps.Count && RunTimeline.IsEvidence(steps[n - 1]) && !cited.Contains(n)) cited.Add(n);
        }
        return cited;
    }

    /// <summary>A value quoted from the request (an address, a path, a login) stands without a step.</summary>
    internal static bool QuotesTheTask(string content, string? task)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(task)) return false;
        foreach (Match m in Regex.Matches(content, @"\S{6,}"))
        {
            var token = m.Value.Trim('`', '"', '\'', ',', '.', ';', ':', '(', ')');
            if (token.Length < 6) continue;
            if (!token.Any(ch => char.IsDigit(ch) || ch == '@' || (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '/' && ch != '_'))) continue;
            if (task.Contains(token, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private async Task HandleAsync(string task, string reply, IReadOnlyList<RunStep> steps, string? workspace)
    {
        try
        {
            var raw = await _llm(AnalyzeSystem, AnalyzerInput(task, reply, steps, workspace), CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return;

            var json = raw.Trim();
            if (json.StartsWith("```")) json = FenceStrip.Replace(json, "").Trim();
            int open = json.IndexOf('['), close = json.LastIndexOf(']');
            if (!json.StartsWith('[') && open >= 0 && close > open) json = json[open..(close + 1)];

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            Log.Info($"[memory:auto] analyzed {steps.Count} step(s), {doc.RootElement.GetArrayLength()} fact(s) proposed");

            var existing = new List<MemoryEntry>(await _store.ListAsync(CancellationToken.None).ConfigureAwait(false));
            var saved = new List<string>(); var replaced = new List<string>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var title   = item.TryGetProperty("title",   out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                var content = item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (title.Length == 0) title = content.Split('\n', 2)[0].Trim();
                if (title.Length > 80) title = title[..77] + "...";

                var cited = CitedSteps(item, steps);
                bool given = cited.Count == 0 && QuotesTheTask(content, task);
                if (cited.Count == 0 && !given) { Log.Info($"[memory:auto] not saved, no step shows it: {title}"); continue; }
                if (Instruction.IsMatch(content)) { Log.Info($"[memory:auto] not saved, an instruction: {title}"); continue; }

                var (verdict, old) = await JudgeAsync(title, content, existing, CancellationToken.None).ConfigureAwait(false);
                if (verdict == Verdict.Duplicate) { Log.Info($"[memory:auto] duplicate: {title}"); continue; }
                if (verdict == Verdict.Replaces)
                {
                    if (old.Any(o => !IsAutoSaved(o))) { Log.Info($"[memory:auto] contradicts a note the operator wrote, kept theirs: {title}"); continue; }
                    foreach (var o in old)
                    {
                        await _store.DeleteAsync(o.Id, CancellationToken.None).ConfigureAwait(false);
                        existing.Remove(o);
                        replaced.Add(TitleOf(o) + " -> " + title);
                    }
                }

                var entry = new MemoryEntry { AgentId = AutoAuthor, Content = content, Metadata = new Dictionary<string, string> { ["title"] = title } };
                await _store.SaveAsync(entry).ConfigureAwait(false);
                existing.Add(entry);
                saved.Add(title);
            }

            if (saved.Count > 0 || replaced.Count > 0)
            {
                Log.Info($"[memory:auto] saved {saved.Count}, replaced {replaced.Count}");
                _notify?.Invoke("memory: " + (saved.Count > 0 ? "saved " + string.Join("; ", saved) : "") + (replaced.Count > 0 ? (saved.Count > 0 ? " · " : "") + "replaced " + string.Join("; ", replaced) : ""));
            }
        }
        catch (Exception ex) { Log.Warn($"[memory:auto] analysis failed: {ex.Message}"); }
    }

    private static string TitleOf(MemoryEntry e) => e.Metadata.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t) ? t : e.Content.Split('\n', 2)[0];

    internal enum Verdict { New, Duplicate, Replaces }

    private const string JudgeSystem =
        "You compare a new project fact with existing notes about the same project. Answer with exactly one line:\n" +
        "DUPLICATE - the notes already say this.\n" +
        "REPLACES <n> - the new fact is about the same thing as note <n> and supersedes it: a changed value, a corrected conclusion, " +
        "or the opposite finding (several notes: REPLACES 2,4).\n" +
        "NEW - the new fact adds information and contradicts none of the notes.";

    internal async Task<(Verdict Verdict, List<MemoryEntry> Old)> JudgeAsync(string title, string content, IReadOnlyList<MemoryEntry> existing, CancellationToken ct)
    {
        try
        {
            var candidates = existing
                .Select(e => (e, score: TokenScore(title + " " + content, e.Content)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Take(5)
                .Select(x => x.e)
                .ToList();
            if (candidates.Count == 0) return (Verdict.New, []);

            var notes  = string.Join("\n\n", candidates.Select((e, i) => $"{i + 1}. {e.Content}"));
            var answer = (await _llm(JudgeSystem, $"New fact:\n{title}: {content}\n\nExisting notes:\n{notes}", ct).ConfigureAwait(false)).Trim();
            var m = Regex.Match(answer, @"\b(DUPLICATE|REPLACES|NEW)\b([\d,\s]*)", RegexOptions.IgnoreCase);
            if (!m.Success) return (Verdict.New, []);
            switch (m.Groups[1].Value.ToUpperInvariant())
            {
                case "DUPLICATE": return (Verdict.Duplicate, []);
                case "REPLACES":
                    var old = Regex.Matches(m.Groups[2].Value, @"\d+")
                        .Select(x => int.Parse(x.Value) - 1).Where(i => i >= 0 && i < candidates.Count)
                        .Distinct().Select(i => candidates[i]).ToList();
                    return old.Count > 0 ? (Verdict.Replaces, old) : (Verdict.New, []);
                default: return (Verdict.New, []);
            }
        }
        catch { return (Verdict.New, []); }
    }

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
        { "the", "a", "an", "is", "are", "of", "in", "on", "at", "to", "for", "and", "or", "with", "this", "that", "it", "its" };

    private static double TokenScore(string query, string text)
    {
        var haystack = Tok(text);
        if (haystack.Count == 0) return 0;
        double score = 0;
        foreach (var q in Tok(query))
        {
            double best = 0;
            foreach (var w in haystack)
            {
                if (w == q) { best = 2; break; }
                if (q.Length >= 4 && (w.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(w, StringComparison.Ordinal))) best = Math.Max(best, 1.5);
                else if (q.Length >= 4 && w.Contains(q, StringComparison.Ordinal)) best = Math.Max(best, 1);
            }
            score += best;
        }
        return score;
    }

    private static List<string> Tok(string text)
        => Regex.Split((text ?? "").ToLowerInvariant(), @"[^a-z0-9_./\-]+").Where(w => w.Length > 1 && !Noise.Contains(w)).ToList();
}
