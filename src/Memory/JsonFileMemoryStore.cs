using System.Text.Json;
using System.Text.Json.Serialization;
using VanityStudio.Infra;

namespace VanityStudio.Memory;

// Disk format: [{id, title, content, saved, by}]
internal sealed class MemoryRecord
{
    [JsonPropertyName("id")]      public int    Id      { get; set; }
    [JsonPropertyName("title")]   public string Title   { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("saved")]   public string Saved   { get; set; } = "";
    [JsonPropertyName("by")]      public string By      { get; set; } = "";
}

/// <summary>File-backed project memory: one memory.json per workspace under the agent home. A note on a subject
/// already stored replaces the earlier one, so the prompt never carries two truths about the same thing.</summary>
public sealed class JsonFileMemoryStore : IMemoryStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<MemoryEntry> _entries = [];
    private Dictionary<string, int> _idMap = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JsonFileMemoryStore(string path)
    {
        _path = path;
        Load();
        Log.Info($"[memory] loaded {_entries.Count} note(s) from {_path}");
    }

    public string Path => _path;

    private void Load()
    {
        _entries = []; _idMap = new(StringComparer.OrdinalIgnoreCase); _nextId = 1;
        if (!File.Exists(_path)) return;
        try
        {
            var records = JsonSerializer.Deserialize<List<MemoryRecord>>(File.ReadAllText(_path), Json) ?? [];
            foreach (var r in records)
            {
                var entry = ToEntry(r);
                if (_idMap.ContainsKey(entry.Id)) continue;
                _entries.Add(entry);
                _idMap[entry.Id] = r.Id;
                if (r.Id >= _nextId) _nextId = r.Id + 1;
            }
            _entries = _entries.OrderByDescending(e => e.CreatedAt).ToList();
        }
        catch (Exception ex) { Log.Warn($"[memory] failed to read {_path}: {ex.Message}"); }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var records = _entries.OrderBy(e => _idMap.TryGetValue(e.Id, out var i) ? i : int.MaxValue).Select(ToRecord).ToList();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(records, Json), ct).ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);
    }

    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _entries.ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var idx = _entries.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0) _entries[idx] = entry;
            else
            {
                var superseded = _entries.Where(e => SameSubject(e, entry)).ToList();
                foreach (var old in superseded) { _entries.Remove(old); _idMap.Remove(old.Id); }
                if (superseded.Count > 0) Log.Info($"[memory] '{Title(entry)}' replaced {superseded.Count} earlier note(s) on the same subject");
                _entries.Insert(0, entry);
                _idMap[entry.Id] = _nextId++;
            }
            await FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(float[] queryEmbedding, int topK = 5, string? agentId = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);

    /// <summary>Every note: the store is per workspace, not per agent, so the agent id is not a filter here.</summary>
    public async Task<IReadOnlyList<MemoryEntry>> GetByAgentAsync(string agentId, int limit = 50, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _entries.Take(limit).ToList(); }
        finally { _gate.Release(); }
    }

    /// <summary>The note's number in memory.json (what the operator and the model refer to), or 0 when unknown.</summary>
    public int NumberOf(string entryId)
    {
        _gate.Wait();
        try { return _idMap.TryGetValue(entryId, out var n) ? n : 0; }
        finally { _gate.Release(); }
    }

    /// <summary>The entry with a given number, or null.</summary>
    public MemoryEntry? ByNumber(int number)
    {
        _gate.Wait();
        try
        {
            var id = _idMap.FirstOrDefault(kv => kv.Value == number).Key;
            return id is null ? null : _entries.FirstOrDefault(e => e.Id == id);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _entries.RemoveAll(e => e.Id == id);
            _idMap.Remove(id);
            await FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // ── format ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static MemoryEntry ToEntry(MemoryRecord r)
    {
        var created = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(r.Saved) && DateTimeOffset.TryParse(r.Saved, out var dt)) created = dt;
        return new MemoryEntry
        {
            Id = r.Id.ToString(), AgentId = r.By ?? "", Content = r.Content, CreatedAt = created,
            Metadata = string.IsNullOrEmpty(r.Title) ? new() : new() { ["title"] = r.Title },
        };
    }

    private MemoryRecord ToRecord(MemoryEntry e)
    {
        _idMap.TryGetValue(e.Id, out var intId);
        var title = Title(e);
        if (title.Length > 80) title = title[..77] + "...";
        return new MemoryRecord { Id = intId > 0 ? intId : _nextId, Title = title, Content = e.Content, Saved = e.CreatedAt.ToString("yyyy-MM-dd"), By = e.AgentId };
    }

    private static string Title(MemoryEntry e) =>
        e.Metadata.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t) ? t : e.Content.Split('\n', 2)[0].Trim();

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "with", "is", "are", "at", "by", "from", "as", "into", "via", "per" };

    private static HashSet<string> Words(string s) =>
        System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}#_.-]{3,}").Select(m => m.Value).Where(w => !Stop.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Same subject: identical normalised title, a title sharing 70% of its words, or one content contained in the other.</summary>
    private static bool SameSubject(MemoryEntry a, MemoryEntry b)
    {
        var ta = Words(Title(a)); var tb = Words(Title(b));
        if (ta.Count > 0 && ta.SetEquals(tb)) return true;
        if (ta.Count >= 3 && tb.Count >= 3)
        {
            int shared = ta.Count(w => tb.Contains(w));
            if (shared >= 0.7 * Math.Min(ta.Count, tb.Count) && shared >= 3) return true;
        }
        var ca = a.Content.Trim(); var cb = b.Content.Trim();
        if (ca.Length >= 40 && cb.Length >= 40)
        {
            var (shortC, longC) = ca.Length <= cb.Length ? (ca, cb) : (cb, ca);
            if (longC.Contains(shortC, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
