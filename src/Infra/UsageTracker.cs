using System.Text.Json;
using System.Text.Json.Serialization;

namespace VanityStudio.Infra;

/// <summary>
/// Persists cumulative LLM token usage to {workspace}/.vanity/usage.json.
/// Format matches VanitySlave: { months: { "YYYY-MM": { prompt, completion, cached, calls, cost_micro } } }
/// Thread-safe.
/// </summary>
public sealed class UsageTracker
{
    private readonly string       _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented               = true,
        Encoder                     = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="path">The usage.json to keep (one per workspace under the agent home).</param>
    public UsageTracker(string path)
    {
        _path = path;
    }

    /// <summary>Model calls recorded this month (0 when nothing was tracked yet).</summary>
    public long Calls
    {
        get
        {
            try { return Load().Months.TryGetValue(DateTime.UtcNow.ToString("yyyy-MM"), out var m) ? m.Calls : 0; }
            catch { return 0; }
        }
    }

    public string Path => _path;

    /// <summary>One month's stored totals, newest month first when <paramref name="month"/> is null.</summary>
    public IReadOnlyList<(string Month, long Prompt, long Completion, long Cached, long Calls, double CostUsd)> Months()
    {
        try
        {
            return Load().Months.OrderByDescending(kv => kv.Key)
                .Select(kv => (kv.Key, kv.Value.Prompt, kv.Value.Completion, kv.Value.Cached, kv.Value.Calls, kv.Value.CostMicro / 1_000_000.0))
                .ToList();
        }
        catch { return []; }
    }

    // Running totals for THIS process, updated synchronously. The file write below is async fire-and-forget, so
    // reading usage.json back cannot give a caller a reliable before/after delta around a unit of work. Every model
    // call in the project funnels through Track, so these two counters are the one place a per-task total can be
    // measured from.
    private long _liveTotal, _liveCached;
    /// <summary>Prompt + completion tokens tracked since this process started.</summary>
    public long LiveTotal => Interlocked.Read(ref _liveTotal);
    /// <summary>Cache-read tokens tracked since this process started (a subset of <see cref="LiveTotal"/>).</summary>
    public long LiveCached => Interlocked.Read(ref _liveCached);

    /// <summary>Record one LLM turn's token counts. Fire-and-forget safe. Pass the model that answered so the month's
    /// estimated cost can be priced; without it the call is still counted, just at the default rate.</summary>
    public void Track(int promptTokens, int completionTokens, int cachedTokens, string? model = null)
    {
        if (promptTokens == 0 && completionTokens == 0) return;
        Interlocked.Add(ref _liveTotal, (long)promptTokens + completionTokens);
        Interlocked.Add(ref _liveCached, cachedTokens);
        _ = TrackAsync(promptTokens, completionTokens, cachedTokens, model);
    }

    // Rough per-million-token prices in USD, by model family - the same table and cache rate the previous agent
    // priced usage.json with, so a project's cost history stays comparable across the change.
    private const double CachedInputRate = 0.1;   // a cache-READ prompt token bills at ~10% of the input price
    private static double PriceIn(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("deepseek")) return 0.27;
        if (m.Contains("haiku") || m.Contains("mini") || m.Contains("flash") || m.Contains("nano")) return 0.15;
        if (m.Contains("opus")) return 15;
        if (m.Contains("claude") || m.Contains("sonnet")) return 3;
        if (m.Contains("gemini")) return 1.25;
        if (m.Contains("gpt") || m.Contains("o3") || m.Contains("o4") || m.Contains("codex")) return 2;
        return 1;
    }
    private static double PriceOut(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("deepseek")) return 1.1;
        if (m.Contains("haiku") || m.Contains("mini") || m.Contains("flash") || m.Contains("nano")) return 0.6;
        if (m.Contains("opus")) return 75;
        if (m.Contains("claude") || m.Contains("sonnet")) return 15;
        if (m.Contains("gemini")) return 10;
        if (m.Contains("gpt") || m.Contains("o3") || m.Contains("o4") || m.Contains("codex")) return 8;
        return 4;
    }

    private async Task TrackAsync(int prompt, int completion, int cached, string? model = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var data = Load();
            var key  = DateTime.UtcNow.ToString("yyyy-MM");

            if (!data.Months.TryGetValue(key, out var month))
                month = new MonthUsage();

            var cacheRead = Math.Max(0, Math.Min(cached, prompt));   // cache hits are a subset of the prompt
            month.Prompt     += prompt;
            month.Completion += completion;
            month.Cached     += cacheRead;
            month.Calls      += 1;
            // Prices are per million tokens, the bucket is in micro-USD: (tokens/1e6) * price * 1e6 == tokens * price.
            month.CostMicro  += (long)((prompt - cacheRead) * PriceIn(model)
                                       + cacheRead * PriceIn(model) * CachedInputRate
                                       + completion * PriceOut(model));

            data.Months[key] = month;

            var text = new System.Text.StringBuilder();
            text.AppendLine("{");
            text.AppendLine("  \"months\": {");
            var months = data.Months.OrderBy(kv => kv.Key).ToList();
            for (int i = 0; i < months.Count; i++)
            {
                var (k, v) = (months[i].Key, months[i].Value);
                var comma = i < months.Count - 1 ? "," : "";
                text.AppendLine($"    \"{k}\": {{");
                text.AppendLine($"      \"prompt\": {v.Prompt},");
                text.AppendLine($"      \"completion\": {v.Completion},");
                text.AppendLine($"      \"cached\": {v.Cached},");
                text.AppendLine($"      \"calls\": {v.Calls},");
                text.AppendLine($"      \"cost_micro\": {v.CostMicro}");
                text.AppendLine($"    }}{comma}");
            }
            text.AppendLine("  }");
            text.Append("}");

            // Write through a temp file and swap: a truncate-then-write interrupted mid-way left an empty usage.json,
            // and the next call parsed that as "no months" and persisted it - the whole cost history gone.
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, text.ToString(),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
            if (File.Exists(_path)) File.Replace(tmp, _path, null); else File.Move(tmp, _path);
        }
        // Usage is bookkeeping and must never fail a model call. A failed READ skips this write entirely (above), so
        // an unreadable file is left alone for the next call rather than being replaced with zeros.
        catch { }
        finally { _gate.Release(); }
    }

    /// <summary>Read the month buckets. Throws when the file exists but cannot be read or parsed, because the caller
    /// writes the result straight back: swallowing the failure and returning empty buckets silently overwrote the
    /// project's whole cost history with zeros on a single transient read error.</summary>
    private UsageData Load()
    {
        if (!File.Exists(_path)) return new();
        var text = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text)) return new();   // never written yet, or truncated by an older build
        return JsonSerializer.Deserialize<UsageData>(text, _json) ?? new();
    }

    private sealed class UsageData
    {
        [JsonPropertyName("months")] public Dictionary<string, MonthUsage> Months { get; set; } = new();
    }

    private sealed class MonthUsage
    {
        [JsonPropertyName("prompt")]     public long Prompt     { get; set; }
        [JsonPropertyName("completion")] public long Completion { get; set; }
        [JsonPropertyName("cached")]     public long Cached     { get; set; }
        [JsonPropertyName("calls")]      public long Calls      { get; set; }
        [JsonPropertyName("cost_micro")] public long CostMicro  { get; set; }
    }
}
