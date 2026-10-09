
using System.Text.Json;

namespace VanityStudio.Llm;

/// <summary>
/// LLM provider catalog. Loaded once from appsettings.json (the same "Ai" section
/// already present in Vanity/appsettings.json). Ported verbatim from TheOrchestrator —
/// proven multi-provider config, no Microsoft.Extensions.* deps.
/// </summary>
public sealed class AiOptions
{
    public const string Section = "Ai";
    public string DecisionMode { get; set; } = "llm";
    public List<AiProfile> Profiles { get; set; } = [];

    private static readonly AsyncLocal<AiOptions?> HostOptions = new();
    public static IDisposable UseForCurrentRun(AiOptions options)
    {
        var previous = HostOptions.Value;
        HostOptions.Value = options;
        return new HostScope(() => HostOptions.Value = previous);
    }
    private sealed class HostScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static AiOptions? _cached;
    private static readonly object _loadLock = new();

    public static AiOptions Load()
    {
        if (HostOptions.Value is { } host) return host;
        lock (_loadLock) { return _cached ??= LoadFromDisk(); }
    }

    public static AiOptions Reload()
    {
        lock (_loadLock) { _cached = LoadFromDisk(); return _cached; }
    }

    private static AiOptions LoadFromDisk()
    {
        // The agent's own config.json first; then appsettings.json ("Ai" section) or ai.json next to the exe / in the cwd.
        if (File.Exists(VanityStudio.Infra.AgentConfig.ConfigFile))
            return VanityStudio.Infra.AgentConfig.Load();
        foreach (var searchDir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var appsettings = Path.Combine(searchDir, "appsettings.json");
            if (File.Exists(appsettings))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(appsettings));
                if (doc.RootElement.TryGetProperty("Ai", out var ai))
                {
                    var opts2 = new AiOptions
                    {
                        DecisionMode = ai.TryGetProperty("DecisionMode", out var dm2) ? dm2.GetString() ?? "llm" : "llm",
                    };
                    if (ai.TryGetProperty("Profiles", out var profiles2) && profiles2.ValueKind == JsonValueKind.Array)
                        foreach (var p in profiles2.EnumerateArray())
                            opts2.Profiles.Add(ParseProfile(p));
                    return opts2;
                }
                // appsettings.json exists but has no "Ai" section — fall through to ai.json in same dir
            }

            var aiJson = Path.Combine(searchDir, "ai.json");
            if (File.Exists(aiJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(aiJson));
                var root = doc.RootElement;
                var opts = new AiOptions
                {
                    DecisionMode = root.TryGetProperty("DecisionMode", out var dm) ? dm.GetString() ?? "llm" : "llm",
                };
                if (root.TryGetProperty("Profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                    foreach (var p in profiles.EnumerateArray())
                        opts.Profiles.Add(ParseProfile(p));
                return opts;
            }
        }
        throw new InvalidOperationException(
            $"No AI config found. Expected {VanityStudio.Infra.AgentConfig.ConfigFile} (run vanity-studio and sign in, or /key), or appsettings.json / ai.json in {AppContext.BaseDirectory} or {Directory.GetCurrentDirectory()}");
    }

    public static AiProfile ParseProfile(JsonElement p) => new()
    {
        Name                 = p.TryGetProperty("Name",                 out var n)  ? n.GetString()  ?? "" : "",
        Provider             = p.TryGetProperty("Provider",             out var pr) ? pr.GetString() ?? "" : "",
        Enabled              = !p.TryGetProperty("Enabled",             out var e)  || e.GetBoolean(),
        ApiKeys              = p.TryGetProperty("ApiKeys",              out var k)  && k.ValueKind == JsonValueKind.Array
                                  ? k.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
        Models               = p.TryGetProperty("Models",               out var m)  && m.ValueKind == JsonValueKind.Array
                                  ? m.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
        VisionModel          = p.TryGetProperty("VisionModel",          out var vm) ? vm.GetString() ?? "" : "",
        BaseUrl              = p.TryGetProperty("BaseUrl",              out var b)  ? b.GetString()  ?? "" : "",
        Layers               = p.TryGetProperty("Layers",               out var l)  && l.ValueKind == JsonValueKind.Array
                                  ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
        RequestTimeoutMs     = p.TryGetProperty("RequestTimeoutMs",     out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : 30_000,
        NumCtx               = p.TryGetProperty("NumCtx",               out var nc) && nc.ValueKind == JsonValueKind.Number ? nc.GetInt32() : 0,
        MaxSystemPromptChars = p.TryGetProperty("MaxSystemPromptChars", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt32() : 0,
        DisableThinking      = p.TryGetProperty("DisableThinking",      out var dt) && dt.ValueKind == JsonValueKind.True,
        Temperature          = p.TryGetProperty("Temperature",          out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetDouble() : (double?)null,
        TopP                 = p.TryGetProperty("TopP",                 out var tpp) && tpp.ValueKind == JsonValueKind.Number ? tpp.GetDouble() : (double?)null,
        JsonMode             = p.TryGetProperty("JsonMode",             out var jm) && jm.ValueKind == JsonValueKind.True,
        MaxTokens            = p.TryGetProperty("MaxTokens",            out var mt) && mt.ValueKind == JsonValueKind.Number ? mt.GetInt32() : 0,
        // OAuth fields
        OAuthProvider        = p.TryGetProperty("OAuthProvider",        out var oap) && oap.ValueKind == JsonValueKind.String ? oap.GetString() : null,
        OAuthAccessToken     = p.TryGetProperty("OAuthAccessToken",     out var oat) && oat.ValueKind == JsonValueKind.String ? oat.GetString() : null,
        OAuthRefreshToken    = p.TryGetProperty("OAuthRefreshToken",    out var ort) && ort.ValueKind == JsonValueKind.String ? ort.GetString() : null,
        OAuthExpiresAt       = p.TryGetProperty("OAuthExpiresAt",       out var oex) && oex.ValueKind == JsonValueKind.Number ? oex.GetInt64() : 0,
        OAuthAccountId       = p.TryGetProperty("OAuthAccountId",       out var oai) && oai.ValueKind == JsonValueKind.String ? oai.GetString() : null,
        OAuthClient          = p.TryGetProperty("OAuthClient",          out var ocl) && ocl.ValueKind == JsonValueKind.String ? ocl.GetString() : null,
        RoleModels           = p.TryGetProperty("RoleModels",           out var rms) && rms.ValueKind == JsonValueKind.Object
                                  ? rms.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "") : new(),
        RoleFallbacks        = p.TryGetProperty("RoleFallbacks",        out var rfb) && rfb.ValueKind == JsonValueKind.Object
                                  ? rfb.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ValueKind == JsonValueKind.Array
                                      ? x.Value.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToArray() : [])
                                  : new(),
        RoleTunings          = p.TryGetProperty("RoleTunings",          out var rtu) && rtu.ValueKind == JsonValueKind.Object
                                  ? JsonSerializer.Deserialize<Dictionary<string, RoleTuning>>(rtu.GetRawText(), RoleTuningJson) ?? new() : new(),
    };

    private static readonly JsonSerializerOptions RoleTuningJson = new() { PropertyNameCaseInsensitive = true };
}

public sealed class AiProfile
{
    public string   Name             { get; set; } = "";
    public string   Provider         { get; set; } = "";
    public bool     Enabled          { get; set; } = true;
    public string[] ApiKeys          { get; set; } = [];
    public string[] Models           { get; set; } = [];
    public string   VisionModel      { get; set; } = "";
    public string   BaseUrl          { get; set; } = "";
    public string[] Layers           { get; set; } = [];
    public int      RequestTimeoutMs { get; set; } = 30_000;
    public int      NumCtx           { get; set; }
    public int      MaxSystemPromptChars { get; set; }
    public bool     DisableThinking      { get; set; }
    public double?  Temperature          { get; set; }   // null = provider/legacy default (0); set per profile (e.g. writer 0.9)
    public double?  TopP                 { get; set; }   // null = provider default; nucleus sampling (0..1), sent as top_p
    public bool     JsonMode             { get; set; }    // opt-in: request response_format=json_object (OpenAI-compatible providers, no-tools calls)
    public int      MaxTokens            { get; set; }    // max_tokens cap — required by DeepSeek JSON mode to prevent truncation

    // ── OAuth (subscription auth) — OpenAI Codex / Anthropic setup-token / Grok (xAI) device-code ──
    public string?  OAuthProvider        { get; set; }    // "openai", "anthropic", "grok", or "antigravity" — triggers OAuth instead of API key
    public string?  OAuthAccessToken     { get; set; }    // short-lived bearer token (internal, never shown)
    public string?  OAuthRefreshToken    { get; set; }    // long-lived refresh token (internal, never shown)
    public long     OAuthExpiresAt       { get; set; }    // Unix-epoch seconds when the access token expires
    public string?  OAuthAccountId       { get; set; }    // provider-side account id (display only)
    public string?  OAuthClient          { get; set; }    // Antigravity only: "app" (Antigravity Pro client) | "cli"/null (Gemini CLI free client)

    // ── Per-role model pin (Roles tab) — one profile, a different model per layer ──
    /// <summary>Layer → model. When the router uses this profile for a layer present here (non-empty value), it
    /// sends THAT model instead of iterating <see cref="Models"/>. Empty/absent for a layer ⇒ fall back to
    /// <see cref="Models"/>. Lets a single profile serve e.g. writer=big-model, critic=cheap-model without a clone.</summary>
    public Dictionary<string, string> RoleModels { get; set; } = new();

    /// <summary>The model this profile pins for <paramref name="layer"/>, or null to use <see cref="Models"/>.
    /// Case-insensitive — the JSON round-trip drops any custom dictionary comparer, so match by iteration.</summary>
    public string? ModelForLayer(string layer)
    {
        if (RoleModels is { Count: > 0 })
            foreach (var kv in RoleModels)
                if (!string.IsNullOrWhiteSpace(kv.Value) && kv.Key.Equals(layer, StringComparison.OrdinalIgnoreCase))
                    return kv.Value.Trim();
        return null;
    }

    /// <summary>Layer → the models tried on this profile after its pinned model fails, in order (Roles tab, per-role
    /// card). A role with none listed falls back to <see cref="Models"/>; a role with some tries only those, then the
    /// router moves to the next profile.</summary>
    public Dictionary<string, string[]> RoleFallbacks { get; set; } = new();

    /// <summary>The fallbacks this profile lists for <paramref name="layer"/>, empty when none.</summary>
    public string[] FallbacksForLayer(string layer)
    {
        if (RoleFallbacks is { Count: > 0 })
            foreach (var kv in RoleFallbacks)
                if (kv.Key.Equals(layer, StringComparison.OrdinalIgnoreCase))
                    return (kv.Value ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToArray();
        return [];
    }

    /// <summary>Layer → the tuning that role overrides on this profile (Roles tab, per-role card). A value left null
    /// is the profile's own; so one profile can keep temperature 0 for code and raise it for design or the copilot.</summary>
    public Dictionary<string, RoleTuning> RoleTunings { get; set; } = new();

    /// <summary>This profile as the router uses it for <paramref name="layer"/>: the same object when the role
    /// overrides nothing, else a copy with the role's tuning applied over the profile's. Credentials, models and
    /// everything else are shared with the original, so a refreshed token on it is seen here too.</summary>
    public AiProfile ForLayer(string layer)
    {
        RoleTuning? t = null;
        if (RoleTunings is { Count: > 0 })
            foreach (var kv in RoleTunings)
                if (kv.Key.Equals(layer, StringComparison.OrdinalIgnoreCase)) { t = kv.Value; break; }
        if (t is null || t.IsEmpty) return this;
        var c = (AiProfile)MemberwiseClone();
        if (t.Temperature is { } tp) c.Temperature = tp;
        if (t.TopP is { } pp) c.TopP = pp;
        if (t.MaxTokens is { } mt) c.MaxTokens = mt;
        if (t.RequestTimeoutMs is { } rt) c.RequestTimeoutMs = rt;
        if (t.NumCtx is { } nc) c.NumCtx = nc;
        if (t.MaxSystemPromptChars is { } ms) c.MaxSystemPromptChars = ms;
        if (t.JsonMode is { } jm) c.JsonMode = jm;
        if (t.DisableThinking is { } dt) c.DisableThinking = dt;
        return c;
    }
}

/// <summary>The tuning one role overrides on a profile; every field null means "the profile's value".</summary>
public sealed class RoleTuning
{
    public double? Temperature          { get; set; }
    public double? TopP                 { get; set; }
    public int?    MaxTokens            { get; set; }
    public int?    RequestTimeoutMs     { get; set; }
    public int?    NumCtx               { get; set; }
    public int?    MaxSystemPromptChars { get; set; }
    public bool?   JsonMode             { get; set; }
    public bool?   DisableThinking      { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Temperature is null && TopP is null && MaxTokens is null && RequestTimeoutMs is null
        && NumCtx is null && MaxSystemPromptChars is null && JsonMode is null && DisableThinking is null;
}
