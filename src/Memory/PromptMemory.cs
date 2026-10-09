using System.Text;
using System.Text.RegularExpressions;

namespace VanityStudio.Memory;

/// <summary>Project memory in the system prompt. A small store is shown whole; a large one goes through one model
/// call that keeps only the notes relevant to this request (ordered first by the words they share with it), with the
/// plain list as the fallback when the filter fails or answers with something that is not a list.</summary>
public static class PromptMemory
{
    /// <summary>Up to this many characters the notes are shown whole, no filter call.</summary>
    public const int BulkChars = 7000;
    public const int MaxEntryChars = 500;
    /// <summary>How much of the store the filter is shown (about 6,000 tokens).</summary>
    public const int CatalogChars = 24_000;

    private const string CurateSystem =
        "You are a project memory filter. Given a request and project notes, return ONLY the notes that directly help with this request - " +
        "relevant file paths, how the project is built and run, known gotchas, config values, the operator's preferences. " +
        "Always keep the notes on how the project is built, tested and run. Be ruthlessly selective with everything else: if a note is not " +
        "directly useful for this specific request, omit it. Format: one bullet per note, keep each note's leading #number and text as given. " +
        "Return an empty string if nothing is relevant.";

    public static async Task<string> BlockAsync(JsonFileMemoryStore? store, string? task,
        Func<string, string, CancellationToken, Task<string>>? llm, CancellationToken ct)
    {
        if (store is null) return "";
        IReadOnlyList<MemoryEntry> entries;
        try { entries = await store.ListAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return ""; }
        if (entries.Count == 0) return "";

        var lines = entries.Select(e => Line(store, e)).ToList();
        int total = lines.Sum(l => l.Length + 1);
        if (total <= BulkChars || llm is null || string.IsNullOrWhiteSpace(task))
            return Bulk(lines, task, BulkChars, entries.Count);
        return await CurateAsync(lines, task, llm, ct).ConfigureAwait(false) is { Length: > 0 } curated ? curated : Bulk(lines, task, BulkChars, entries.Count);
    }

    private static string Header =>
        "# Project memory\nNotes from earlier sessions in this workspace (the memory tool searches, saves and deletes them; facts the turn proves are saved automatically):";

    private static string Line(JsonFileMemoryStore store, MemoryEntry e)
    {
        var title = e.Metadata.TryGetValue("title", out var t) && t.Length > 0 ? t : e.Content.Split('\n')[0];
        var body = OneLine(e.Content);
        if (body.Length > MaxEntryChars) body = body[..MaxEntryChars] + "…";
        var n = store.NumberOf(e.Id);
        return $"- #{n} {title} ({e.CreatedAt:yyyy-MM-dd}, {e.AgentId}): {body}";
    }

    private static string Bulk(List<string> lines, string? task, int maxChars, int count)
    {
        var terms = Terms(task);
        var ordered = terms.Count == 0 ? lines : lines.OrderByDescending(l => Relevance(l, terms)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        int used = 0, shown = 0;
        foreach (var l in ordered)
        {
            if (used + l.Length + 1 > maxChars && shown > 0) break;
            sb.AppendLine(l); used += l.Length + 1; shown++;
        }
        if (shown < count) sb.AppendLine($"- … {count - shown} more note(s): memory action=list or search");
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> CurateAsync(List<string> lines, string task, Func<string, string, CancellationToken, Task<string>> llm, CancellationToken ct)
    {
        var terms = Terms(task);
        var ordered = terms.Count == 0 ? lines : lines.OrderByDescending(l => Relevance(l, terms)).ToList();
        var catalog = new StringBuilder(); int used = 0;
        foreach (var l in ordered)
        {
            var short_ = l.Length > 260 ? l[..260] + "…" : l;
            if (used + short_.Length + 1 > CatalogChars && used > 0) break;
            catalog.AppendLine(short_); used += short_.Length + 1;
        }
        try
        {
            var answer = (await llm(CurateSystem, $"Request: {task}\n\nNotes:\n{catalog}", ct).ConfigureAwait(false)).Trim();
            if (answer.Length == 0 || answer.Equals("empty string", StringComparison.OrdinalIgnoreCase)) return "";
            if (!LooksLikeNoteList(answer, catalog.Length)) return "";
            return Header + "\n" + answer;
        }
        catch { return ""; }
    }

    internal static bool LooksLikeNoteList(string body, int catalogLength)
    {
        if (body.Length > Math.Max(2000, catalogLength)) return false;
        foreach (var marker in new[] { "<tool_call", "<tool_result", "</tool_call", "<function_call", "```tool", "[result " })
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static HashSet<string> Terms(string? task)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(task)) return set;
        foreach (Match m in Regex.Matches(task, @"[A-Za-z_][A-Za-z0-9_.\-]{3,}"))
        {
            var w = m.Value.Trim('.', '-', '_');
            if (w.Length >= 4 && !Scaffolding.Contains(w)) set.Add(w);
        }
        return set;
    }

    private static int Relevance(string content, HashSet<string> terms)
    {
        int hits = 0;
        foreach (var t in terms) if (content.Contains(t, StringComparison.OrdinalIgnoreCase)) hits++;
        return hits;
    }

    private static readonly HashSet<string> Scaffolding = new(StringComparer.OrdinalIgnoreCase)
    {
        "this","that","with","from","must","should","will","have","been","which","there","they","then","than","into","only",
        "also","just","make","made","does","doing","before","after","every","each","the","and","for","not","please","what","when",
        "where","about","could","would","your","want","need","like","file","files","current","time",
    };

    private static string OneLine(string s) => string.Join(' ', s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
}
