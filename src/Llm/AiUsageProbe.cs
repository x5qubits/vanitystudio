using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


namespace VanityStudio.Llm
{
    /// <summary>
    /// Reads the SUBSCRIPTION usage/limits for an OAuth ("Login via code") AI profile from the provider's own
    /// usage endpoint - the same numbers the provider's app shows (weekly + 5-hour "% remaining"). API-key
    /// profiles have no such endpoint and return null. Never fabricates: any failure returns null so the card
    /// shows "unavailable", never a made-up number.
    ///
    /// Endpoints (reverse-engineered from the providers' own apps):
    ///   OpenAI OAuth (Codex/ChatGPT): GET  https://chatgpt.com/backend-api/wham/usage   (Bearer + ChatGPT-Account-Id)
    ///   Antigravity (Gemini Code Assist): POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary
    ///        with body {} and the Antigravity UA headers - the SAME call the IDE's "Models &amp; Usage" panel shows,
    ///        group by group, bucket by bucket, sentence by sentence (+ :loadCodeAssist for the plan line).
    /// </summary>
    public static class AiUsageProbe
    {
        /// <summary>One line of the provider's panel, verbatim: "Weekly Limit Remaining 68% - You have used some of
        /// your weekly limit, it will fully refresh in 3 days, 9 hours." Nothing is renamed or summarised; the card
        /// prints these in the order the provider sent them (Pavel, 2026-09-07: "must show exactly like this").</summary>
        public sealed class Bucket
        {
            public string id { get; set; } = "";           // "gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h"
            public string label { get; set; } = "";        // provider displayName: "Weekly Limit Remaining"
            public string window { get; set; } = "";       // "weekly" | "5h"
            public double remaining { get; set; }          // 0-100, percent REMAINING
            public string? reset { get; set; }             // ISO 8601
            public string? description { get; set; }       // provider sentence, present only once the bucket was used
        }

        public sealed class Pool
        {
            public string label { get; set; } = "";      // provider group name ("Gemini Models", "Claude and GPT models"), "" for a single pool
            public double? weeklyRemaining { get; set; }  // 0-100, percent REMAINING (null = unknown)
            public double? fiveHourRemaining { get; set; }
            public string? weeklyReset { get; set; }      // ISO 8601 or human hint
            public string? fiveHourReset { get; set; }
            public List<Bucket> buckets { get; set; } = new();   // the provider's own rows, in its order (Antigravity)
        }

        // A prepaid credit balance (oneprovider) rather than a windowed % pool - shown as "$X.XX left".
        public sealed class Balance
        {
            public double remaining { get; set; }
            public double? limit { get; set; }
            public string unit { get; set; } = "";   // "USD", …
            public string? expiresAt { get; set; }
        }

        /// <summary>One model the profile pins for a role, as the provider sees it RIGHT NOW: offered to this account
        /// at all, and its own remaining fraction. The pool percentages above can be healthy while a pinned model is not
        /// offered (verified live 2026-09-07: every gemini-3.8 pin answered 429 "Individual quota reached" on an account
        /// whose model list only goes up to 3.7; the weekly pool said 82%).</summary>
        public sealed class ModelStatus
        {
            public string role { get; set; } = "";
            public string model { get; set; } = "";
            public bool offered { get; set; }
            public double? remaining { get; set; }   // percent, when the provider reports it for this model
            public string? reset { get; set; }
            public string? hint { get; set; }        // e.g. "offered tiered flash: gemini-3.7-flash-tiered"
        }

        public sealed class Usage
        {
            public string provider { get; set; } = "";
            public string? plan { get; set; }
            public List<Pool> pools { get; set; } = new();
            public Balance? balance { get; set; }
            public List<ModelStatus> models { get; set; } = new();
            public string? raw { get; set; }   // provider JSON, only when debug requested - for parser calibration
        }

        /// <summary>Probe one profile. Returns null for API-key profiles or on any failure (never a fake number).</summary>
        public static async Task<Usage?> ProbeAsync(AiProfile p, long userId, bool includeRaw = false, CancellationToken ct = default)
        {
            if (p == null) return null;

            // API-KEY providers that expose a prepaid balance/usage endpoint (no OAuth).
            if (string.Equals(p.Provider, "oneprovider", StringComparison.OrdinalIgnoreCase))
            {
                var apiKey = FirstRealKey(p);
                if (string.IsNullOrWhiteSpace(apiKey)) return null;
                try { return await ProbeOneProviderAsync(p, apiKey!, includeRaw, ct).ConfigureAwait(false); }
                catch { return null; }
            }

            // DeepSeek exposes a prepaid balance to API keys: GET https://api.deepseek.com/user/balance.
            if (string.Equals(p.Provider, "deepseek", StringComparison.OrdinalIgnoreCase))
            {
                var apiKey = FirstRealKey(p);
                if (string.IsNullOrWhiteSpace(apiKey)) return null;
                try { return await ProbeDeepSeekAsync(p, apiKey!, includeRaw, ct).ConfigureAwait(false); }
                catch { return null; }
            }

            // OAUTH ("Login via code") providers with a subscription usage API.
            if (!OAuthTokenRefresher.UsesOAuth(p)) return null;
            // Keep the token alive exactly like the router does before a real call - refresh in place only when it is
            // actually expiring, adopting any rotation a concurrent caller persisted (Reload) and writing the fresh
            // token back (Persist), both wired at the composition root. Without this a RARELY-USED OAuth profile (few
            // roles, so real calls seldom refresh it) carries a stale access token and every usage read 401s - which
            // showed as "no usage API" next to an identical profile that IS in heavy rotation, until the operator
            // clicked "fetch models" (a real call) to refresh it by hand. Best-effort: EnsureFreshAsync refreshes at
            // most once per expiry (not per page load) and on genuine failure just leaves the old token, so the read
            // 401s and the card shows "unavailable" - never a fabricated number.
            try { await OAuthTokenRefresher.EnsureFreshAsync(p, userId, ct: ct).ConfigureAwait(false); } catch { }
            var token = p.OAuthAccessToken;
            if (string.IsNullOrWhiteSpace(token)) return null;

            var provider = (p.OAuthProvider ?? "").ToLowerInvariant();
            try
            {
                if (provider == "openai")      return await ProbeOpenAiAsync(p, token!, includeRaw, ct).ConfigureAwait(false);
                if (provider == "antigravity") return await ProbeAntigravityAsync(p, token!, includeRaw, ct).ConfigureAwait(false);
            }
            catch { /* fall through to null - never fabricate */ }
            return null;
        }

        private static string? FirstRealKey(AiProfile p)
        {
            foreach (var k in p.ApiKeys ?? System.Array.Empty<string>())
            {
                var t = (k ?? "").Trim().Trim('\'', '"');
                if (t.Length > 0 && !t.Equals("oauth", StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        // ── OneProvider (prepaid OpenAI-compatible gateway) ───────────────────────────────────────────────
        // Verified live: GET {base}/v1/usage -> { quota: { remaining, limit, unit }, expires_at, isValid }.
        /// <summary>Why a provider shows no remaining credits: most API-key providers simply do not expose a balance
        /// endpoint. Said in one line on the card instead of the generic "usage is not reported for API keys".</summary>
        public static string CreditsNote(string? provider)
        {
            var v = (provider ?? "").Trim().ToLowerInvariant();
            if (v.Contains("openai")) return "OpenAI does not expose remaining credits to API keys - see platform.openai.com/usage";
            if (v.Contains("anthropic") || v.Contains("claude")) return "Anthropic does not expose remaining credits to API keys - see console.anthropic.com";
            if (v.Contains("gemini") || v.Contains("google")) return "Google AI Studio keys have no balance API - see aistudio.google.com";
            if (v.Contains("alibaba") || v.Contains("dashscope") || v.Contains("qwen")) return "Alibaba Model Studio does not expose remaining credits via API - see the Model Studio console";
            if (v.Contains("xai") || v.Contains("grok")) return "xAI does not expose remaining credits via API - see console.x.ai";
            if (v.Contains("deepseek")) return "DeepSeek balance could not be read (GET /user/balance)";
            if (v.Contains("mistral")) return "Mistral does not expose remaining credits via API";
            if (v.Length == 0) return "no provider";
            return provider + " has no usage or balance API";
        }

        // DeepSeek (prepaid, API key): GET https://api.deepseek.com/user/balance -> { is_available, balance_infos:
        // [ { currency, total_balance, granted_balance, topped_up_balance } ] } (documented; amounts are strings).
        private static async Task<Usage?> ProbeDeepSeekAsync(AiProfile p, string key, bool includeRaw, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var baseUrl = string.IsNullOrWhiteSpace(p.BaseUrl) ? "https://api.deepseek.com" : p.BaseUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^3];
            using var resp = await http.GetAsync(baseUrl + "/user/balance", ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return includeRaw ? new Usage { provider = "deepseek", raw = "HTTP " + (int)resp.StatusCode + ": " + Trim(raw) } : null;
            var u = ParseDeepSeekBalance(raw);
            if (u is null) return includeRaw ? new Usage { provider = "deepseek", raw = Trim(raw) } : null;
            if (includeRaw) u.raw = Trim(raw);
            return u;
        }

        public static Usage? ParseDeepSeekBalance(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array) return null;
                foreach (var b in infos.EnumerateArray())
                {
                    var total = TryStr(b, "total_balance");
                    if (total is null || !double.TryParse(total, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount)) continue;
                    return new Usage
                    {
                        provider = "deepseek",
                        plan = root.TryGetProperty("is_available", out var av) && av.ValueKind == JsonValueKind.False ? "balance exhausted" : null,
                        balance = new Balance { remaining = amount, unit = TryStr(b, "currency") ?? "" },
                    };
                }
            }
            catch { }
            return null;
        }

        /// <summary>loadCodeAssist -> currentTier { id, name }: the tier THIS login actually gets. Verified live 2026-09-07:
        /// the engine's Antigravity login for dragon.network is "free-tier" while the IDE on the same Google account shows
        /// a Google AI Pro plan - so the pools and refusals the card shows are the free tier's, not the plan's.</summary>
        public static string? ParseTier(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                // The SUBSCRIPTION is `paidTier` ({id:"g1-pro-tier", name:"Google AI Pro", availableCredits:[{creditType:
                // GOOGLE_ONE_AI}]}) - verified live 2026-09-07 on the same Gemini CLI client tokens that report
                // currentTier free-tier. currentTier is only the Code Assist product tier; the plan is the paid tier.
                // (OpenClaw's removed Antigravity plugin read currentTier/planType/availablePromptCredits - it never
                // reached paidTier, which is why it showed nothing for Pro accounts either.)
                // The panel prints "Your Plan: Google AI Pro" - the name alone. availableCredits only names a credit
                // TYPE (GOOGLE_ONE_AI), never an amount, so it is not printed next to it.
                if (root.TryGetProperty("paidTier", out var pt) && pt.ValueKind == JsonValueKind.Object)
                {
                    var pname = TryStr(pt, "name") ?? TryStr(pt, "id") ?? "";
                    if (pname.Length > 0) return pname;
                }
                if (!root.TryGetProperty("currentTier", out var t) || t.ValueKind != JsonValueKind.Object) return null;
                var id = TryStr(t, "id") ?? ""; var name = TryStr(t, "name") ?? "";
                if (id.Length == 0 && name.Length == 0) return null;
                return id.Length > 0 ? id + (name.Length > 0 && !name.Equals(id, StringComparison.OrdinalIgnoreCase) ? " · " + name : "") : name;
            }
            catch { return null; }
        }

        private static async Task<Usage?> ProbeOneProviderAsync(AiProfile p, string key, bool includeRaw, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var baseUrl = string.IsNullOrWhiteSpace(p.BaseUrl) ? "https://api.oneprovider.dev" : p.BaseUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^3];   // avoid /v1/v1/usage
            using var resp = await http.GetAsync(baseUrl + "/v1/usage", ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return includeRaw ? new Usage { provider = "oneprovider", raw = "HTTP " + (int)resp.StatusCode + ": " + Trim(raw) } : null;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("quota", out var q) || q.ValueKind != JsonValueKind.Object
                || !q.TryGetProperty("remaining", out var rem) || rem.ValueKind != JsonValueKind.Number)
                return includeRaw ? new Usage { provider = "oneprovider", raw = Trim(raw) } : null;
            var bal = new Balance
            {
                remaining = rem.GetDouble(),
                limit = q.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number ? lim.GetDouble() : (double?)null,
                unit = q.TryGetProperty("unit", out var un) && un.ValueKind == JsonValueKind.String ? un.GetString() ?? "" : "",
                expiresAt = TryStr(root, "expires_at"),
            };
            var u = new Usage { provider = "oneprovider", balance = bal };
            if (includeRaw) u.raw = Trim(raw);
            return u;
        }

        // ── OpenAI (Codex / ChatGPT subscription) ─────────────────────────────────────────────────────────
        private static async Task<Usage?> ProbeOpenAiAsync(AiProfile p, string token, bool includeRaw, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "vanity-usage/1.0");
            if (!string.IsNullOrWhiteSpace(p.OAuthAccountId))
                http.DefaultRequestHeaders.Add("ChatGPT-Account-Id", p.OAuthAccountId);

            using var resp = await http.GetAsync("https://chatgpt.com/backend-api/wham/usage", ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return includeRaw ? new Usage { provider = "openai", raw = "HTTP " + (int)resp.StatusCode + ": " + Trim(raw) } : null;

            // Verified live shape: { plan_type, rate_limit: { primary_window, secondary_window } } where each window is
            // { used_percent, limit_window_seconds (604800=weekly, 18000=5h), reset_at (unix) }. A window can be null.
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("rate_limit", out var rl) || rl.ValueKind != JsonValueKind.Object)
                return includeRaw ? new Usage { provider = "openai", raw = Trim(raw) } : null;
            var pool = new Pool();
            foreach (var wk in new[] { "primary_window", "secondary_window" })
            {
                if (!rl.TryGetProperty(wk, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("used_percent", out var up) || up.ValueKind != JsonValueKind.Number) continue;
                var rem = Clamp(100.0 - up.GetDouble());
                long secs = w.TryGetProperty("limit_window_seconds", out var lw) && lw.ValueKind == JsonValueKind.Number ? lw.GetInt64() : 0;
                string? reset = w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number ? UnixToIso(ra.GetInt64()) : null;
                if (secs >= 200000) { pool.weeklyRemaining = rem; pool.weeklyReset = reset; }   // ~7d
                else                { pool.fiveHourRemaining = rem; pool.fiveHourReset = reset; }
            }
            if (pool.weeklyRemaining is null && pool.fiveHourRemaining is null)
                return includeRaw ? new Usage { provider = "openai", raw = Trim(raw) } : null;
            var u = new Usage { provider = "openai", plan = TryStr(root, "plan_type") };
            u.pools.Add(pool);
            if (includeRaw) u.raw = Trim(raw);
            return u;
        }

        // ── Antigravity (Gemini Code Assist) ──────────────────────────────────────────────────────────────
        private static async Task<Usage?> ProbeAntigravityAsync(AiProfile p, string token, bool includeRaw, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // Same UA gate the completion path uses - Cloud Code checks it for the Antigravity tier.
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SingleCallLlmClient.AntigravityUserAgent);
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Goog-Api-Client", "gl-node/22.21.1");

            // retrieveUserQuotaSummary with an EMPTY body {} returns the SAME rows the Antigravity "Models & Usage" panel
            // shows - groups[] { displayName, buckets[] { displayName, window, remainingFraction, resetTime, description } }.
            // HOST ORDER MATTERS: the two Cloud Code hosts keep DIFFERENT pools for the same login. Verified live
            // 2026-09-07 11:06Z on games.shark.com: daily-cloudcode-pa said Gemini weekly 68% (reset 09-10 20:42Z, the
            // panel's "3 days, 9 hours") and 5h 94%; cloudcode-pa said 95% / 100% with other reset clocks. The panel
            // and the completion client (SingleCallLlmClient.CloudCodeBaseUrls) both ride daily-cloudcode-pa first, so
            // the summary is read from the SAME host the calls consume - reading cloudcode-pa first showed a pool
            // nobody was using (the card said 95% while the panel said 68%).
            string? raw = null; string hostUsed = SingleCallLlmClient.CloudCodeBaseUrls[0];
            foreach (var baseUrl in SingleCallLlmClient.CloudCodeBaseUrls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1internal:retrieveUserQuotaSummary")
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                    using var r = await http.SendAsync(req, ct).ConfigureAwait(false);
                    var body = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (r.IsSuccessStatusCode) { raw = body; hostUsed = baseUrl; break; }
                    VanityStudio.Infra.Log.Warn($"[usage] {baseUrl} retrieveUserQuotaSummary -> HTTP {(int)r.StatusCode}: {Trim(body)[..Math.Min(600, Trim(body).Length)]}");
                    if (includeRaw && raw is null) raw = "HTTP " + (int)r.StatusCode + ": " + Trim(body);
                }
                catch (Exception ex) { VanityStudio.Infra.Log.Warn("[usage] " + baseUrl + ": " + ex.Message); }
            }
            if (raw is null) return null;
            var usage = ParseAntigravityQuota(raw, includeRaw);
            if (usage is null) return null;
            // The plan this login has ("Your Plan: Google AI Pro" on the panel = paidTier), read from the same host.
            try
            {
                using var treq = new HttpRequestMessage(HttpMethod.Post, $"{hostUsed}/v1internal:loadCodeAssist")
                { Content = new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}", Encoding.UTF8, "application/json") };
                using var tr = await http.SendAsync(treq, ct).ConfigureAwait(false);
                if (tr.IsSuccessStatusCode) usage.plan = ParseTier(await tr.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            catch { }
            // NOT read: fetchAvailableModels. Its catalog is not entitlement and its per-model quotaInfo is not usage -
            // on 2026-09-07 gemini-3.8-flash-tiered answered an 839k-token call while absent from the catalog, and every
            // per-model bucket reads 100% with a reset five hours from whenever it is asked. Showing "0% / not offered"
            // from it was wrong. ParseAvailableModels stays available for ?debug=1 calibration only.
            return usage;
        }

        /// <summary>Parse fetchAvailableModels for the profile's pinned role models: offered or not (absent from
        /// `models`, or disabled), the model's own remaining fraction and reset, and a hint naming the tiered models the
        /// account IS offered (`tieredModelIds`) when a pin is missing.</summary>
        public static List<ModelStatus> ParseAvailableModels(string raw, IDictionary<string, string>? pins)
        {
            var list = new List<ModelStatus>();
            if (pins == null || pins.Count == 0 || string.IsNullOrWhiteSpace(raw)) return list;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                bool hasModels = root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object;
                string tiered = "";
                if (root.TryGetProperty("tieredModelIds", out var tiers) && tiers.ValueKind == JsonValueKind.Object)
                    foreach (var t in tiers.EnumerateObject())
                        if (t.Value.ValueKind == JsonValueKind.Array)
                            foreach (var m in t.Value.EnumerateArray())
                                if (m.ValueKind == JsonValueKind.String)
                                    tiered += (tiered.Length > 0 ? ", " : "") + t.Name + ": " + m.GetString();
                foreach (var kv in pins)
                {
                    var ms = new ModelStatus { role = kv.Key ?? "", model = kv.Value ?? "" };
                    if (hasModels && ms.model.Length > 0 && models.TryGetProperty(ms.model, out var md) && md.ValueKind == JsonValueKind.Object)
                    {
                        ms.offered = true;
                        if (md.TryGetProperty("quotaInfo", out var qi) && qi.ValueKind == JsonValueKind.Object)
                        {
                            ms.remaining = Clamp((qi.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number ? rf.GetDouble() : 0.0) * 100.0);
                            ms.reset = TryStr(qi, "resetTime");
                        }
                        if (md.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True) { ms.offered = false; ms.hint = "disabled by the provider"; }
                    }
                    else
                    {
                        ms.offered = false;
                        ms.hint = "not offered to this account" + (tiered.Length > 0 ? " (tiered models offered - " + tiered + ")" : "");
                    }
                    list.Add(ms);
                }
            }
            catch { }
            return list;
        }

        /// <summary>Parse retrieveUserQuotaSummary into the SAME rows the Antigravity "Models &amp; Usage" panel draws
        /// (live shape verified 2026-09-07: groups[] { displayName "Gemini Models" | "Claude and GPT models",
        /// buckets[] { bucketId, displayName "Weekly Limit Remaining" | "Five Hour Limit Remaining", window "weekly" |
        /// "5h", remainingFraction, resetTime, description "You have used some of your weekly limit, it will fully
        /// refresh in 2 days, 2 hours." (only once used) } }). Every bucket is kept verbatim in `buckets`, in the
        /// provider's order, with its own label and sentence - the card prints them, it does not summarise them.
        /// Two rules from 2026-09-06 stay: proto3 JSON OMITS a remainingFraction of 0, so a bucket WITHOUT the field is
        /// 0%, not unknown; and the legacy weekly/fiveHour numbers are the MINIMUM per window (the binding limit) for
        /// callers that still read them.</summary>
        public static Usage? ParseAntigravityQuota(string raw, bool includeRaw)
        {
            var u = new Usage { provider = "antigravity" };
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                    {
                        var pool = new Pool { label = ((g.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : "") ?? "").Trim() };
                        if (g.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
                            foreach (var b in buckets.EnumerateArray())
                            {
                                if (b.ValueKind != JsonValueKind.Object) continue;
                                var win = (TryStr(b, "window") ?? "").ToLowerInvariant();
                                if (win.Length == 0) continue;
                                double frac = b.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number ? rf.GetDouble() : 0.0;
                                var pct = Clamp(frac * 100.0);
                                var reset = TryStr(b, "resetTime");
                                bool weekly = win.Contains("week"), fiveHour = win.Contains("5") || win.Contains("five");
                                if (weekly && (pool.weeklyRemaining is null || pct < pool.weeklyRemaining)) { pool.weeklyRemaining = pct; pool.weeklyReset = reset; }
                                else if (!weekly && fiveHour && (pool.fiveHourRemaining is null || pct < pool.fiveHourRemaining)) { pool.fiveHourRemaining = pct; pool.fiveHourReset = reset; }
                                // The panel row, verbatim. A label is only derived when the provider sent none.
                                var label = (TryStr(b, "displayName") ?? "").Trim();
                                if (label.Length == 0) label = weekly ? "Weekly Limit Remaining" : fiveHour ? "Five Hour Limit Remaining" : win;
                                pool.buckets.Add(new Bucket
                                {
                                    id = TryStr(b, "bucketId") ?? "",
                                    label = label,
                                    window = weekly ? "weekly" : fiveHour ? "5h" : win,
                                    remaining = pct,
                                    reset = reset,
                                    description = (TryStr(b, "description") ?? "").Trim() is { Length: > 0 } ds ? ds : null,
                                });
                            }
                        if (pool.buckets.Count > 0) u.pools.Add(pool);
                    }
            }
            catch { }
            if (u.pools.Count == 0) return includeRaw ? new Usage { provider = "antigravity", raw = Trim(raw) } : null;
            if (includeRaw) u.raw = Trim(raw);
            return u;
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
        private static double Clamp(double d) => Math.Round(Math.Max(0, Math.Min(100, d)), 0);
        private static string? TryStr(JsonElement parent, string name)
            => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static string Trim(string s) => s.Length > 4000 ? s.Substring(0, 4000) + "…" : s;
        private static string? UnixToIso(long unixSeconds)
            => unixSeconds <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }
}
