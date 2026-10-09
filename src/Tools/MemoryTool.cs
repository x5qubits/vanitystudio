using System.Text.Json;
using System.Text.RegularExpressions;
using VanityStudio.Infra;
using VanityStudio.Llm;
using VanityStudio.Memory;

namespace VanityStudio.Tools;

/// <summary>Project memory for the agent: facts worth keeping between sessions of one workspace (paths, commands
/// that work, decisions, how things are wired). Search ranks notes by the words they share with the query and
/// returns the notes themselves; save writes a note (a note on a subject already stored replaces it); delete removes
/// one by number.</summary>
public sealed class MemoryTool : ITool
{
    private readonly JsonFileMemoryStore _store;

    public MemoryTool(JsonFileMemoryStore store) => _store = store;

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "memory",
        Description =
            "Project memory: notes kept between sessions of this workspace - file paths, build and run commands that work, " +
            "architecture decisions, environment facts, the operator's preferences.\n" +
            "search `query`: words or a question - returns the matching notes, or 'Not in memory'.\n" +
            "list: all note titles with age.\n" +
            "save `title` + `content`: keep a fact the next session would otherwise have to rediscover. One subject per note; " +
            "saving a subject again replaces the earlier note.\n" +
            "delete `id`: remove a note by its number.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["action"]  = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "search", "list", "save", "delete" }, ["description"] = "search, list, save or delete." },
                ["query"]   = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Words or a question to find notes by (search)." },
                ["title"]   = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Short subject line of the note (save)." },
                ["content"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The fact itself, concrete and self-contained (save)." },
                ["id"]      = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "The note number to delete." },
            },
            ["required"] = new[] { "action" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException ex) { return "Error: arguments are not valid JSON: " + ex.Message; }

        string action = S(root, "action").Trim().ToLowerInvariant();
        IReadOnlyList<MemoryEntry> entries;
        try { entries = await _store.ListAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error($"[memory] load failed: {ex.Message}"); return "Memory unavailable: " + ex.Message; }

        switch (action)
        {
            case "list":
                return RenderList(entries);

            case "search":
                string query = S(root, "query").Trim();
                if (query.Length == 0) return "Error: 'search' needs a 'query'.";
                return Answer(entries, query);

            case "save":
            case "add":
            case "write":
                var title = S(root, "title").Trim();
                var content = S(root, "content").Trim();
                if (content.Length == 0 && title.Length == 0) return "Error: 'save' needs 'content' (and ideally a 'title').";
                if (content.Length == 0) content = title;
                if (title.Length == 0) title = content.Split('\n')[0];
                if (title.Length > 80) title = title[..77] + "...";
                var entry = new MemoryEntry { AgentId = AgentToolContext.AgentId ?? "agent", Content = content, Metadata = new() { ["title"] = title } };
                await _store.SaveAsync(entry, ct).ConfigureAwait(false);
                return $"Saved note #{_store.NumberOf(entry.Id)}: {title}";

            case "delete":
            case "remove":
                int id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32()
                       : int.TryParse(S(root, "id").Trim().TrimStart('#'), out var parsed) ? parsed : 0;
                var victim = id > 0 ? _store.ByNumber(id) : null;
                if (victim is null) return $"Error: no note #{id}. Use list to see the numbers.";
                await _store.DeleteAsync(victim.Id, ct).ConfigureAwait(false);
                return $"Deleted note #{id}.";

            default:
                return "Error: memory needs action=search|list|save|delete.";
        }
    }

    // ── render ───────────────────────────────────────────────────────────────────────────────────────────────────

    private string RenderList(IReadOnlyList<MemoryEntry> entries)
    {
        if (entries.Count == 0) return "(memory is empty)";
        var lines = entries.OrderByDescending(e => e.CreatedAt).Select(e => "- " + Number(e) + GetTitle(e) + Age(e));
        return string.Join("\n", lines) + $"\n({entries.Count} note(s))";
    }

    private string Answer(IReadOnlyList<MemoryEntry> entries, string query)
    {
        if (entries.Count == 0) return "Not in memory.";
        var ranked = entries
            .Select(e => (e, score: Score(GetTitle(e), query) * 2 + Score(e.Content, query)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(5)
            .Select(x => x.e)
            .ToList();
        return ranked.Count > 0 ? Render(ranked) : $"Not in memory. ({entries.Count} note(s) stored; use list to browse titles.)";
    }

    private string Render(IEnumerable<MemoryEntry> notes) =>
        string.Join("\n\n", notes.Select(e => "## " + Number(e) + GetTitle(e) + Age(e) + "\n" + e.Content));

    private string Number(MemoryEntry e)
    {
        var n = _store.NumberOf(e.Id);
        return n > 0 ? $"#{n} " : "";
    }

    // ── search scoring (token-based: exact word = 2, prefix/stem = 1.5, substring = 1, phrase bonus = 2x) ──────

    private static double Score(string text, string query)
    {
        var haystack = Tokens(text);
        if (haystack.Count == 0) return 0;
        double score = 0;
        foreach (var q in Tokens(query))
        {
            double best = 0;
            foreach (var w in haystack)
            {
                if (w == q) { best = 2; break; }
                if (q.Length >= 4 && (w.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(w, StringComparison.Ordinal)))
                    best = Math.Max(best, 1.5);
                else if (q.Length >= 4 && w.Contains(q, StringComparison.Ordinal))
                    best = Math.Max(best, 1);
            }
            score += best;
        }
        if (score > 0 && text.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) score *= 2;
        return score;
    }

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
        { "the", "a", "an", "is", "are", "of", "in", "on", "at", "to", "for", "and", "or", "with", "this", "that", "it", "its", "what", "which", "where", "how" };

    private static List<string> Tokens(string text)
        => Regex.Split((text ?? "").ToLowerInvariant(), @"[^a-z0-9_.$/-]+").Where(w => w.Length > 1 && !Noise.Contains(w)).ToList();

    private static string GetTitle(MemoryEntry e)
        => e.Metadata.TryGetValue("title", out var t) && !string.IsNullOrEmpty(t) ? t : Clip(e.Content, 60);

    private static string Age(MemoryEntry e)
    {
        int days = (int)(DateTimeOffset.UtcNow.Date - e.CreatedAt.Date).TotalDays;
        return " (" + (days <= 0 ? "today" : days == 1 ? "yesterday" : days + " days ago") + ")";
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";
    private static string S(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
