using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace VanityStudio.Llm;

/// <summary>
/// Routes LLM calls to the right profile by layer, with automatic key+model retry,
/// memory of the last-working combination, and a process-wide token usage meter.
/// Ported verbatim from TheOrchestrator (proven multi-provider failover).
///
/// Routing: enabled profiles whose Layers contain the requested layer → try every
/// (key, model) from the last-working cursor → first success wins. Fallback when no
/// profile declares the layer: "orchestrator" → "any" → first enabled → throw.
/// </summary>
public sealed class LlmRouter : ILlmClient
{
    private readonly AiOptions      _opts;
    private readonly LlmCallLogger? _callLogger;
    private readonly long           _userId;   // operator this router serves (0 = Default/global) — for ai_usage logging
    private readonly Dictionary<string, (int KeyIdx, int ModelIdx)> _cursors = new();
    private readonly object _cursorLock = new();
    // Reuse transports across turns. Creating an HttpClient for every tool call leaks handlers/sockets under a
    // long-running project agent. The cache is bounded and the auth token is part of the key so OAuth refreshes
    // receive a client with the new bearer header.
    private readonly Dictionary<string, HttpClient> _httpClients = new(StringComparer.Ordinal);
    private readonly object _httpLock = new();
    private const int MaxHttpClients = 32;

    public LlmRouter(AiOptions opts, LlmCallLogger? callLogger = null, long userId = 0)
    {
        _opts = opts; _callLogger = callLogger; _userId = userId;
    }

    private static LlmRouter? _default;
    private static readonly object _defaultLock = new();

    // Shared call logger for ALL routers (Default + per-user). Held in its OWN static so ResolveFor can build a per-user
    // router WITHOUT reading the `Default` property — critical since Default now redirects INTO ResolveFor (reading
    // Default._callLogger from inside ResolveFor would recurse Default→ResolveFor→Default → StackOverflow).
    private static LlmCallLogger? _sharedCallLogger;
    private static readonly object _loggerLock = new();
    private static LlmCallLogger SharedCallLogger()
    {
        if (_sharedCallLogger is not null) return _sharedCallLogger;
        lock (_loggerLock)
        {
            // Non-project (shared) AI calls: bodies go next to the exe under logs\_shared\prompts\, alongside the
            // per-project trees, so everything diagnostic lives under one logs root.
            return _sharedCallLogger ??= new LlmCallLogger(Path.Combine(VanityStudio.Infra.AgentConfig.LogsDir, "_shared"));
        }
    }

    /// <summary>The same logs/prompts/*.jsonl writer every chat call uses. Exposed so AI work that does NOT go
    /// through this router — image RENDERS, which talk to the image endpoints directly — still lands in the one
    /// file an operator opens to answer "what did the AI just do?". A render that is invisible there reads as a
    /// render that never happened.</summary>
    public static LlmCallLogger CallLogger => SharedCallLogger();

    /// <summary>Resolves the operator whose DB profiles back <see cref="Default"/> — the deployment OWNER (first user).
    /// The composition root sets this so Default routes through the SAME DB profiles the Settings page edits, instead of
    /// raw appsettings.json. When unset (very early startup / tests), Default falls back to appsettings via AiOptions.Load.
    /// This is what makes disabling a provider in Settings actually stop every LlmRouter.Default caller (TaskExecutor,
    /// the content/reddit/facebook/google LLM helpers, …) — appsettings is now ONLY the one-time seed for a fresh user.</summary>
    private static Func<long>? _defaultUserIdProvider;
    public static Func<long>? DefaultUserIdProvider
    {
        get => _defaultUserIdProvider;
        set => _defaultUserIdProvider = value;
    }

    /// <summary>AMBIENT TENANT (AsyncLocal): set once at each tenant entry point - the per-user workflow loop,
    /// a chat host run, a background job, a goal tick. <see cref="Default"/> consults it FIRST, so every legacy
    /// Default caller buried in channel/content helpers rides the CURRENT tenant's profiles: no tenant work ever
    /// burns the owner's keys, and an owner who keeps no profiles no longer breaks tenant runs. An explicit
    /// userId on CallWithLayerAsync still overrides per call. Flows correctly across awaits; 0 = unset.</summary>
    private static readonly AsyncLocal<long> _ambientUserId = new();
    public static long AmbientUserId
    {
        get => _ambientUserId.Value;
        set => _ambientUserId.Value = value;
    }

    private static readonly AsyncLocal<LlmRouter?> HostRouter = new();

    public static IDisposable UseForCurrentRun(LlmRouter router)
    {
        var previous = HostRouter.Value;
        HostRouter.Value = router;
        return new HostScope(() => HostRouter.Value = previous);
    }

    private sealed class HostScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public static LlmRouter Default
    {
        get
        {
            if (HostRouter.Value is { } hostRouter) return hostRouter;
            // FULL TENANT CONTAINMENT (operator directive): every flow carries its tenant - the request
            // middleware, the per-user workflow loops, jobs, chat runs, goal ticks all stamp AmbientUserId.
            // There is NO cross-tenant fallback and NO owner fallback: a call that reaches here without
            // attribution resolves the dead appsettings router and fails with the clear "No enabled AI
            // profiles" message. That is BY DESIGN - an attribution gap must surface as a loud error,
            // never as one tenant's work silently billed to another's keys.
            if (_ambientUserId.Value > 0 && UserOptionsProvider is not null)
                return ResolveFor(_ambientUserId.Value);

            if (_default is not null) return _default;
            lock (_defaultLock)
            {
                if (_default is not null) return _default;
                _default = new LlmRouter(AiOptions.Load(), SharedCallLogger());
                return _default;
            }
        }
    }

    // ── Per-user routing ──────────────────────────────────────────────────────────────────────────
    // AI profiles are per-operator and live in the DB. To route a call with that user's profiles WITHOUT
    // coupling this LLM layer to Vanity.Data, the composition root injects a provider delegate. Each user
    // gets their own LlmRouter instance (so the per-instance key/model cursor isolates automatically).
    // When unset, or userId<=0, calls fall back to Default (appsettings — today's global behavior).
    public static Func<long, AiOptions>? UserOptionsProvider { get; set; }
    private static readonly ConcurrentDictionary<long, LlmRouter> _byUser = new();

    /// <summary>The router for a specific operator (or <see cref="Default"/> when no provider / userId&lt;=0).</summary>
    public static LlmRouter ResolveFor(long userId)
    {
        if (userId <= 0 || UserOptionsProvider is null) return Default;
        // NOTE: SharedCallLogger() — NOT Default._callLogger. Default now redirects into ResolveFor, so reading Default
        // here would recurse into an infinite Default→ResolveFor→Default loop (StackOverflow).
        return _byUser.GetOrAdd(userId, uid => new LlmRouter(UserOptionsProvider!(uid), SharedCallLogger(), uid));
    }

    /// <summary>Drop a user's cached router so the next call rebuilds it from the DB (call after editing
    /// that user's profiles, same process).</summary>
    public static void InvalidateUser(long userId) => _byUser.TryRemove(userId, out _);

    public Task<LlmResponse> CallAsync(string systemPrompt, IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        => CallWithLayerAsync("orchestrator", systemPrompt, history, tools, ct);

    public async Task<LlmResponse> CallWithLayerAsync(string layer, string systemPrompt, IReadOnlyList<ConversationMessage> history, IReadOnlyList<ToolDefinition> tools, CancellationToken ct, long userId = 0)
    {
        // Per-user dispatch: route through the calling operator's own profiles. Guard against recursion —
        // only redirect when the resolved instance is a DIFFERENT one than the current.
        if (userId > 0)
        {
            var forUser = ResolveFor(userId);
            if (!ReferenceEquals(forUser, this))
                return await forUser.CallWithLayerAsync(layer, systemPrompt, history, tools, ct).ConfigureAwait(false);
        }

        // Build the FULL fallback chain of candidate profiles, in priority order, deduped by profile name:
        //   requested layer → "orchestrator" → "any" → every usable profile.
        // We run the execution loop over this WHOLE chain — so if every profile for the requested layer ERRORS
        // (e.g. OpenAI returns 429 insufficient_quota across all keys), we keep going and try the fallback-layer
        // profiles (e.g. DeepSeek) before giving up. A working profile anywhere in the chain wins. (Hard rule: any
        // profile that errors must fall through to the next, ultimately to the fallback layer.)
        var candidates = new List<AiProfile>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddLayer(string lyr)
        {
            foreach (var p in GetCandidates(lyr))
                if (seenNames.Add(p.Name)) candidates.Add(p);
        }
        AddLayer(layer);
        if (!layer.Equals("orchestrator", StringComparison.OrdinalIgnoreCase)) AddLayer("orchestrator");
        AddLayer("any");
        foreach (var p in _opts.Profiles.Where(IsUsable))   // last resort: ANY usable profile, regardless of layer tag
            if (seenNames.Add(p.Name)) candidates.Add(p);
        if (candidates.Count == 0) throw new InvalidOperationException("No enabled AI profiles configured.");

        // The run's own preference (LlmCallScope.PreferredPair): the login that answered its previous step goes first,
        // and on it that step's model comes right after the pin (see runFirst below). It is a preference, not a lock:
        // a failure runs the walk below as usual and the run then prefers whatever answered. Without it a run bounced
        // between logins every time a jail expired (three models in one run, 2026-10-06). It applies on the layer it
        // was earned on only (LlmCallScope.PreferredLayer).
        string? runProfile = null, runModel = null;
        if (LlmCallScope.PreferredPair.Value is { Length: > 0 } pref && pref.IndexOf('|') > 0
            && (LlmCallScope.PreferredLayer.Value is not { } prefLayer || prefLayer.Equals(layer, StringComparison.OrdinalIgnoreCase)))
        {
            runProfile = pref[..pref.IndexOf('|')]; runModel = pref[(pref.IndexOf('|') + 1)..];
            var at = candidates.FindIndex(p => p.Name.Equals(runProfile, StringComparison.OrdinalIgnoreCase));
            if (at > 0) { var p = candidates[at]; candidates.RemoveAt(at); candidates.Insert(0, p); }
            if (at < 0) runProfile = null;
        }

        // If ANY candidate (profile,model) is not currently rate-limited, skip the cooling ones this call; if EVERY
        // option is cooling, ignore cooldowns and try anyway (a real attempt beats a hard fail while everything resets).
        bool anyReady = candidates.Any(p => ModelsFor(p, layer).Any(m => !IsCooling(p.Name, m)));

        Exception? lastEx = null;
        int failures = 0;   // profiles that actually FAILED an attempt (not ones skipped because they're jailed/cooling)
        for (int ci = 0; ci < candidates.Count; ci++)
        {
            var profile = candidates[ci];

            // OAUTH PARITY: subscription (OAuth) profiles ride this same candidate loop as key profiles — the
            // one extra step is keeping their short-lived access token alive. An expired token whose refresh
            // fails counts as this candidate failing (logged, next candidate tried), exactly like a dead key.
            if (!await OAuthTokenRefresher.EnsureFreshAsync(profile, _userId, ct: ct).ConfigureAwait(false))
            {
                Console.WriteLine($"[llm] {profile.Name}: OAuth token expired and refresh failed — trying next");
                lastEx = new InvalidOperationException($"OAuth token expired for profile '{profile.Name}' and refresh failed.");
                failures++;
                continue;
            }

            var (startKey, startModel) = GetCursor(profile.Name);
            var keys   = profile.ApiKeys.Length > 0 ? profile.ApiKeys : [""];
            // Per-role pin: the model this profile fixes for the requested layer goes first, always; the profile's
            // other models follow as its own fallbacks (a stalled or failing pinned model moves to the next model of
            // the SAME login before the next login). Two things end the walk on this login early, because they are
            // the login's and not the model's: a quota hit (429), and a second silent stall in a row (a throttled
            // login hangs every model; the next login answered the same prompt in seconds every time on
            // 2026-10-05, and a 13-model rotation would otherwise cost 13 waits before reaching it). Without a pin
            // the Models list is walked from the last-working cursor.
            var pinned = profile.ModelForLayer(layer);
            var models = ModelsFor(profile, layer);
            // On the run's profile, the pin is still asked first on every step, and the run's model comes right after
            // it, ahead of the role's other fallbacks. A jailed pin is skipped below while its jail lasts, so the run
            // returns to the pin once the jail is over; keeping the fallback instead left a whole run on 3.7 after one
            // 1.7 s 503 on 3.8 (websisco task 15, 2026-10-06). The rest keep their order.
            bool runFirst = runProfile is not null && profile.Name.Equals(runProfile, StringComparison.OrdinalIgnoreCase)
                && runModel is not null && models.Any(m => m.Equals(runModel, StringComparison.OrdinalIgnoreCase));
            if (runFirst)
            {
                var lead = new List<string>();
                if (pinned is not null) lead.AddRange(models.Where(m => m.Equals(pinned, StringComparison.OrdinalIgnoreCase)));
                lead.AddRange(models.Where(m => m.Equals(runModel, StringComparison.OrdinalIgnoreCase) && !lead.Contains(m, StringComparer.OrdinalIgnoreCase)));
                models = lead.Concat(models.Where(m => !lead.Contains(m, StringComparer.OrdinalIgnoreCase))).ToArray();
            }
            for (int ki = 0; ki < keys.Length; ki++)
            {
                var keyIdx = (startKey + ki) % keys.Length;
                bool leaveLogin = false; int stalls = 0;
                for (int mi = 0; mi < models.Length && !leaveLogin; mi++)
                {
                    var modelIdx = pinned is null && !runFirst ? (startModel + mi) % models.Length : mi;
                    var key = keys[keyIdx]; var model = models[modelIdx];
                    if (anyReady && IsCooling(profile.Name, model)) continue;   // known rate-limited until reset - skip, don't re-hammer
                    var attemptClock = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var client = new SingleCallLlmClient(HttpFor(profile, key), profile.ForLayer(layer), key, model, _callLogger);
                        LlmResponse result;
                        try
                        {
                            result = await client.CallAsync(systemPrompt, history, tools, ct);
                        }
                        catch (InvalidOperationException ex) when (ex.Message.StartsWith("HTTP 401") && OAuthTokenRefresher.UsesOAuth(profile))
                        {
                            // Token the server revoked early (or an expiry the stored timestamp missed): force one
                            // refresh and retry the SAME (key, model). The bearer header is fixed in the client's
                            // ctor, so the retry needs a fresh client. A second 401 falls through like any failure.
                            if (!await OAuthTokenRefresher.EnsureFreshAsync(profile, _userId, force: true, ct: ct).ConfigureAwait(false)) throw;
                            client = new SingleCallLlmClient(HttpFor(profile, key), profile.ForLayer(layer), key, model, _callLogger);
                            result = await client.CallAsync(systemPrompt, history, tools, ct);
                        }
                        SaveCursor(profile.Name, keyIdx, pinned is null && !runFirst ? modelIdx : 0);   // a pinned role always starts at its pin
                        _failStreak.TryRemove(profile.Name + "|" + model, out _);   // it works: forget the history
                        // Honest fallback log: report REAL failures separately from profiles we skipped because they're
                        // jailed (rate-limited/cooling). Once a 429'd primary is parked, this flips from "N failed" to
                        // "skipped" - a quick confirmation that the jail is doing its job and not re-hammering the pool.
                        if (failures > 0)
                            Console.WriteLine($"[llm] layer '{layer}': fell back to profile '{profile.Name}' (model '{model}') after {failures} profile(s) failed");
                        else if (ci > 0)
                            Console.WriteLine($"[llm] layer '{layer}': using fallback profile '{profile.Name}' (model '{model}') — {ci} profile(s) skipped (rate-limited/cooling)");
                        // Record REAL token usage + cost for the dashboard (best-effort; never breaks the call).
                        bool hadImage = history.Any(h => !string.IsNullOrEmpty(h.ImageDataUrl));
                        AiUsageLog.TryRecord(_userId, layer, profile.Provider, profile.Name, result.ModelName ?? model,
                            result.PromptTokens ?? 0, result.CompletionTokens ?? 0, result.CachedTokens ?? 0, result.TotalTokens ?? 0, hadImage);
                        return result;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Jail this (profile, model) so the router stops re-hammering it FIRST on every single turn:
                        //  - rate-limited (429 / RESOURCE_EXHAUSTED): park until the provider's own reset time.
                        //  - model unavailable (404 / "model not found" / unsupported): a PERSISTENT mismatch - this
                        //    provider simply doesn't serve this model (e.g. OneProvider 404s the 4.x Claude line), so it
                        //    would fail identically every call. Park it for a fixed window instead of re-failing forever.
                        string pair = profile.Name + "|" + model;
                        int streak = _failStreak.AddOrUpdate(pair, 1, (_, n) => n + 1);
                        if (IsRateLimit(ex)) { _cooldownUntil[pair] = DateTime.UtcNow + ParseResetDelay(ex.Message); leaveLogin = true; }
                        else if (IsModelUnavailable(ex)) _cooldownUntil[pair] = DateTime.UtcNow + TimeSpan.FromMinutes(30);
                        //  - transient gateway/server failure (5xx / upstream timeout): the SAME call just burned up to
                        //    two minutes waiting on a dead gateway; without a jail the next turn re-waits on it FIRST
                        //    before falling back. Park it briefly so following turns start on a healthy candidate,
                        //    while a recovered provider comes back after seconds, not half an hour.
                        // A jail must outlast the failure that earned it. 90s flat against a call that burned 305s in a
                        // timeout meant the pair left jail long before the next turn, was tried FIRST again, and burned
                        // another 305s - 18 of those in 100 minutes, 1.5h of wall clock (2026-09-15). Park a slow
                        // failure for several times what it cost, so the next turn starts on a healthy candidate.
                        else if (IsTransientServerError(ex))
                        {
                            // Fast failure (< 10s) = server immediately rejected — sustained overload or maintenance.
                            // Slow failure (≥ 10s) = gateway timeout — brief hiccup, recover sooner.
                            // Without this inversion, a capacity-exhausted 503 that takes 0.5s gets a 90s jail and
                            // hammers the provider every 90s all through a maintenance window.
                            double elapsed = attemptClock.Elapsed.TotalSeconds;
                            double coolSec = elapsed < SlowFailureSeconds
                                ? 900   // fast rejection → 15 min (maintenance / capacity)
                                : Math.Max(90, Math.Min(1800, elapsed * 3));
                            _cooldownUntil[pair] = DateTime.UtcNow + TimeSpan.FromSeconds(coolSec);
                            if (elapsed >= SlowFailureSeconds && ++stalls >= 2) leaveLogin = true;   // two slow failures in a row: the login hangs, not the model
                        }
                        // Nothing above matched: the provider failed without saying anything the classifiers know.
                        // Those get NO jail at all today, so the same dead pair is tried first on every turn - 93 such
                        // rejects in one day, each ~1s, all on the same two profiles. Behaviour is the evidence when
                        // the message is not: a pair that fails repeatedly with no success between is not transient.
                        else if (streak >= UnexplainedFailuresBeforeJail)
                            _cooldownUntil[pair] = DateTime.UtcNow + TimeSpan.FromMinutes(30);
                        Console.WriteLine($"[llm] {profile.Name} key[{keyIdx}] model '{model}' failed ({streak}x): {ex.Message} — trying next");
                        lastEx = ex;
                        failures++;
                    }
                }
            }
        }
        throw new InvalidOperationException($"All {candidates.Count} LLM profile(s) failed for layer '{layer}' (and all fallbacks). Last error: {lastEx?.Message}", lastEx);
    }

    // ── rate-limit cooldowns ──────────────────────────────────────────────────────────────────────────
    // When a (profile, model) returns 429/RESOURCE_EXHAUSTED, park it until its reset so we stop trying it first
    // every turn. Process-wide + shared across users: a pool's quota is the same whoever hits it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _cooldownUntil = new();
    /// <summary>Consecutive failures per (profile, model), cleared the moment one succeeds. It is the only signal for a
    /// provider that fails without explaining itself - no 429, no 404, no 5xx, just a refusal the classifiers cannot
    /// read. Repetition is then the evidence: three in a row with no success between is a broken pairing, not weather.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _failStreak = new();
    private const int UnexplainedFailuresBeforeJail = 3;
    private static bool IsCooling(string profile, string model) =>
        _cooldownUntil.TryGetValue(profile + "|" + model, out var until) && DateTime.UtcNow < until;
    private static bool IsRateLimit(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Contains("HTTP 429") || m.Contains("RESOURCE_EXHAUSTED") || m.Contains("rateLimitExceeded")
            || m.Contains("insufficient_quota") || m.Contains("Too Many Requests")
            || m.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0           // "Individual quota reached", "quota exceeded"
            || m.IndexOf("credit balance", StringComparison.OrdinalIgnoreCase) >= 0; // Anthropic: HTTP 400 "credit balance is too low"
    }
    // A model this provider does NOT serve - a persistent mismatch (404 or an explicit unknown/unsupported-model
    // message), so re-trying it every call is pure waste. Jailed on a fixed window like a rate-limit, per (profile,model).
    private static bool IsModelUnavailable(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Contains("HTTP 404")
            || m.IndexOf("model unavailable", StringComparison.OrdinalIgnoreCase) >= 0     // the client's own provider-notice failure
            || m.IndexOf("no longer available", StringComparison.OrdinalIgnoreCase) >= 0   // Google's retirement wording
            || m.IndexOf("model not found", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("no such model", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("unsupported model", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("unknown model", StringComparison.OrdinalIgnoreCase) >= 0;
    }
    // A gateway/server hiccup: 5xx from the provider or an upstream timeout surfaced in the error text. One
    // failure predicts the next few SECONDS, not the next hour - jailed on a short window (90s) per (profile,model),
    // unlike the persistent 404/unsupported case above.
    internal static bool IsTransientServerError(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Contains("HTTP 500") || m.Contains("HTTP 502") || m.Contains("HTTP 503") || m.Contains("HTTP 504")
            || m.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("stalled", StringComparison.OrdinalIgnoreCase) >= 0     // a silent request or stream, given up (SilencePolicy)
            || m.IndexOf("bad gateway", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("service unavailable", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("MODEL_CAPACITY_EXHAUSTED", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("OVERLOADED", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("\"UNAVAILABLE\"", StringComparison.OrdinalIgnoreCase) >= 0;
    }
    // Parse a reset hint from the provider error ("resets in 3h21m10s", "resets in 10m", "try again in 45s"). No
    // parseable time -> a short 60s default so we retry soon; capped at 6h so a bogus value can't park a pool forever.
    private static TimeSpan ParseResetDelay(string message)
    {
        var msg = message ?? "";
        // Gap-eater must be GREEDY: with a lazy [^0-9]*? and all number groups optional, the match "succeeds"
        // right after the keyword with zero digits captured, so "Resets in 1h21m9s" parsed as the 60s default
        // and the router re-hammered the exhausted pool every minute. Greedy can't overshoot (digits are
        // excluded from the class, it stops at the first digit); bounded so it can't drift to unrelated numbers.
        var m = System.Text.RegularExpressions.Regex.Match(msg,
            @"(?:resets?|try again|retry|refresh)[^0-9]{0,40}(?:(\d+)\s*h)?\s*(?:(\d+)\s*m(?:in)?)?\s*(?:(\d+)\s*s)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && (m.Groups[1].Success || m.Groups[2].Success || m.Groups[3].Success))
        {
            int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            int mi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            int s = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            var ts = new TimeSpan(h, mi, s);
            if (ts > TimeSpan.Zero) return ts > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : ts + TimeSpan.FromSeconds(5);
        }
        return TimeSpan.FromSeconds(60);
    }

    /// <summary>ADDITIVE: stream a TEXT (no-tools) completion for <paramref name="layer"/>, invoking <paramref name="onDelta"/>
    /// with the accumulated text as it grows; returns the full text. Picks the first usable profile (layer → orchestrator →
    /// any). No multi-profile failover — callers (the answer module) fall back to the blocking path on any error.</summary>
    public async Task<string> CallStreamWithLayerAsync(string layer, string systemPrompt,
        IReadOnlyList<ConversationMessage> history, Action<string> onDelta, CancellationToken ct, long userId = 0)
    {
        if (userId > 0)
        {
            var forUser = ResolveFor(userId);
            if (!ReferenceEquals(forUser, this))
                return await forUser.CallStreamWithLayerAsync(layer, systemPrompt, history, onDelta, ct).ConfigureAwait(false);
        }
        var profile = GetCandidates(layer).FirstOrDefault()
                   ?? GetCandidates("orchestrator").FirstOrDefault()
                   ?? GetCandidates("any").FirstOrDefault()
                   ?? _opts.Profiles.FirstOrDefault(IsUsable)
                   ?? throw new InvalidOperationException("No enabled AI profiles for streaming.");
        // Best-effort OAuth renewal — a token that stays dead just errors below and the caller
        // falls back to the blocking path (which has the full refresh + failover loop).
        await OAuthTokenRefresher.EnsureFreshAsync(profile, _userId, ct: ct).ConfigureAwait(false);
        var (startKey, startModel) = GetCursor(profile.Name);
        var keys   = profile.ApiKeys.Length > 0 ? profile.ApiKeys : [""];
        // Per-role pin (same as the blocking path): a layer-specific model wins over the Models list.
        var models = ModelsFor(profile, layer);
        var model = profile.ModelForLayer(layer) is null ? models[startModel % models.Length] : models[0];
        var client = new SingleCallLlmClient(HttpFor(profile, keys[startKey % keys.Length]), profile.ForLayer(layer), keys[startKey % keys.Length], model, _callLogger);
        var text = await client.CallStreamAsync(systemPrompt, history, onDelta, ct).ConfigureAwait(false);
        // Streamed replies were invisible in ai_usage (TryRecord lived only on the blocking path) - record them too.
        if (client.LastStreamUsage is { } su)
            AiUsageLog.TryRecord(_userId, layer, profile.Provider, profile.Name, model,
                su.Prompt, su.Completion, su.Cached, su.Total, history.Any(h => !string.IsNullOrEmpty(h.ImageDataUrl)));
        return text;
    }

    /// <summary>The models a profile offers for a layer, in the order they are tried: the role's pinned model first,
    /// then the fallbacks the role lists (Roles tab); a role that lists none falls back to the profile's Models list
    /// in order; without a pin, the Models list.</summary>
    internal static string[] ModelsFor(AiProfile p, string layer)
    {
        var list = p.Models.Length > 0 ? p.Models : [""];
        if (p.ModelForLayer(layer) is not { } pinned) return list;
        var own = p.FallbacksForLayer(layer);
        var after = own.Length > 0 ? own : list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pinned };
        return new[] { pinned }.Concat(after.Where(m => m.Length > 0 && seen.Add(m))).ToArray();
    }

    private List<AiProfile> GetCandidates(string layer) =>
        _opts.Profiles.Where(p => IsUsable(p) && p.Layers.Contains(layer, StringComparer.OrdinalIgnoreCase)).ToList();

    private static bool IsUsable(AiProfile p) =>
        p.Enabled && (p.ApiKeys.Length == 0 || p.ApiKeys.Any(k => !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_")));

    private (int KeyIdx, int ModelIdx) GetCursor(string profileName)
    { lock (_cursorLock) return _cursors.TryGetValue(profileName, out var c) ? c : (0, 0); }

    private void SaveCursor(string profileName, int keyIdx, int modelIdx)
    { lock (_cursorLock) _cursors[profileName] = (keyIdx, modelIdx); }

    /// <summary>A failure that took this long or longer waited on the provider (a hang or a gateway timeout); a faster
    /// one was refused outright. Tests lower it to drive the walk without real waits.</summary>
    internal static double SlowFailureSeconds = 10;

    /// <summary>Tests only: every HttpClient the router creates gets this handler instead of the network.</summary>
    internal static Func<HttpMessageHandler>? TestHandler;

    private HttpClient HttpFor(AiProfile profile, string key)
    {
        string cacheKey = string.Join("\u001f", profile.Name, profile.Provider, profile.BaseUrl,
            profile.RequestTimeoutMs.ToString(CultureInfo.InvariantCulture), key ?? "", profile.OAuthAccessToken ?? "",
            profile.OAuthAccountId ?? "");
        lock (_httpLock)
        {
            if (_httpClients.TryGetValue(cacheKey, out var existing)) return existing;
            if (_httpClients.Count >= MaxHttpClients)
            {
                var victim = _httpClients.First();
                _httpClients.Remove(victim.Key);
                try { victim.Value.Dispose(); } catch { }
            }
            var created = TestHandler is { } h ? new HttpClient(h(), disposeHandler: true) : new HttpClient();
            _httpClients[cacheKey] = created;
            return created;
        }
    }

    // ── process-wide usage meter ───────────────────────────────────────────────
    private sealed class ModelUsage { public int Calls; public long PromptTokens, CompletionTokens, TotalTokens; }
    private static readonly object _usageLock = new();
    private static readonly Dictionary<string, ModelUsage> _usageByModel = new();

    internal static void RecordUsage(string model, int prompt, int completion, int total)
    {
        if (string.IsNullOrEmpty(model)) return;
        if (prompt == 0 && completion == 0 && total == 0) return;
        lock (_usageLock)
        {
            if (!_usageByModel.TryGetValue(model, out var slot)) _usageByModel[model] = slot = new ModelUsage();
            slot.Calls++; slot.PromptTokens += prompt; slot.CompletionTokens += completion; slot.TotalTokens += total;
        }
    }

    public static void ResetUsage() { lock (_usageLock) _usageByModel.Clear(); }

    public static (int Calls, long Prompt, long Completion, long Total) GetUsageTotals()
    {
        lock (_usageLock)
        {
            int calls = 0; long p = 0, c = 0, t = 0;
            foreach (var u in _usageByModel.Values) { calls += u.Calls; p += u.PromptTokens; c += u.CompletionTokens; t += u.TotalTokens; }
            return (calls, p, c, t);
        }
    }

    public static string DescribeUsage()
    {
        lock (_usageLock)
        {
            if (_usageByModel.Count == 0) return "  (no LLM calls)";
            var sb = new StringBuilder();
            int totalCalls = 0; long tp = 0, tc = 0, tt = 0;
            foreach (var (model, u) in _usageByModel.OrderByDescending(kv => kv.Value.TotalTokens))
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-32} calls={1,3}  prompt={2,9:N0}  completion={3,8:N0}  total={4,9:N0}",
                    model, u.Calls, u.PromptTokens, u.CompletionTokens, u.TotalTokens));
                totalCalls += u.Calls; tp += u.PromptTokens; tc += u.CompletionTokens; tt += u.TotalTokens;
            }
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "  {0,-32} calls={1,3}  prompt={2,9:N0}  completion={3,8:N0}  total={4,9:N0}", "TOTAL", totalCalls, tp, tc, tt));
            return sb.ToString();
        }
    }

    public string Describe()
    {
        var enabled = _opts.Profiles.Count(IsUsable);
        return $"{enabled}/{_opts.Profiles.Count} profile(s) usable";
    }
}
