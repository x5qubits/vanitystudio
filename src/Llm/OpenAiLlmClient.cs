using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VanityStudio.Llm;

/// <summary>
/// Stateless single OpenAI-compatible chat completion (one key + model). Used by LlmRouter.
/// Supports OpenAI, DeepSeek, Grok (xAI), Ollama, Mistral, Cohere, Perplexity, Gemini, Antigravity.
/// Antigravity uses the same Gemini API backend (generativelanguage.googleapis.com) via OpenAI-compatible
/// chat completions, authenticated with Google OAuth Bearer tokens.
/// OAuth: OpenAI (Codex backend), Grok (xAI device-code via Platform API), Anthropic, Antigravity (Google OAuth).
/// Ported verbatim from TheOrchestrator.
/// </summary>
public sealed class SingleCallLlmClient
{
    private readonly HttpClient     _http;
    private readonly string         _model;
    private readonly string         _chatPath;
    private readonly string         _baseUrl;     // kept for building absolute URLs (Cloud Code paths contain colons)
    private readonly AiProfile      _profile;
    private readonly LlmCallLogger? _logger;
    private readonly SilencePolicy  _silence;
    /// <summary>True when this client talks to the Codex Responses backend (subscription token) instead of
    /// the Platform API. Decided once in the ctor: the endpoint, the auth header and the request body shape
    /// all have to agree, so it must never be re-derived from the profile's fields somewhere further down.</summary>
    private readonly bool           _isCodex;
    /// <summary>True when this client talks to the Google Cloud Code API (daily-cloudcode-pa.googleapis.com)
    /// authenticated with Google OAuth. Antigravity subscriptions route through Cloud Code, not the
    /// standard Gemini API — the Gemini endpoint rejects OAuth tokens even with cloud-platform scope.</summary>
    private readonly bool           _isAntigravityOAuth;
    /// <summary>True when this client talks to the Anthropic Messages API (provider "anthropic"): its own request
    /// and response shapes, an x-api-key header for a key, a bearer header for a token.</summary>
    private readonly bool           _isAnthropic;

    /// <summary>The Antigravity language server's own User-Agent, captured from it on 2026-09-15. The shape is
    /// <c>ua_name/subclient_type/ide_version (aidev_client; os_type=..; arch=..; cl=..)</c>, built from its spawn
    /// flags --override_user_agent_name / --subclient_type / --override_ide_version; `cl` is the build changelist its
    /// -stamp prints. Version and cl pin the client we present as; os_type and arch follow the machine we run on, the
    /// way the Go client fills them. Update the version and cl together when the installed IDE moves.</summary>
    internal static readonly string AntigravityUserAgent =
        "antigravity/hub/2.13.0 (aidev_client; os_type=" + GoOsName + "; arch=" + GoArchName + "; cl=979372352)";

    private static string GoOsName =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "windows"
        : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "darwin"
        : "linux";

    private static string GoArchName
    {
        get
        {
            switch (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)
            {
                case System.Runtime.InteropServices.Architecture.Arm64: return "arm64";
                case System.Runtime.InteropServices.Architecture.X86:   return "386";
                default:                                                return "amd64";
            }
        }
    }
    /// <summary>Cached Cloud Code project ID (discovered via loadCodeAssist, memoized).</summary>
    private string?                 _projectId;
    /// <summary>Cross-call project cache keyed by ACCOUNT (stable across token refreshes) — a new client is built
    /// per LLM call, so without this the loadCodeAssist/onboardUser bootstrap would re-run on every message.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _antigravityProjectByAccount = new();
    /// <summary>Session ID for Cloud Code requests (stable per client instance).</summary>
    private readonly string         _sessionId = Guid.NewGuid().ToString("N");
    /// <summary>Cloud Code base URLs to try (mirrors OmniRoute's ANTIGRAVITY_RUNTIME_BASE_URLS).</summary>
    public static readonly string[] CloudCodeBaseUrls = [
        "https://daily-cloudcode-pa.googleapis.com",
        "https://cloudcode-pa.googleapis.com",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private static readonly Regex[] ContextOverflowPatterns =
    [
        new("prompt is too long",                       RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("input is too long for requested model",    RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("exceeds the context window",               RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("input token count.*exceeds the maximum",   RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("maximum prompt length is \\d+",            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("reduce the length of the messages",        RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("maximum context length is \\d+ tokens",    RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("exceeds the available context size",       RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("greater than the context length",          RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("context[_ ]length[_ ]exceeded",            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("too many tokens",                          RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("token limit exceeded",                     RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>Is this "key" the placeholder that means *authenticate with the subscription token*, rather
    /// than a real credential? Blank counts. The UI writes it quoted ('oauth') in places, so trim quotes.</summary>
    private static bool IsOAuthSentinel(string? key)
    {
        var k = (key ?? "").Trim().Trim('\'', '"');
        return k.Length == 0 || k.Equals("oauth", StringComparison.OrdinalIgnoreCase);
    }

    public SingleCallLlmClient(HttpClient http, AiProfile profile, string key, string model, LlmCallLogger? logger = null, SilencePolicy? silence = null)
    {
        _model = model; _profile = profile; _logger = logger; _silence = silence ?? SilencePolicy.Shared;

        // A REAL API KEY ALWAYS WINS over a stored OAuth token. A profile is one thing or the other: the
        // 'oauth' sentinel in ApiKeys is how a profile declares "authenticate me with the subscription
        // token"; anything else is a genuine key and must be used as one.
        //
        // This used to be "token beats key, always", which meant a token landing on a key-based profile
        // silently rerouted a paid pay-as-you-go key onto the ChatGPT subscription — different endpoint,
        // different billing, and the key never sent. The UI then reported it as "Authenticated", so the
        // profile looked healthy while its credential was ignored. Key present ⇒ Platform API, full stop.
        var usesOAuth = !string.IsNullOrWhiteSpace(profile.OAuthAccessToken) && IsOAuthSentinel(key);

        // OpenAI OAuth profiles route through the Codex Responses backend, not the Platform API.
        // Grok OAuth profiles use the SAME api.x.ai Platform API — only the auth header differs.
        // Antigravity OAuth uses the Google Cloud Code API (daily-cloudcode-pa.googleapis.com)
        // because the standard Gemini API rejects OAuth tokens — Cloud Code is what the
        // Antigravity desktop app and OmniRoute both use.
        // Decided ONCE here and kept — the endpoint and the request BODY shape must agree.
        var isCodexOAuth = string.Equals(profile.OAuthProvider, "openai", StringComparison.OrdinalIgnoreCase) && usesOAuth;
        _isCodex = isCodexOAuth;
        // Cloud Code REST API is disabled for this project — fall back to Gemini native API.
        // Note: requires a Google Cloud project with Generative Language API enabled.
        _isAntigravityOAuth = string.Equals(profile.Provider, "antigravity", StringComparison.OrdinalIgnoreCase) && usesOAuth;
        _isAnthropic = string.Equals(profile.Provider, "anthropic", StringComparison.OrdinalIgnoreCase);
        _projectId = null;  // discovered lazily via loadCodeAssist on first call
        var baseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? (isCodexOAuth ? "https://chatgpt.com/backend-api/codex"
                : _isAntigravityOAuth ? "https://daily-cloudcode-pa.googleapis.com"   // Google Cloud Code (Antigravity tier)
                : ProviderBaseUrl(profile.Provider))
            : profile.BaseUrl.TrimEnd('/');
        _baseUrl = baseUrl;
        // Antigravity routes through Cloud Code streamGenerateContent (absolute URLs built in AntigravityCallAsync).
        _chatPath = isCodexOAuth ? "responses"
            : _isAntigravityOAuth ? "v1internal:streamGenerateContent"
            : ProviderChatPath(profile.Provider);
        // A user-typed Base URL ending in "/v1" (e.g. https://api.oneprovider.dev/v1) would double against the
        // "v1/chat/completions" path -> ".../v1/v1/..." (HTTP 404). Drop the redundant segment so both the "base"
        // and "base/v1" forms an operator might paste work identically. (Fixes the oneprovider 404 on the slave.)
        if (_chatPath.StartsWith("v1/", StringComparison.Ordinal) && baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        { baseUrl = baseUrl[..^3]; _baseUrl = baseUrl; }
        // OAUTH profiles authenticate with the token the sign-in flow stored on the profile, NOT with ApiKeys[0].
        // OpenAI OAuth: must have a token or throw; Grok OAuth: same.
        if (usesOAuth && string.IsNullOrWhiteSpace(profile.OAuthAccessToken))
        {
            var provName = profile.OAuthProvider ?? "OAuth";
            throw new InvalidOperationException(
                $"{provName} OAuth profile '{profile.Name}' is missing its access token; reconnect it in AI Profiles or replace it with a real API key.");
        }

        var bearer = usesOAuth ? profile.OAuthAccessToken! : key;

        // HttpClient is CACHED per (profile+key) by LlmRouter.HttpFor; the same instance is reused for every call.
        // .NET throws "Properties can only be modified before sending the first request" if we mutate BaseAddress /
        // Timeout / DefaultRequestHeaders after the first send, so configure them ONCE - the cache key includes
        // baseUrl/timeout/bearer, so any change to those already returns a fresh HttpClient.
        if (http.BaseAddress is null)
        {
            http.BaseAddress = new Uri(baseUrl + "/");
            // Fixed 600s ceiling regardless of profile.RequestTimeoutMs: a short per-profile timeout (60s)
            // killed long coding-layer generations, and the resulting TaskCanceledException reads as an
            // OperationCanceledException upstream - failing the TODO and disabling the brief instead of retrying.
            http.Timeout = TimeSpan.FromSeconds(600);
            if (_isAnthropic)
            {
                // Messages API: a key goes in x-api-key; a bearer token in Authorization with the oauth beta flag.
                http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                if (usesOAuth)
                {
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    http.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
                }
                else if (!string.IsNullOrWhiteSpace(bearer))
                    http.DefaultRequestHeaders.Add("x-api-key", bearer);
            }
            else if (!string.IsNullOrWhiteSpace(bearer))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            // Cloud Code gates the free Antigravity tier on the client's User-Agent. These are the headers the real
            // Antigravity language server puts on the wire, captured from it directly on 2026-09-15 by running it with
            // its own --cloud_code_endpoint pointed at a local listener: User-Agent, Authorization, Content-Type and
            // Accept-Encoding, and NOTHING else. The X-Goog-Api-Client we used to send is not one of them, and the UA
            // we carried ("antigravity/2.6.0 darwin/arm64 google-api-nodejs-client/10.3.0") claimed a Node client on
            // macOS seven minor versions behind the installed 2.13.0.
            if (_isAntigravityOAuth)
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
            // Codex Responses backend requires the ChatGPT-Account-Id header for OpenAI OAuth profiles.
            // Grok OAuth does NOT need any extra headers - just the Bearer token.
            if (isCodexOAuth && !string.IsNullOrWhiteSpace(profile.OAuthAccountId))
                http.DefaultRequestHeaders.Add("ChatGPT-Account-Id", profile.OAuthAccountId);
        }

        _http = http;
    }

    public async Task<LlmResponse> CallAsync(
        string systemPrompt, IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        if (_profile.MaxSystemPromptChars > 0 && systemPrompt.Length > _profile.MaxSystemPromptChars)
            systemPrompt = systemPrompt[.._profile.MaxSystemPromptChars];

        var modelForCall = _model;
        // Switch to the profile's vision model when the turn carries ANY image - a user-attached image OR a
        // screenshot returned by a tool (the computer tool). Missing the tool-result case left computer-use runs on a
        // text-only model that silently ignored the screenshots and hallucinated.
        if (!string.IsNullOrWhiteSpace(_profile.VisionModel)
            && history.Any(h => !string.IsNullOrEmpty(h.ImageDataUrl)
                             || (h.ToolResults != null && h.ToolResults.Any(tr => !string.IsNullOrEmpty(tr.ScreenshotDataUrl)))))
            modelForCall = _profile.VisionModel;

        // Antigravity: Cloud Code streamGenerateContent envelope — a different protocol, handled separately.
        if (_isAntigravityOAuth)
            return await AntigravityCallAsync(systemPrompt, history, tools, modelForCall, ct).ConfigureAwait(false);

        // Anthropic: the Messages API, its own shapes.
        if (_isAnthropic)
            return await AnthropicCallAsync(systemPrompt, history, tools, modelForCall, ct).ConfigureAwait(false);

        var messages = BuildMessages(systemPrompt, history);
        var body     = BuildBody(messages, tools.Count > 0 ? tools : null, modelForCall);
        var bodyJson = body.ToJsonString(BodyJson);
        var sw       = System.Diagnostics.Stopwatch.StartNew();
        string raw   = "";

        var isCodex = _isCodex;   // set in the ctor — a real API key means Platform API, token or not

        try
        {
            var resp = await _http.PostAsync(_chatPath, new StringContent(bodyJson, Encoding.UTF8, "application/json"), ct);
            raw = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                if (isCodex) raw = TryParseCodexStreamError(raw);

                if (IsContextOverflow(raw) && history.Count > 1)
                {
                    var truncated = TruncateHistoryForContextRetry(history);
                    body = BuildBody(BuildMessages(systemPrompt, truncated), tools.Count > 0 ? tools : null, modelForCall);
                    bodyJson = body.ToJsonString(BodyJson);
                    sw.Restart();
                    resp = await _http.PostAsync(_chatPath, new StringContent(bodyJson, Encoding.UTF8, "application/json"), ct);
                    raw = await resp.Content.ReadAsStringAsync(ct);
                    sw.Stop();
                    if (resp.IsSuccessStatusCode)
                    {
                        if (isCodex) raw = ExtractCodexSsePayload(raw);
                        var retryResult = Tagged(ParseResponse(raw));
                        await LogAsync(systemPrompt, truncated, tools, bodyJson, raw, sw.Elapsed, result: retryResult);
                        return retryResult;
                    }
                }
                if (isCodex) raw = TryParseCodexStreamError(raw);
                await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed,
                    error: $"HTTP {(int)resp.StatusCode}: {raw[..Math.Min(500, raw.Length)]}");
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {raw[..Math.Min(600, raw.Length)]}");
            }

            // Codex streams by default — extract the last SSE data payload which contains the full response
            if (isCodex) raw = ExtractCodexSsePayload(raw);

            var result = Tagged(ParseResponse(raw));
            await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed, result: result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException oce)
        {
            // Nobody cancelled ct: this is HttpClient's own timeout surfacing as TaskCanceledException.
            // Rethrow as TimeoutException so router/agent retry paths treat it as TRANSIENT - as an OCE it
            // reads as a deliberate cancellation upstream and aborts the whole run (the TODO 5 stall bug).
            sw.Stop();
            await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed, error: "request timeout: " + oce.Message);
            throw new TimeoutException("LLM request timed out after " + (int)sw.Elapsed.TotalSeconds + "s (HTTP client timeout, not a cancellation).", oce);
        }
        catch (InvalidOperationException)  { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed, error: ex.Message);
            throw;
        }
    }

    /// <summary>ADDITIVE streaming variant (text only, no tools) — POSTs with stream:true, parses the SSE delta chunks,
    /// invokes <paramref name="onDelta"/> with the ACCUMULATED text as it grows, and returns the full text. Used for the
    /// answer module so the UI can render the reply as it writes. The proven non-streaming <see cref="CallAsync"/> is
    /// untouched. Throws on transport/HTTP error so the caller can fall back to the blocking path.</summary>
    public async Task<string> CallStreamAsync(string systemPrompt, IReadOnlyList<ConversationMessage> history,
        Action<string> onDelta, CancellationToken ct)
    {
        LastStreamUsage = null;
        if (_isAnthropic) throw new NotSupportedException("Streaming is not implemented for the Anthropic transport; the blocking path is used.");

        // Antigravity: real incremental streaming off streamGenerateContent's SSE (text appears as it generates).
        if (_isAntigravityOAuth)
            return await AntigravityStreamAsync(systemPrompt, history, onDelta, ct).ConfigureAwait(false);

        // Codex streaming: extract text from SSE response.output_text.delta events.
        if (_profile.MaxSystemPromptChars > 0 && systemPrompt.Length > _profile.MaxSystemPromptChars)
            systemPrompt = systemPrompt[.._profile.MaxSystemPromptChars];

        var body = BuildBody(BuildMessages(systemPrompt, history), withTools: null, _model);
        body["stream"] = true;
        // Ask chat-completions backends to report usage in the final SSE chunk, so streamed calls can be
        // logged in ai_usage like blocking ones. Codex (Responses API) rejects stream_options and reports
        // usage on its response.completed event instead.
        if (!_isCodex) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        var bodyJson = body.ToJsonString(BodyJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, _chatPath)
        { Content = new StringContent(bodyJson, Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {err[..Math.Min(200, err.Length)]}");
        }

        var sb = new StringBuilder();
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        // A stalled SSE stream (provider hangs mid-response - common when a pool is rate-limited/degraded) would
        // otherwise block toward the 45-minute task cap. Abort a read that sees no data for 90s so the call fails
        // fast and the router fails over to another profile / the task retries, instead of wedging the whole build.
        string? line;
        const int streamIdleSec = 90;
        while (true)
        {
            using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idleCts.CancelAfter(TimeSpan.FromSeconds(streamIdleSec));
                try { line = await reader.ReadLineAsync(idleCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
                { throw new TimeoutException("LLM streaming response stalled - no data for " + streamIdleSec + "s; failing over."); }
                catch (IOException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); break; }
                catch (IOException) { break; } // connection closed/aborted by provider — treat as end of stream
            }
            if (line is null) break;
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(data);
                // Codex Responses SSE: text arrives as response.output_text.delta events — the
                // chat-completions 'choices' shape below never appears on that backend.
                if (_isCodex)
                {
                    if (doc.RootElement.TryGetProperty("type", out var evt))
                    {
                        var evtType = evt.GetString();
                        if (evtType == "response.output_text.delta"
                            && doc.RootElement.TryGetProperty("delta", out var cd) && cd.ValueKind == JsonValueKind.String)
                        {
                            var codexChunk = cd.GetString();
                            if (!string.IsNullOrEmpty(codexChunk)) { sb.Append(codexChunk); onDelta(sb.ToString()); }
                        }
                        else if (evtType == "response.completed"
                            && doc.RootElement.TryGetProperty("response", out var cresp) && cresp.ValueKind == JsonValueKind.Object
                            && cresp.TryGetProperty("usage", out var cu) && cu.ValueKind == JsonValueKind.Object)
                            CaptureStreamUsage(cu, codex: true);
                    }
                    continue;
                }
                if (doc.RootElement.TryGetProperty("usage", out var su) && su.ValueKind == JsonValueKind.Object)
                    CaptureStreamUsage(su, codex: false);   // final include_usage chunk (choices is empty there)
                var choices = doc.RootElement.TryGetProperty("choices", out var ch) ? ch : default;
                if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
                if (delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("content", out var c)
                    && c.ValueKind == JsonValueKind.String)
                {
                    var chunk = c.GetString();
                    if (!string.IsNullOrEmpty(chunk)) { sb.Append(chunk); onDelta(sb.ToString()); }
                }
            }
            catch (JsonException) { /* skip a malformed/keepalive chunk */ }
        }
        return sb.ToString();
    }

    /// <summary>Usage reported by the LAST streaming call (chat: the stream_options.include_usage final chunk;
    /// Codex: the response.completed event). Null when the provider sent none. Lets the router record streamed
    /// calls in ai_usage - before this, streamed replies were invisible there.</summary>
    public (int Prompt, int Completion, int Cached, int Total)? LastStreamUsage { get; private set; }

    private void CaptureStreamUsage(JsonElement usage, bool codex)
    {
        int pt = 0, ctok = 0, tt = 0, cached = 0;
        if (codex)
        {
            if (usage.TryGetProperty("input_tokens",  out var i) && i.ValueKind == JsonValueKind.Number) pt   = i.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number) ctok = o.GetInt32();
            if (usage.TryGetProperty("total_tokens",  out var t) && t.ValueKind == JsonValueKind.Number) tt   = t.GetInt32();
            if (usage.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object
                && itd.TryGetProperty("cached_tokens", out var c) && c.ValueKind == JsonValueKind.Number) cached = c.GetInt32();
        }
        else
        {
            if (usage.TryGetProperty("prompt_tokens",     out var i) && i.ValueKind == JsonValueKind.Number) pt   = i.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var o) && o.ValueKind == JsonValueKind.Number) ctok = o.GetInt32();
            if (usage.TryGetProperty("total_tokens",      out var t) && t.ValueKind == JsonValueKind.Number) tt   = t.GetInt32();
            if (usage.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object
                && ptd.TryGetProperty("cached_tokens", out var c) && c.ValueKind == JsonValueKind.Number) cached = c.GetInt32();
        }
        if (tt <= 0) tt = pt + ctok;
        if (tt <= 0 && pt <= 0 && ctok <= 0) return;
        LastStreamUsage = (pt, ctok, cached, tt);
        LlmRouter.RecordUsage(_model, pt, ctok, tt);   // in-memory totals parity with the blocking path (Tagged)
    }

    // ── Anthropic Messages API ────────────────────────────────────────────────────────────────────────────────────

    private async Task<LlmResponse> AnthropicCallAsync(string systemPrompt, IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<ToolDefinition> tools, string modelForCall, CancellationToken ct)
    {
        var bodyJson = BuildAnthropicBody(systemPrompt, history, tools, modelForCall).ToJsonString(BodyJson);
        var sw       = System.Diagnostics.Stopwatch.StartNew();
        string raw   = "";
        try
        {
            using var resp = await _http.PostAsync(_chatPath, new StringContent(bodyJson, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
            {
                if (IsContextOverflow(raw) && history.Count > 1)
                {
                    var truncated = TruncateHistoryForContextRetry(history);
                    bodyJson = BuildAnthropicBody(systemPrompt, truncated, tools, modelForCall).ToJsonString(BodyJson);
                    sw.Restart();
                    using var retry = await _http.PostAsync(_chatPath, new StringContent(bodyJson, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                    raw = await retry.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    sw.Stop();
                    if (retry.IsSuccessStatusCode)
                    {
                        var retryResult = Tagged(ParseAnthropicResponse(raw));
                        await LogAsync(systemPrompt, truncated, tools, bodyJson, raw, sw.Elapsed, result: retryResult);
                        return retryResult;
                    }
                }
                await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed,
                    error: $"HTTP {(int)resp.StatusCode}: {raw[..Math.Min(500, raw.Length)]}");
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {raw[..Math.Min(600, raw.Length)]}");
            }
            var result = Tagged(ParseAnthropicResponse(raw));
            await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed, result: result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException oce)
        {
            sw.Stop();
            await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed, error: "request timeout: " + oce.Message);
            throw new TimeoutException("LLM request timed out after " + (int)sw.Elapsed.TotalSeconds + "s (HTTP client timeout, not a cancellation).", oce);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed, error: ex.Message);
            throw;
        }
    }

    /// <summary>The Messages request: system as a cached text block, the history as alternating user/assistant turns
    /// (tool results are user content, adjacent same-role turns are merged, as the API requires), tools with their
    /// JSON schema as input_schema.</summary>
    private JsonObject BuildAnthropicBody(string systemPrompt, IReadOnlyList<ConversationMessage> history, IReadOnlyList<ToolDefinition> tools, string model)
    {
        var safe = SanitizeHistory(history.ToList());
        var messages = new JsonArray();
        JsonObject? last = null;
        void Add(string role, JsonArray parts)
        {
            if (parts.Count == 0) return;
            if (last is not null && last["role"]!.ToString() == role)
            {
                var content = (JsonArray)last["content"]!;
                foreach (var p in parts) content.Add(p!.DeepClone());
                return;
            }
            last = new JsonObject { ["role"] = role, ["content"] = parts };
            messages.Add(last);
        }
        static JsonObject TextBlock(string t) => new() { ["type"] = "text", ["text"] = t };
        static JsonObject ImageBlock(string dataUrl)
        {
            var (mime, b64) = SplitDataUrl(dataUrl);
            return new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = b64 } };
        }

        foreach (var msg in safe)
        {
            var parts = new JsonArray();
            switch (msg.Role)
            {
                case MessageRole.User:
                    if (!string.IsNullOrWhiteSpace(msg.Text)) parts.Add(TextBlock(msg.Text));
                    if (!string.IsNullOrEmpty(msg.ImageDataUrl)) parts.Add(ImageBlock(msg.ImageDataUrl));
                    Add("user", parts);
                    break;
                case MessageRole.Assistant:
                    if (!string.IsNullOrWhiteSpace(msg.Text)) parts.Add(TextBlock(msg.Text.TrimEnd()));
                    foreach (var tc in msg.ToolCalls)
                    {
                        JsonNode? input = null;
                        try { input = JsonNode.Parse(string.IsNullOrWhiteSpace(tc.ArgsJson) ? "{}" : tc.ArgsJson); } catch { }
                        if (input is not JsonObject) input = new JsonObject();
                        parts.Add(new JsonObject { ["type"] = "tool_use", ["id"] = AnthropicId(tc.Id), ["name"] = tc.Name, ["input"] = input });
                    }
                    Add("assistant", parts);
                    break;
                case MessageRole.Tool:
                    foreach (var tr in msg.ToolResults)
                    {
                        var content = new JsonArray { TextBlock(string.IsNullOrWhiteSpace(tr.Output) ? "(no output)" : tr.Output) };
                        var imgs = tr.ImageDataUrls is { Count: > 0 } ? tr.ImageDataUrls : (tr.ScreenshotDataUrl != null ? [tr.ScreenshotDataUrl] : null);
                        if (imgs != null) foreach (var u in imgs) content.Add(ImageBlock(u));
                        var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = AnthropicId(tr.ToolCallId), ["content"] = content };
                        if (tr.IsError) block["is_error"] = true;
                        parts.Add(block);
                    }
                    Add("user", parts);
                    break;
            }
        }
        if (messages.Count == 0) messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray { TextBlock("(continue)") } });

        var body = new JsonObject
        {
            ["model"]      = model,
            ["max_tokens"] = OutputCap(16000),
            ["messages"]   = messages,
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            body["system"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = systemPrompt, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } } };
        if (_profile.Temperature is { } temp) body["temperature"] = temp;
        else if (_profile.TopP is { } topP) body["top_p"] = topP;
        if (tools.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var t in tools)
                arr.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = JsonNode.Parse(JsonSerializer.Serialize(t.Parameters, Json)) });
            body["tools"] = arr;
        }
        return body;
    }

    /// <summary>Anthropic ids must match [a-zA-Z0-9_-]; ids minted by another provider in a mixed history are mapped
    /// the same way on the call and on its result, so the pairing holds.</summary>
    private static string AnthropicId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "toolu_" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder(id.Length);
        foreach (var c in id) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.ToString();
    }

    private static LlmResponse ParseAnthropicResponse(string raw)
    {
        if (!LooksLikeJson(raw))
            throw new InvalidOperationException($"LLM returned non-JSON body ({raw.Length} chars). First 200: {raw[..Math.Min(200, raw.Length)]}");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "error")
            throw new InvalidOperationException("Anthropic error: " + raw[..Math.Min(600, raw.Length)]);

        var text = new StringBuilder(); var thoughts = new StringBuilder();
        var calls = new List<LlmToolCall>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                var kind = block.TryGetProperty("type", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                switch (kind)
                {
                    case "text":
                        if (block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) text.Append(t.GetString());
                        break;
                    case "thinking":
                        if (block.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String) thoughts.Append(th.GetString());
                        break;
                    case "tool_use":
                        calls.Add(new LlmToolCall
                        {
                            Id = block.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                            Name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            ArgsJson = block.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object ? inp.GetRawText() : "{}",
                        });
                        break;
                }
            }
        var stop = root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String ? sr.GetString() : null;
        var finish = stop switch { "end_turn" => "stop", "tool_use" => "tool_calls", "max_tokens" => "length", "stop_sequence" => "stop", _ => stop };

        int? input = null, output = null, cacheRead = null, cacheWrite = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            if (u.TryGetProperty("input_tokens", out var a) && a.ValueKind == JsonValueKind.Number) input = a.GetInt32();
            if (u.TryGetProperty("output_tokens", out var b) && b.ValueKind == JsonValueKind.Number) output = b.GetInt32();
            if (u.TryGetProperty("cache_read_input_tokens", out var c) && c.ValueKind == JsonValueKind.Number) cacheRead = c.GetInt32();
            if (u.TryGetProperty("cache_creation_input_tokens", out var d) && d.ValueKind == JsonValueKind.Number) cacheWrite = d.GetInt32();
        }
        // input_tokens excludes what the cache served; the prompt as sent is the three together.
        int? prompt = input is null && cacheRead is null && cacheWrite is null ? null : (input ?? 0) + (cacheRead ?? 0) + (cacheWrite ?? 0);
        return new LlmResponse
        {
            Text = text.Length > 0 ? text.ToString() : null,
            Thoughts = thoughts.Length > 0 ? thoughts.ToString() : null,
            ToolCalls = calls, FinishReason = finish,
            PromptTokens = prompt, CompletionTokens = output, CachedTokens = cacheRead,
            TotalTokens = prompt is null && output is null ? null : (prompt ?? 0) + (output ?? 0),
        };
    }

    private bool IsContextOverflow(string raw) => ContextOverflowPatterns.Any(p => p.IsMatch(raw));

    private IReadOnlyList<ConversationMessage> TruncateHistoryForContextRetry(IReadOnlyList<ConversationMessage> history)
    {
        var targetChars = (_profile.NumCtx > 0 ? _profile.NumCtx : 64_000) * 4 * 0.6;
        var kept = new List<ConversationMessage>(); var chars = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var msgChars = CountMessageChars(history[i]);
            if (kept.Count > 0 && chars + msgChars > targetChars) break;
            kept.Add(history[i]); chars += msgChars;
        }
        kept.Reverse();
        var sanitized = SanitizeHistory(kept);
        return sanitized.Count > 0 ? sanitized : [history[^1]];
    }

    // Plain, factual placeholder for a tool_use whose result went missing (dropped parallel result / compaction). It
    // only satisfies the API's pairing rule; it does not instruct the model. Seeing "no result", the model naturally
    // re-issues the call if it still needs that output, or continues if it does not - so the task runs to completion.
    private const string MissingToolResult = "(no result was captured for this tool call)";

    public static List<ConversationMessage> SanitizeHistory(List<ConversationMessage> msgs)
    {
        while (msgs.Count > 0 && msgs[0].Role != MessageRole.User) msgs.RemoveAt(0);
        var result = new List<ConversationMessage>();
        for (var i = 0; i < msgs.Count; i++)
        {
            var msg = msgs[i];
            if (msg.Role == MessageRole.Tool)
            {
                var prev = result.Count > 0 ? result[^1] : null;
                if (prev is null || prev.Role != MessageRole.Assistant || prev.ToolCalls.Count == 0) continue;
                // Every tool_use id on the preceding assistant MUST have a tool_result here, or the request 400s
                // ("tool_use ids ... without tool_result blocks immediately after") and the router retries the SAME
                // broken history across every profile (13 failed calls / ~half the tokens on task 24, 2026-09-07). A
                // cancelled or errored parallel tool, or a compaction that dropped a result, leaves ids uncovered -
                // backfill a synthetic result for each. New objects only; the live session message is never mutated.
                var covered = new HashSet<string>(msg.ToolResults.Select(tr => tr.ToolCallId), StringComparer.Ordinal);
                var missing = prev.ToolCalls.Where(tc => !string.IsNullOrEmpty(tc.Id) && !covered.Contains(tc.Id)).ToList();
                if (missing.Count == 0) { result.Add(msg); continue; }
                var merged = new List<ToolResultRecord>(msg.ToolResults);
                foreach (var tc in missing)
                    merged.Add(new ToolResultRecord { ToolCallId = tc.Id, ToolName = tc.Name, Output = MissingToolResult });
                result.Add(ConversationMessage.FromToolResults(merged));
                continue;
            }
            result.Add(msg);
            // An assistant tool_use turn followed by a NON-tool message would leave the tool_use dangling mid-history
            // (the trailing-only trim below never catches it). Inject the missing results so the pairing holds. A
            // TRAILING unpaired tool_use (no next message) is still dropped by the loop below, unchanged.
            if (msg.Role == MessageRole.Assistant && msg.ToolCalls.Count > 0)
            {
                var next = i + 1 < msgs.Count ? msgs[i + 1] : null;
                if (next is not null && next.Role != MessageRole.Tool)
                {
                    var fills = msg.ToolCalls.Where(tc => !string.IsNullOrEmpty(tc.Id))
                        .Select(tc => new ToolResultRecord { ToolCallId = tc.Id, ToolName = tc.Name, Output = MissingToolResult })
                        .ToList();
                    if (fills.Count > 0) result.Add(ConversationMessage.FromToolResults(fills));
                }
            }
        }
        while (result.Count > 0 && result[^1].Role == MessageRole.Assistant && result[^1].ToolCalls.Count > 0)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static int CountMessageChars(ConversationMessage msg) =>
        msg.Text.Length + msg.ToolCalls.Sum(tc => tc.Name.Length + tc.ArgsJson.Length)
        + msg.ToolResults.Sum(tr => tr.ToolName.Length + tr.Output.Length);

    /// <summary>Call loadCodeAssist + onboardUser to discover and enable the Cloud Code project.</summary>
    private async Task EnsureProjectIdAsync()
    {
        if (_projectId is not null) return;
        var acctKey = !string.IsNullOrWhiteSpace(_profile.OAuthAccountId) ? _profile.OAuthAccountId! : _profile.Name;
        if (_antigravityProjectByAccount.TryGetValue(acctKey, out var cachedProj)) { _projectId = cachedProj; return; }

        var metadata = new JsonObject { ["ideType"] = "ANTIGRAVITY" };
        var loadBody = new JsonObject { ["metadata"] = metadata };
        var loadBodyJson = loadBody.ToJsonString(BodyJson);

        string? tierId = null;

        // Step 1: discover project ID via loadCodeAssist
        foreach (var baseUrl in CloudCodeBaseUrls)
        {
            try
            {
                var url = $"{baseUrl}/v1internal:loadCodeAssist";
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(loadBodyJson, Encoding.UTF8, "application/json"),
                };
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                var raw = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.TryGetProperty("cloudaicompanionProject", out var proj))
                {
                    if (proj.ValueKind == JsonValueKind.String)
                        _projectId = proj.GetString();
                    else if (proj.ValueKind == JsonValueKind.Object && proj.TryGetProperty("id", out var pid))
                        _projectId = pid.GetString();
                }
                // Capture the default tier id (only needed if we must onboard).
                if (root.TryGetProperty("allowedTiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tiers.EnumerateArray())
                        if (t.TryGetProperty("isDefault", out var defEl) && defEl.ValueKind == JsonValueKind.True
                            && t.TryGetProperty("id", out var idEl)) { tierId = idEl.GetString(); break; }
                    tierId ??= (tiers[0].TryGetProperty("id", out var tid2) ? tid2.GetString() : null);
                }
                if (_projectId is not null) break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[antigravity] loadCodeAssist on {baseUrl} failed: {ex.Message}");
            }
        }

        // Step 2: ONLY if loadCodeAssist didn't hand back a project, provision one via onboardUser and read the
        // project straight out of its response. (When loadCodeAssist already returned the project - the common
        // case - onboardUser is pure latency, so skip it.)
        if (_projectId is null && tierId is not null)
        {
            var onboardJson = new JsonObject { ["tier_id"] = tierId, ["metadata"] = new JsonObject { ["ideType"] = "ANTIGRAVITY" } }.ToJsonString(BodyJson);
            foreach (var baseUrl in CloudCodeBaseUrls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1internal:onboardUser")
                    { Content = new StringContent(onboardJson, Encoding.UTF8, "application/json") };
                    using var resp = await _http.SendAsync(req);
                    var raw = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode)
                    {
                        try
                        {
                            using var od = JsonDocument.Parse(raw);
                            if (od.RootElement.TryGetProperty("response", out var r0) && r0.TryGetProperty("cloudaicompanionProject", out var cp))
                                _projectId = cp.ValueKind == JsonValueKind.String ? cp.GetString()
                                           : cp.TryGetProperty("id", out var cpid) ? cpid.GetString() : _projectId;
                        }
                        catch { }
                        break;
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[antigravity] onboardUser on {baseUrl} failed: {ex.Message}"); }
            }
        }

        if (_projectId is not null)
            _antigravityProjectByAccount[acctKey] = _projectId;   // cache so later messages skip the bootstrap entirely
        else
            Console.Error.WriteLine("[antigravity] loadCodeAssist failed on all URLs");
    }

    // ── Antigravity (Google Cloud Code) — the VERIFIED live flow ─────────────────────────────────────────
    // EnsureProjectIdAsync (loadCodeAssist ideType ANTIGRAVITY → free-tier project), then
    // streamGenerateContent?alt=sse with the { project, requestId, model, userAgent:"antigravity",
    // requestType:"agent", request:{ contents, systemInstruction, generationConfig, sessionId } } envelope,
    // authed with the Antigravity User-Agent (set in the ctor). SSE reply: response.candidates[0].content.parts[].text.
    private async Task<LlmResponse> AntigravityCallAsync(string systemPrompt, IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<ToolDefinition> tools, string modelForCall, CancellationToken ct)
    {
        await EnsureProjectIdAsync().ConfigureAwait(false);
        var upstreamModel = ResolveAntigravityModel(modelForCall);
        var body     = BuildAntigravityEnvelope(systemPrompt, history, tools, upstreamModel, thoughts: WantsThoughts(upstreamModel));
        var bodyJson = body.ToJsonString(BodyJson);
        var sw       = System.Diagnostics.Stopwatch.StartNew();
        string raw   = "";
        var watch    = new SilenceWatch(_silence.AllowanceFor(_profile.Name, _model));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse")
            { Content = new StringContent(bodyJson, Encoding.UTF8, "application/json") };
            using var resp = await SendWithinSilenceAsync(_http, req, watch, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                sw.Stop();
                await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed,
                    error: $"HTTP {(int)resp.StatusCode}: {raw[..Math.Min(500, raw.Length)]}", watch: watch);
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {raw[..Math.Min(600, raw.Length)]}");
            }
            var p = await ParseAntigravitySseAsync(resp, ct, watch).ConfigureAwait(false);
            sw.Stop();
            // A PROVIDER NOTICE is not a completion. A retired codename (gemini-3-flash-agent, 2026-09-07) answers
            // HTTP 200 with one bare text chunk - "Gemini 3.5 Flash is no longer available. Please switch to Gemini
            // 3.7 Flash in the latest version of Antigravity." - and NO finishReason, NO usageMetadata, which every
            // real reply carries. Fail the call instead: the router classifies the message as model-unavailable,
            // jails the model 30 min and moves to the next candidate. (Same guard as the engine.)
            if (IsProviderNotice(p.finish, p.tt, p.toolCalls.Count, p.text))
            {
                var notice = $"HTTP 200 provider notice, not a completion (no finishReason/usageMetadata) - model unavailable: {p.text.Trim()[..Math.Min(200, p.text.Trim().Length)]}";
                await LogAsync(systemPrompt, history, tools, bodyJson, p.text, sw.Elapsed, error: notice, watch: watch);
                throw new InvalidOperationException(notice);
            }
            var result = Tagged(new LlmResponse
            {
                Text = p.text, ToolCalls = p.toolCalls, FinishReason = p.finish,
                ModelName = p.model ?? modelForCall, PromptTokens = p.pt, CompletionTokens = p.ctok,
                ReasoningTokens = p.reasoning, ToolUsePromptTokens = p.toolUsePrompt,
                TotalTokens = p.tt, CachedTokens = p.cached,
                Thoughts = p.thoughts.Length > 0 ? p.thoughts : null,
            });
            _silence.Learn(_profile.Name, _model, watch.Elapsed);   // a real completion: the pair's proven need
            await LogAsync(systemPrompt, history, tools, bodyJson, p.text, sw.Elapsed, result: result, watch: watch);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException oce)
        {
            // Same conversion as CallAsync: an HttpClient timeout is transient, not a cancellation.
            sw.Stop();
            await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed, error: "request timeout: " + oce.Message, watch: watch);
            throw new TimeoutException("LLM request timed out after " + (int)sw.Elapsed.TotalSeconds + "s (HTTP client timeout, not a cancellation).", oce);
        }
        catch (InvalidOperationException)  { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            await LogAsync(systemPrompt, history, tools, bodyJson, raw, sw.Elapsed, error: ex.Message, watch: watch);
            throw;
        }
    }

    // The wait for the response headers, bounded by the pair's silence allowance instead of the 600 s HTTP ceiling:
    // the provider accepting a request and never answering it is the stall seen in the wild, and the next login
    // answers the same prompt in seconds. Operator Stop / the run cap (ct) still win over the allowance.
    internal static async Task<HttpResponseMessage> SendWithinSilenceAsync(HttpClient http, HttpRequestMessage req, SilenceWatch watch, CancellationToken ct)
    {
        using var silent = CancellationTokenSource.CreateLinkedTokenSource(ct);
        silent.CancelAfter(watch.Allowance);
        try
        {
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, silent.Token).ConfigureAwait(false);
            watch.Received();
            return resp;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && silent.IsCancellationRequested)
        {
            throw new TimeoutException($"LLM request stalled - no response headers for {(int)watch.Allowance.TotalSeconds}s (this login and model never needed more than a third of that); failing over.");
        }
    }

    // Streaming twin of AntigravityCallAsync — same envelope, but emits text incrementally as the SSE arrives.
    private async Task<string> AntigravityStreamAsync(string systemPrompt, IReadOnlyList<ConversationMessage> history,
        Action<string> onDelta, CancellationToken ct)
    {
        await EnsureProjectIdAsync().ConfigureAwait(false);
        var bodyJson = BuildAntigravityEnvelope(systemPrompt, history, [], ResolveAntigravityModel(_model)).ToJsonString(BodyJson);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse")
        { Content = new StringContent(bodyJson, Encoding.UTF8, "application/json") };
        var watch = new SilenceWatch(_silence.AllowanceFor(_profile.Name, _model));
        using var resp = await SendWithinSilenceAsync(_http, req, watch, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {err[..Math.Min(200, err.Length)]}");
        }
        var sb = new StringBuilder();
        bool sawFinish = false, sawUsage = false;
        int promptTokens = 0, completionTokens = 0, totalTokens = 0, cachedTokens = 0;
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await ReadSseLineAsync(reader, ct, watch.AllowanceMs).ConfigureAwait(false)) is not null)
        {
            watch.Received();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;   // some chunks are arrays/keepalives - TryGetProperty would throw
                if (root.TryGetProperty("response", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object) root = wrapped;
                if (root.TryGetProperty("usageMetadata", out var um) && um.ValueKind == JsonValueKind.Object)
                {
                    sawUsage = true;
                    if (um.TryGetProperty("promptTokenCount", out var p) && p.TryGetInt32(out var pv)) promptTokens = pv;
                    if (um.TryGetProperty("candidatesTokenCount", out var c) && c.TryGetInt32(out var cv)) completionTokens = cv;
                    if (um.TryGetProperty("totalTokenCount", out var t) && t.TryGetInt32(out var tv)) totalTokens = tv;
                    if (um.TryGetProperty("cachedContentTokenCount", out var cache) && cache.TryGetInt32(out var cacheValue)) cachedTokens = cacheValue;
                }
                if (root.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
                {
                    var cand = cands[0];
                    if (cand.ValueKind == JsonValueKind.Object && cand.TryGetProperty("finishReason", out var fr) && fr.ValueKind == JsonValueKind.String) sawFinish = true;
                    if (cand.ValueKind == JsonValueKind.Object && cand.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Object && content.TryGetProperty("parts", out var parts)
                        && parts.ValueKind == JsonValueKind.Array)
                        foreach (var pp in parts.EnumerateArray())
                            if (pp.ValueKind == JsonValueKind.Object && pp.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            {
                                var chunk = t.GetString();
                                if (!string.IsNullOrEmpty(chunk)) { sb.Append(chunk); onDelta(sb.ToString()); }
                            }
                }
            }
            catch { /* skip a partial/keepalive/unexpected chunk */ }
        }
        // Same guard as the blocking path: text without any finishReason/usageMetadata is a provider notice
        // ("... is no longer available ..."), not a completion - fail so the router moves on.
        if (IsProviderNotice(sawFinish ? "STOP" : null, sawUsage ? 0 : null, 0, sb.ToString()))
            throw new InvalidOperationException($"HTTP 200 provider notice, not a completion (no finishReason/usageMetadata) - model unavailable: {sb.ToString().Trim()[..Math.Min(200, sb.ToString().Trim().Length)]}");
        if (sawUsage && (promptTokens > 0 || completionTokens > 0 || totalTokens > 0 || cachedTokens > 0))
        {
            if (totalTokens <= 0) totalTokens = promptTokens + completionTokens;
            LastStreamUsage = (promptTokens, completionTokens, cachedTokens, totalTokens);
            LlmRouter.RecordUsage(_model, promptTokens, completionTokens, totalTokens);
        }
        _silence.Learn(_profile.Name, _model, watch.Elapsed);
        return sb.ToString();
    }

    /// <summary>True when a Cloud Code stream ended with text but WITHOUT the finishReason and usageMetadata every
    /// real completion carries - the shape of a provider notice ("... is no longer available. Please switch to ...").
    /// Shape first, wording second: an unknown notice with the same shape is refused too.</summary>
    public static bool IsProviderNotice(string? finish, int? totalTokens, int toolCalls, string text)
        => finish is null && totalTokens is null && toolCalls == 0 && !string.IsNullOrWhiteSpace(text);

    // Friendly names → Antigravity upstream codenames (OmniRoute's antigravityModelAliases). Pass through anything
    // already a codename (e.g. gemini-pro-agent). Empty → the known-good default.
    private static string ResolveAntigravityModel(string model)
    {
        var m = (model ?? "").Trim();
        if (m.StartsWith("antigravity/", StringComparison.OrdinalIgnoreCase)) m = m["antigravity/".Length..];
        return m.ToLowerInvariant() switch
        {
            "gemini-3.1-pro-high" or "gemini-3.1-pro" or "gemini-3-pro" or "gemini-pro" or "gemini-3-pro-high" => "gemini-pro-agent",
            "gemini-3-pro-image-preview"                                     => "gemini-3-pro-image",
            "gemini-claude-sonnet-4-5" or "gemini-claude-sonnet-4-5-thinking" => "claude-sonnet-4-6",
            "gemini-claude-opus-4-5-thinking"                                => "claude-opus-4-6-thinking",
            ""                                                               => "gemini-pro-agent",
            _                                                                => m,
        };
    }

    /// <summary>Ask for the model's reasoning to be returned with its reply. A worker spent 548,000 reasoning tokens
    /// in one project build and not a word of it came back: nothing in the request asked for it, so the chat showed
    /// 2,800 tool calls and no reason for any of them. Asked for, Code Assist returns a short summary as a part marked
    /// <c>thought</c>, ahead of the tool call (verified live 2026-10-05, gemini-3.8-flash-high: 229 characters for
    /// 402 reasoning tokens, the output count unchanged). Gemini models only - that is what was verified - and not
    /// on a profile that turns thinking off.</summary>
    internal bool WantsThoughts(string upstreamModel) =>
        !_profile.DisableThinking && upstreamModel.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);

    internal JsonObject BuildAntigravityEnvelope(string systemPrompt, IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<ToolDefinition> tools, string model, bool thoughts = false)
    {
        var contents = new JsonArray();
        // THOUGHT-SIGNATURE TRIM: a long Gemini run accumulates one opaque thoughtSignature (measured 1-12KB each) per
        // signed model turn, replayed verbatim on every call - 39 of them were ~21% of a turn-39 coder prompt. A
        // signature's reasoning value is spent after ~1 turn, and Google documents "skip_thought_signature_validator"
        // to bypass signature validation. So keep the REAL signature only on the newest KeepSignedModelTurns model turns
        // (the recent reasoning chain the API validates strictly - the most recent turn is ALWAYS in this set) and send
        // the dummy on OLDER ones. If Gemini 400s on a downgraded old call, raise KeepSignedModelTurns (set it very large
        // to disable). Watch promptTokenCount: it should drop on long runs with no 400.
        const int KeepSignedModelTurns = 4;
        var signedTurnIdx = new List<int>();
        for (int i = 0; i < history.Count; i++)
            if (history[i].Role == MessageRole.Assistant && history[i].ToolCalls is { Count: > 0 })
                signedTurnIdx.Add(i);
        var keepRealSig = new HashSet<int>(signedTurnIdx.Skip(Math.Max(0, signedTurnIdx.Count - KeepSignedModelTurns)));
        for (int mi = 0; mi < history.Count; mi++)
        {
            var msg = history[mi];
            var parts = new JsonArray();
            if (!string.IsNullOrEmpty(msg.Text)) parts.Add(new JsonObject { ["text"] = msg.Text });
            if (!string.IsNullOrEmpty(msg.ImageDataUrl))
            {
                var (mime, b64) = SplitDataUrl(msg.ImageDataUrl);
                if (b64.Length > 0) parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = b64 } });
            }
            // The assistant's OWN tool calls → Gemini functionCall parts. Without these the model's actions vanish from
            // the history (empty-text turns were dropped by the parts.Count==0 skip below), so it re-issues calls it
            // already made - the "amnesia"/loop we saw. Gemini wants args as a JSON object, not a string.
            foreach (var tc in msg.ToolCalls)
            {
                JsonNode? argsNode = null;
                try { argsNode = JsonNode.Parse(string.IsNullOrWhiteSpace(tc.ArgsJson) ? "{}" : tc.ArgsJson); } catch { argsNode = null; }
                var fcObj = new JsonObject { ["name"] = tc.Name, ["args"] = argsNode ?? new JsonObject() };
                // Emit the call id. Code Assist maps functionCall.id -> the Anthropic tool_use.id; with MULTIPLE
                // functionCalls in one turn (parallel tools) the request 400s ("messages.N.content.M.tool_use.id:
                // Field required") without it. The id is already unique + matched by the functionResponse below.
                if (!string.IsNullOrEmpty(tc.Id)) fcObj["id"] = tc.Id;
                var fcPart = new JsonObject { ["functionCall"] = fcObj };
                // Echo the thoughtSignature Gemini gave us for this call - required or the API 400s. Calls made by a
                // non-Gemini model (primary/fallback swap) have none; Google documents this exact dummy value to skip
                // signature validation for traces transferred from another model, so a mixed-model history can still
                // run on Gemini instead of 400ing on every candidate.
                // Real signature only on the newest KeepSignedModelTurns model turns; older signed calls -> dummy (see
                // the THOUGHT-SIGNATURE TRIM note above). Unsigned calls already fall through to the dummy as before.
                fcPart["thoughtSignature"] = (!string.IsNullOrEmpty(tc.ThoughtSignature) && keepRealSig.Contains(mi))
                    ? tc.ThoughtSignature
                    : "skip_thought_signature_validator";
                parts.Add(fcPart);
            }
            // Tool results → Gemini functionResponse parts, NOT plain text. As text the model cannot tell a result from
            // the user speaking, so it never registers that its call returned. response must be a JSON object.
            // ORDER: every functionResponse first, then the text, then every image. Code Assist translates these parts
            // one to one for a Claude model, and Anthropic requires each tool_result to follow its tool_use with
            // nothing between: two calls in one step with a screenshot after the first result ("result A, image,
            // result B") 400ed on Opus with "tool_use ids were found without tool_result blocks immediately after",
            // the router fell to the Gemini fallback and the run stayed there (websisco task 8, 2026-10-06). Gemini
            // does not care about the order within a user content.
            if (msg.ToolResults.Count > 0)
            {
                var results = new JsonArray();
                var images  = new JsonArray();
                foreach (var tr in msg.ToolResults)
                {
                    var frObj = new JsonObject
                    {
                        ["name"]     = tr.ToolName,
                        ["response"] = new JsonObject { ["result"] = tr.Output ?? "" },
                    };
                    // Match the functionCall id above so Code Assist can pair this result to its Anthropic tool_use.
                    if (!string.IsNullOrEmpty(tr.ToolCallId)) frObj["id"] = tr.ToolCallId;
                    results.Add(new JsonObject { ["functionResponse"] = frObj });
                    var geminiImgs = tr.ImageDataUrls is { Count: > 0 } ? tr.ImageDataUrls : (tr.ScreenshotDataUrl != null ? [tr.ScreenshotDataUrl] : null);
                    if (geminiImgs != null)
                        foreach (var imgUrl in geminiImgs)
                        {
                            var (smime, sb64) = SplitDataUrl(imgUrl);
                            if (sb64.Length > 0) images.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = smime, ["data"] = sb64 } });
                        }
                }
                var rest = parts;   // the text and the message's own image, built above
                parts = new JsonArray();
                foreach (var p in results) parts.Add(p!.DeepClone());
                foreach (var p in rest)    parts.Add(p!.DeepClone());
                foreach (var p in images)  parts.Add(p!.DeepClone());
            }
            if (parts.Count == 0) continue;
            // Assistant → "model"; user text and tool results → "user". Gemini/CloudCode REQUIRE strict user/model
            // alternation and do NOT merge neighbours, so any two adjacent same-role turns would 400 - e.g. a tool
            // result followed by a user nudge (empty-response nudge, step-limit message), a re-anchored task prompt
            // before a recent user turn, or a content-less assistant that got dropped just above (parts.Count==0).
            // FOLD this turn's parts into the previous content when the role matches, preserving the alternation.
            AddOrMergeContent(contents, msg.Role == MessageRole.Assistant ? "model" : "user", parts);
        }
        if (contents.Count == 0)
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray { new JsonObject { ["text"] = " " } } });

        // The conversation's own id when the agent loop set one (stable across its calls: same shard, cached
        // prefix); the per-client id only for callers outside a session.
        var request = new JsonObject { ["contents"] = contents, ["sessionId"] = LlmCallScope.SessionId.Value ?? _sessionId };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            request["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } } };
        var gen = new JsonObject();
        if (_profile.Temperature is { } tp) gen["temperature"] = tp;
        if (_profile.TopP is { } topp) gen["topP"] = topp;
        // Output budget: without this, Code Assist's small default truncates a large write_file mid-generation
        // (finishReason MAX_TOKENS, the tool call is dropped, the task fails without TASK_COMPLETE). Default high
        // so a full file (e.g. a multi-step form controller) fits one turn; a profile MaxTokens still overrides.
        gen["maxOutputTokens"] = OutputCap(32000);
        if (thoughts) gen["thinkingConfig"] = new JsonObject { ["includeThoughts"] = true };
        if (gen.Count > 0) request["generationConfig"] = gen;
        if (tools is { Count: > 0 })
        {
            var decls = new JsonArray();
            foreach (var t in tools)
                decls.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonSerializer.SerializeToNode(t.Parameters, BodyJson) });
            request["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = decls } };
        }
        return new JsonObject
        {
            ["project"]     = _projectId ?? "",
            ["requestId"]   = Guid.NewGuid().ToString("N"),
            ["model"]       = model,
            ["userAgent"]   = "antigravity",
            ["requestType"] = "agent",
            ["request"]     = request,
        };
    }

    // net472's ReadLineAsync(ct) shim (NetFxLlmPolyfills) cannot cancel the underlying read, and the Antigravity body
    // is read with ResponseHeadersRead so HttpClient.Timeout does not bound it - a wedged stream would hang forever,
    // immune to the run cap AND operator Stop. Guard each line with an idle watchdog: no data for this long, or the
    // token trips, and we abandon the read (the caller's `using` disposes the stream, faulting the orphaned read) and
    // throw. A TimeoutException classifies Transient, so the loop retries instead of hanging.
    // Append a Gemini "contents" turn, folding into the previous entry when the role matches so the request never
    // carries two adjacent same-role turns (which Gemini/CloudCode reject). Empty part sets are skipped.
    internal static void AddOrMergeContent(JsonArray contents, string role, JsonArray parts)
    {
        if (parts == null || parts.Count == 0) return;
        if (contents.Count > 0 && contents[contents.Count - 1] is JsonObject prevC
            && prevC["role"]?.ToString() == role && prevC["parts"] is JsonArray prevParts)
            foreach (var pt in parts)
            {
                if (pt != null) prevParts.Add(pt.DeepClone());
            }
        else
            contents.Add(new JsonObject { ["role"] = role, ["parts"] = parts });
    }

    // The default when no SilenceWatch is given (tests, callers outside the two Cloud Code paths): the live paths pass
    // the pair's learned allowance (SilencePolicy), which starts here and comes down to what the pair has shown it
    // needs. The PRIMARY interrupt is `ct` (operator Stop / run cap), which the linked CTS honours promptly
    // REGARDLESS of this value - the idle is only a backstop for a silent-but-open socket that would otherwise hang
    // forever (ResponseHeadersRead means HttpClient's own timeout no longer bounds the body read).
    private const int AntigravityStreamIdleMs = SilencePolicy.CeilingSec * 1000;
    private static async Task<string?> ReadSseLineAsync(StreamReader reader, CancellationToken ct, int idleMs = AntigravityStreamIdleMs)
    {
        ct.ThrowIfCancellationRequested();
        var readTask = reader.ReadLineAsync();   // argless net472 read - not itself cancellable
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = await Task.WhenAny(readTask, Task.Delay(idleMs, idleCts.Token)).ConfigureAwait(false);
        if (done == readTask)
        {
            idleCts.Cancel();                    // free the idle timer
            return await readTask.ConfigureAwait(false);
        }
        _ = readTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);   // observe the orphaned read's fault
        ct.ThrowIfCancellationRequested();       // operator Stop / run cap wins first
        throw new TimeoutException($"Antigravity stream stalled - no data for {idleMs / 1000}s");
    }

    // Test hook: drive the SSE idle watchdog with a short idle and no live stream.
    internal static Task<string?> DebugReadSseLine(StreamReader reader, CancellationToken ct, int idleMs)
        => ReadSseLineAsync(reader, ct, idleMs);

    internal static async Task<(string text, List<LlmToolCall> toolCalls, string? finish, int? pt, int? ctok, int? tt, int? cached, int? reasoning, int? toolUsePrompt, string? model, string thoughts)>
        ParseAntigravitySseAsync(HttpResponseMessage resp, CancellationToken ct, SilenceWatch? watch = null)
    {
        var text = new StringBuilder();
        var thoughts = new StringBuilder();   // parts marked `thought`: the model's reasoning summary, never part of its reply
        var toolCalls = new List<LlmToolCall>();
        string? finish = null, model = null, lastSig = null;
        int? pt = null, ctok = null, tt = null, cached = null, reasoning = null, toolUsePrompt = null;
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await ReadSseLineAsync(reader, ct, watch?.AllowanceMs ?? AntigravityStreamIdleMs).ConfigureAwait(false)) is not null)
        {
            watch?.Received();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;   // some chunks are arrays/keepalives - TryGetProperty would throw
                if (root.TryGetProperty("response", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object) root = wrapped;   // Cloud Code wraps the Gemini reply
                if (root.TryGetProperty("modelVersion", out var mv) && mv.ValueKind == JsonValueKind.String) model = mv.GetString();
                if (root.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
                {
                    var cand = cands[0];
                    if (cand.ValueKind == JsonValueKind.Object)
                    {
                        if (cand.TryGetProperty("finishReason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString();
                        if (cand.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                            && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                            foreach (var pp in parts.EnumerateArray())
                            {
                                if (pp.ValueKind != JsonValueKind.Object) continue;
                                // Gemini 3 hangs an opaque thoughtSignature off the part (usually the functionCall part
                                // itself, sometimes a preceding thought part). Capture it - it MUST be echoed back on the
                                // functionCall next turn or the API 400s. Carry the last-seen one forward to the call.
                                if (pp.TryGetProperty("thoughtSignature", out var tsg) && tsg.ValueKind == JsonValueKind.String)
                                    lastSig = tsg.GetString();
                                if (pp.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                                {
                                    // Reasoning goes to its own buffer. Appended to the reply it would be stored in the
                                    // conversation, re-sent on every later call, and taken as the answer on a final turn.
                                    bool isThought = pp.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True;
                                    (isThought ? thoughts : text).Append(t.GetString());
                                    // Live view for the host: the chunk as it arrives, reasoning and reply told apart.
                                    if (LlmCallScope.OnDelta.Value is { } onDelta && t.GetString() is { Length: > 0 } chunk)
                                        try { onDelta(isThought ? "thought" : "text", chunk); } catch { }
                                }
                                else if (pp.TryGetProperty("functionCall", out var fc) && fc.ValueKind == JsonValueKind.Object)
                                {
                                    var name = fc.TryGetProperty("name", out var fn) ? fn.GetString() ?? "" : "";
                                    var args = fc.TryGetProperty("args", out var fa) ? fa.GetRawText() : "{}";
                                    toolCalls.Add(new LlmToolCall { Id = "call_" + Guid.NewGuid().ToString("N"), Name = name, ArgsJson = args, ThoughtSignature = lastSig });
                                    lastSig = null;   // consumed by this call
                                }
                            }
                    }
                }
                if (root.TryGetProperty("usageMetadata", out var um) && um.ValueKind == JsonValueKind.Object)
                {
                    if (um.TryGetProperty("promptTokenCount",     out var a) && a.TryGetInt32(out var av)) pt   = av;
                    if (um.TryGetProperty("candidatesTokenCount", out var b) && b.TryGetInt32(out var bv)) ctok = bv;
                    if (um.TryGetProperty("totalTokenCount",      out var c) && c.TryGetInt32(out var cv)) tt   = cv;
                    if (um.TryGetProperty("thoughtsTokenCount",   out var d) && d.TryGetInt32(out var dv)) reasoning = dv;
                    if (um.TryGetProperty("toolUsePromptTokenCount", out var e2) && e2.TryGetInt32(out var e2v)) toolUsePrompt = e2v;
                    // Cache-hit credit (implicit context caching). Code Assist reports it here when the stable prefix hits;
                    // without carrying it, ai_usage always logged 0 cached and billed the whole prompt at the miss rate.
                    if (um.TryGetProperty("cachedContentTokenCount", out var e) && e.TryGetInt32(out var ev)) cached = ev;
                }
            }
            catch { /* skip a malformed/keepalive/unexpected chunk */ }
        }
        // Gemini reports visible candidate, hidden thinking, and tool-use prompt tokens separately. Keep input and
        // output semantics intact while including all provider-reported components in the canonical total.
        int input = InputTokenCount(pt ?? 0, toolUsePrompt ?? 0);
        ctok = GeneratedTokenCount(input, ctok ?? 0, reasoning ?? 0, tt ?? 0);
        return (text.ToString(), toolCalls, finish, input, ctok, tt > 0 ? tt : input + (ctok ?? 0), cached, reasoning, toolUsePrompt, model, thoughts.ToString().Trim());
    }

    private async Task LogAsync(
        string systemPrompt, IReadOnlyList<ConversationMessage> history, IReadOnlyList<ToolDefinition> tools,
        string bodyJson, string rawResponse, TimeSpan elapsed, LlmResponse? result = null, string? error = null, SilenceWatch? watch = null)
    {
        if (_logger is null) return;
        int? pt = null, ctok = null, tt = null, cached = null, reasoning = null, toolUsePrompt = null;
        if (LooksLikeJson(rawResponse))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawResponse);
                // rawResponse may be a JSON ARRAY or primitive (e.g. the model emitted array-leading text) - calling
                // TryGetProperty on a non-object root throws InvalidOperationException, which is NOT a JsonException.
                // Logging must NEVER break the actual LLM call, so gate on the root kind AND swallow everything below.
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    if (u.TryGetProperty("prompt_tokens",     out var a) && a.ValueKind == JsonValueKind.Number) pt   = a.GetInt32();
                    if (u.TryGetProperty("completion_tokens", out var b) && b.ValueKind == JsonValueKind.Number) ctok = b.GetInt32();
                    if (u.TryGetProperty("total_tokens",      out var c) && c.ValueKind == JsonValueKind.Number) tt   = c.GetInt32();
                    if (u.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object
                        && ptd.TryGetProperty("cached_tokens", out var ca) && ca.ValueKind == JsonValueKind.Number)
                        cached = ca.GetInt32();
                    if (u.TryGetProperty("completion_tokens_details", out var ctd) && ctd.ValueKind == JsonValueKind.Object
                        && ctd.TryGetProperty("reasoning_tokens", out var rt) && rt.ValueKind == JsonValueKind.Number)
                        reasoning = rt.GetInt32();
                    if (u.TryGetProperty("prompt_tokens_details", out var ptd2) && ptd2.ValueKind == JsonValueKind.Object
                        && ptd2.TryGetProperty("tool_use_prompt_tokens", out var tu) && tu.ValueKind == JsonValueKind.Number)
                        toolUsePrompt = tu.GetInt32();
                }
                // Native Gemini responses use usageMetadata rather than the OpenAI-compatible usage object.
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("usageMetadata", out var geminiUsage)
                    && geminiUsage.ValueKind == JsonValueKind.Object)
                {
                    int geminiPrompt = 0;
                    if (geminiUsage.TryGetProperty("promptTokenCount", out var gp) && gp.TryGetInt32(out var gpv)) geminiPrompt = gpv;
                    int candidateCount = 0;
                    if (geminiUsage.TryGetProperty("candidatesTokenCount", out var gc) && gc.TryGetInt32(out var gcv)) candidateCount = gcv;
                    if (geminiUsage.TryGetProperty("thoughtsTokenCount", out var gr) && gr.TryGetInt32(out var grv)) reasoning = grv;
                    int geminiToolUsePrompt = 0;
                    if (geminiUsage.TryGetProperty("toolUsePromptTokenCount", out var gtu) && gtu.TryGetInt32(out var gtuv)) geminiToolUsePrompt = gtuv;
                    if (geminiUsage.TryGetProperty("totalTokenCount", out var gt) && gt.TryGetInt32(out var gtv)) tt = gtv;
                    if (geminiUsage.TryGetProperty("cachedContentTokenCount", out var gcc) && gcc.TryGetInt32(out var gccv)) cached = gccv;
                    toolUsePrompt = geminiToolUsePrompt;
                    pt = InputTokenCount(geminiPrompt, geminiToolUsePrompt);
                    ctok = GeneratedTokenCount(pt ?? 0, candidateCount, reasoning ?? 0, tt ?? 0);
                }
            }
            catch { /* usage extraction is best-effort - never let it fail the call */ }
        }
        // Streamed calls hand the parsed TEXT as rawResponse (no usage json to mine), and non-OpenAI usage
        // shapes don't match the fields above - but the RESPONSE object already carries whatever the client
        // parsed (it feeds usage.json). Fall back to it so every logged line shows its tokens.
        pt ??= result?.PromptTokens; ctok ??= result?.CompletionTokens; tt ??= result?.TotalTokens;
        cached ??= result?.CachedTokens;
        reasoning ??= result?.ReasoningTokens;
        toolUsePrompt ??= result?.ToolUsePromptTokens;
        await _logger.LogAsync(new LlmCallLogEntry
        {
            Profile = _profile.Name, Model = _model, TurnMs = (int)elapsed.TotalMilliseconds,
            IsError = error is not null, Error = error, SystemPrompt = systemPrompt,
            MessageCount = history.Count, ToolCount = tools.Count, RawRequest = bodyJson,
            Tools = string.Join(",", tools.Select(t => t.Name)), NewInput = LlmCallLogger.NewInputOf(history),
            ResponseText = result?.Text, Thoughts = result?.Thoughts, ToolCallCount = result?.ToolCalls.Count ?? 0,
            ToolCalls = result != null && result.ToolCalls.Count > 0 ? string.Join("\n", result.ToolCalls.Select(t => t.Name + " " + t.ArgsJson)) : null,
            FinishReason = result?.FinishReason, RawResponse = rawResponse.Length > 0 ? rawResponse : null,
            PromptTokens = pt, CompletionTokens = ctok, ReasoningTokens = reasoning, ToolUsePromptTokens = toolUsePrompt,
            TotalTokens = tt, CachedTokens = cached,
            FirstByteMs = watch?.FirstByte is { } fb ? (int)fb.TotalMilliseconds : null,
            LongestSilenceMs = watch is null ? null : (int)watch.LongestSilence.TotalMilliseconds,
            SilenceAllowanceMs = watch?.AllowanceMs,
        });
    }

    private static int InputTokenCount(int prompt, int toolUsePrompt)
    {
        long input = Math.Max(0, (long)prompt) + Math.Max(0, toolUsePrompt);
        return input > int.MaxValue ? int.MaxValue : (int)input;
    }

    private static int GeneratedTokenCount(int input, int candidates, int reasoning, int total)
    {
        long generated = Math.Max(0L, (long)candidates + Math.Max(0, reasoning));
        // If a provider omits its component fields, preserve a non-zero aggregate rather than undercounting it.
        // When fields are present, tool-use prompt tokens remain input and are not mislabeled as output.
        if (generated == 0 && total > input) generated = (long)total - input;
        return generated > int.MaxValue ? int.MaxValue : (int)generated;
    }

    /// <summary>The output cap sent with a request: the profile's own when set, else <paramref name="fallback"/>,
    /// and never above the cap the calling scope asked for (<see cref="LlmCallScope.MaxOutputTokens"/>).</summary>
    private int OutputCap(int fallback)
    {
        var cap = _profile.MaxTokens > 0 ? _profile.MaxTokens : fallback;
        if (LlmCallScope.MaxOutputTokens.Value is int scoped && scoped > 0 && (cap <= 0 || scoped < cap)) cap = scoped;
        return cap;
    }

    private LlmResponse Tagged(LlmResponse r)
    {
        r.ProfileName = _profile.Name; r.ModelName = _model;
        if (r.PromptTokens is not null || r.CompletionTokens is not null || r.TotalTokens is not null)
            LlmRouter.RecordUsage(r.ModelName, r.PromptTokens ?? 0, r.CompletionTokens ?? 0, r.TotalTokens ?? 0);
        return r;
    }

    private JsonObject BuildBody(JsonArray messages, IReadOnlyList<ToolDefinition>? withTools, string? modelOverride = null)
    {
        var isCodex = _isCodex;   // must match the endpoint chosen in the ctor, never re-derived here

        if (isCodex)
            return BuildCodexBody(messages, withTools, modelOverride);

        var body = new JsonObject { ["model"] = modelOverride ?? _model, ["messages"] = messages, ["temperature"] = _profile.Temperature ?? 0 };
        if (_profile.TopP.HasValue) body["top_p"] = _profile.TopP.Value;
        if (withTools is { Count: > 0 }) body["tools"] = BuildTools(withTools);
        if (_profile.NumCtx > 0) body["options"] = new JsonObject { ["num_ctx"] = _profile.NumCtx };
        if (_profile.DisableThinking) body["thinking"] = new JsonObject { ["type"] = "disabled" };
        // Opt-in strict JSON: only when the profile asks for it, the provider accepts the OpenAI-style
        // response_format, and this is NOT a tool call (json_object + tools is rejected by some models).
        if (_profile.JsonMode && (withTools is null || withTools.Count == 0) && SupportsJsonResponseFormat(_profile.Provider))
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
            EnsureJsonMentioned(messages);
        }
        // Send a cap ALWAYS: with no max_tokens the provider applies its own default, and DeepSeek's is 8192.
        // A write_file whose content ran past it came back as tool-call arguments cut mid-JSON ("Could not parse
        // arguments as JSON"), the model re-issued the same oversized call, and 9 write_file plus 28 terminal
        // calls were lost that way in one day - each re-sending a 100-200k prompt (2026-09-15). The Gemini path
        // already defaults to 32000; 16000 stays under the smallest common chat-model output cap.
        body["max_tokens"] = OutputCap(16000);
        return body;
    }

    /// <summary>Build a Codex Responses API body (POST /responses). The Codex backend accepts a
    /// similar shape to v1/chat/completions but with different field names and the `store` flag.</summary>
    private JsonObject BuildCodexBody(JsonArray messages, IReadOnlyList<ToolDefinition>? withTools, string? modelOverride = null)
    {
        var body = new JsonObject
        {
            ["model"] = modelOverride ?? _model,
            ["input"] = ToCodexInput(messages),
            ["stream"] = true,     // Codex requires streaming
            ["store"] = false,
            // Prompt-cache routing hint: the backend caches by exact prefix but ROUTES by this key —
            // without a stable key, repeat calls can land on shards without the cached prefix (observed:
            // ~10k identical prompt tokens billed uncached turn after turn).
            ["prompt_cache_key"] = "vanity-" + (_profile.OAuthAccountId ?? _profile.Name),
        };
        if (_profile.Temperature.HasValue) body["temperature"] = _profile.Temperature.Value;
        if (_profile.TopP.HasValue) body["top_p"] = _profile.TopP.Value;
        if (withTools is { Count: > 0 }) body["tools"] = BuildCodexTools(withTools);
        if (_profile.DisableThinking) body["reasoning"] = new JsonObject { ["effort"] = "none" };
        if (_profile.MaxTokens > 0 || LlmCallScope.MaxOutputTokens.Value is > 0) body["max_output_tokens"] = OutputCap(_profile.MaxTokens);
        return body;
    }

    /// <summary>Re-shape chat-completions messages into Responses `input` items.
    /// Tool round-trips and multimodal user content are the places the two APIs disagree, and the backend
    /// rejects the chat shape outright (verified live: an assistant `tool_calls` array + a `role:"tool"` reply → HTTP 400
    /// "Missing required parameter: 'input[N].content'"). Responses carries the same round-trip as two FLAT
    /// items — function_call, then function_call_output — paired by call_id rather than nested in a message.
    /// Everything else (plain system/user/assistant text) is already valid Responses input and passes through.</summary>
    private static JsonArray ToCodexInput(JsonArray messages)
    {
        var arr = new JsonArray();
        foreach (var node in messages)
        {
            if (node is not JsonObject msg) continue;
            var role = msg["role"]?.ToString();

            if (role == "tool")
            {
                arr.Add(new JsonObject
                {
                    ["type"]    = "function_call_output",
                    ["call_id"] = msg["tool_call_id"]?.ToString() ?? "",
                    ["output"]  = msg["content"]?.ToString() ?? "",
                });
                continue;
            }

            // Multimodal user content is the OTHER place the two APIs disagree: chat parts
            // (`text` / `image_url:{url}`) → Responses parts (`input_text` / `input_image` with a
            // plain-string image_url). The backend rejects the chat names outright (verified live:
            // HTTP 400 "Invalid value: 'text'. Supported values are: 'input_text', 'input_image', …").
            if (role == "user" && msg["content"] is JsonArray chatParts)
            {
                var parts = new JsonArray();
                foreach (var p in chatParts)
                {
                    if (p is not JsonObject part) continue;
                    switch (part["type"]?.ToString())
                    {
                        case "text":
                            parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = part["text"]?.ToString() ?? "" });
                            break;
                        case "image_url":
                            parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = part["image_url"]?["url"]?.ToString() ?? "" });
                            break;
                    }
                }
                arr.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                continue;
            }

            if (role == "assistant" && msg["tool_calls"] is JsonArray calls && calls.Count > 0)
            {
                // the assistant's own words (when it spoke as well as called) stay a normal message…
                var said = msg["content"]?.ToString();
                if (!string.IsNullOrEmpty(said))
                    arr.Add(new JsonObject { ["role"] = "assistant", ["content"] = said });
                // …and each call becomes its own top-level item
                foreach (var call in calls)
                {
                    if (call?["function"] is not JsonObject fn) continue;
                    arr.Add(new JsonObject
                    {
                        ["type"]      = "function_call",
                        ["call_id"]   = call["id"]?.ToString() ?? "",
                        ["name"]      = fn["name"]?.ToString() ?? "",
                        ["arguments"] = fn["arguments"]?.ToString() ?? "{}",
                    });
                }
                continue;
            }

            arr.Add(msg.DeepClone());   // DeepClone: a JsonNode may only ever have one parent
        }
        return arr;
    }

    /// <summary>Build tools array for Codex Responses API — flat shape with name at top level.</summary>
    private static JsonArray BuildCodexTools(IReadOnlyList<ToolDefinition> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(JsonSerializer.Serialize(t.Parameters, Json)),
            });
        return arr;
    }

    /// <summary>Build a Gemini-native request body for Antigravity OAuth (no Cloud Code).</summary>
    private JsonObject BuildSimpleGeminiBody(JsonArray messages, string systemPrompt,
        IReadOnlyList<ToolDefinition>? withTools, string? modelOverride = null)
    {
        // ── Build the inner Gemini-native request ──
        var contents = new JsonArray();
        foreach (var node in messages)
        {
            if (node is not JsonObject msg) continue;
            var role = msg["role"]?.ToString();
            if (role == "system") continue;   // handled as systemInstruction below

            var geminiRole = role switch
            {
                "assistant" => "model",
                "tool"      => "function",
                _           => role ?? "user",
            };

            var parts = new JsonArray();

            if (role == "tool")
            {
                // OpenAI tool result → Gemini functionResponse
                var toolName = msg["name"]?.ToString() ?? "";
                var output = msg["content"]?.ToString() ?? "";
                parts.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"]     = toolName,
                        ["response"] = JsonNode.Parse(output) ?? output,
                    },
                });
            }
            else if (role == "assistant" && msg["tool_calls"] is JsonArray calls && calls.Count > 0)
            {
                // Assistant text (if any) as a text part first
                var said = msg["content"]?.ToString();
                if (!string.IsNullOrEmpty(said))
                    parts.Add(new JsonObject { ["text"] = said });
                // Each tool call as a functionCall part
                foreach (var call in calls)
                {
                    if (call?["function"] is not JsonObject fn) continue;
                    var fnName = fn["name"]?.ToString() ?? "";
                    var fnArgs = fn["arguments"]?.ToString() ?? "{}";
                    // Gemini wants args as a JSON object, not a string
                    JsonNode? argsNode = null;
                    try { argsNode = JsonNode.Parse(fnArgs); } catch { argsNode = fnArgs; }
                    parts.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["name"] = fnName,
                            ["args"]  = argsNode ?? new JsonObject(),
                        },
                        // OpenAI-format history carries no thoughtSignature; Gemini 3 400s on unsigned
                        // functionCall parts, so send Google's documented validation-skip dummy value.
                        ["thoughtSignature"] = "skip_thought_signature_validator",
                    });
                }
            }
            else
            {
                // Plain text or multimodal content
                var content = msg["content"];
                if (content is JsonArray chatParts)
                {
                    foreach (var p in chatParts)
                    {
                        if (p is not JsonObject part) continue;
                        if (part["type"]?.ToString() == "text")
                            parts.Add(new JsonObject { ["text"] = part["text"]?.ToString() ?? "" });
                        else if (part["type"]?.ToString() == "image_url")
                        {
                            var dataUrl = part["image_url"]?["url"]?.ToString() ?? "";
                            var (mime, b64) = SplitDataUrl(dataUrl);
                            parts.Add(new JsonObject
                            {
                                ["inlineData"] = new JsonObject
                                {
                                    ["mimeType"] = mime,
                                    ["data"]     = b64,
                                },
                            });
                        }
                    }
                }
                else
                {
                    parts.Add(new JsonObject { ["text"] = content?.ToString() ?? "" });
                }
            }

            contents.Add(new JsonObject { ["role"] = geminiRole, ["parts"] = parts });
        }

        var body = new JsonObject
        {
            ["contents"]          = contents,
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } } },
        };

        // ── generationConfig ──
        var genConfig = new JsonObject();
        if (_profile.Temperature.HasValue) genConfig["temperature"] = _profile.Temperature.Value;
        if (_profile.TopP.HasValue)        genConfig["topP"]        = _profile.TopP.Value;
        // As in the other builders: an unset cap is the PROVIDER's cap, not "no cap".
        genConfig["maxOutputTokens"] = OutputCap(32000);
        if (genConfig.Count > 0) body["generationConfig"] = genConfig;

        // ── tools (Gemini-native shape: [{functionDeclarations:[{name,description,parameters}]}]) ──
        if (withTools is { Count: > 0 })
        {
            var fds = new JsonArray();
            foreach (var t in withTools)
                fds.Add(new JsonObject
                {
                    ["name"]        = t.Name,
                    ["description"] = t.Description,
                    ["parameters"]  = JsonNode.Parse(JsonSerializer.Serialize(t.Parameters, Json)),
                });
            body["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = fds } };
        }

        // Include model in the inner request
        body["model"] = modelOverride ?? _model;

        return body;
    }

    /// <summary>Split a data: URL into (mimeType, base64).</summary>
    private static (string mime, string b64) SplitDataUrl(string dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl)) return ("image/png", "");
        const string prefix = "data:";
        if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return ("image/png", dataUrl);
        var rest = dataUrl[prefix.Length..];
        var semi = rest.IndexOf(';');
        var mime = semi >= 0 ? rest[..semi] : "image/png";
        var comma = rest.IndexOf(',');
        var b64 = comma >= 0 ? rest[(comma + 1)..] : rest;
        return (mime.Length > 0 ? mime : "image/png", b64);
    }

    /// <summary>Ensure at least one message mentions "json" (case-insensitive) — OpenAI's hard requirement</summary>
    private static void EnsureJsonMentioned(JsonArray messages)
    {
        foreach (var m in messages)
        {
            var content = (m as JsonObject)?["content"];
            var text = content switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray parts => string.Join(" ", parts.Select(p => (p as JsonObject)?["text"]?.ToString() ?? "")),
                _ => content?.ToString() ?? "",
            };
            if (text.Contains("json", StringComparison.OrdinalIgnoreCase)) return;   // already satisfied
        }

        const string hint = "\n\nRespond with valid JSON only.";
        var first = messages.FirstOrDefault(m => (m as JsonObject)?["role"]?.ToString() == "system") as JsonObject;
        if (first is not null && first["content"] is JsonValue sv && sv.TryGetValue<string>(out var sys))
            first["content"] = sys + hint;
        else
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = "Respond with valid JSON only." });
    }

    /// <summary>Providers whose chat-completions API accepts OpenAI-style
    /// <c>response_format: {type:"json_object"}</c>. Gemini (responseMimeType), Cohere, and Ollama
    /// use a different shape, so they're excluded — JsonMode is a no-op there and the tolerant
    /// parser remains the guarantee.</summary>
    private static bool SupportsJsonResponseFormat(string provider) => provider?.ToLowerInvariant() switch
    {
        "openai" or "deepseek" or "grok" or "xai" or "mistral" or "perplexity" or "together" or "groq" => true,
        _ => false,
    };

    private static JsonArray BuildMessages(string systemPrompt, IReadOnlyList<ConversationMessage> history)
    {
        var safe = SanitizeHistory(history.ToList());
        var arr = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };
        foreach (var msg in safe)
        {
            if (!HasMessageContent(msg)) continue;
            switch (msg.Role)
            {
                case MessageRole.User:
                    if (!string.IsNullOrEmpty(msg.ImageDataUrl))
                        arr.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = msg.Text },
                            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = msg.ImageDataUrl } },
                        } });
                    else
                        arr.Add(new JsonObject { ["role"] = "user", ["content"] = msg.Text });
                    break;
                case MessageRole.Assistant:
                    var asst = new JsonObject { ["role"] = "assistant" };
                    if (!string.IsNullOrEmpty(msg.Text)) asst["content"] = msg.Text;
                    if (msg.ToolCalls.Count > 0)
                    {
                        var tcs = new JsonArray();
                        foreach (var tc in msg.ToolCalls)
                            tcs.Add(new JsonObject
                            {
                                ["id"] = string.IsNullOrEmpty(tc.Id) ? Guid.NewGuid().ToString("N")[..8] : tc.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = tc.ArgsJson },
                            });
                        asst["tool_calls"] = tcs;
                    }
                    arr.Add(asst);
                    break;
                case MessageRole.Tool:
                    foreach (var tr in msg.ToolResults)
                        arr.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = string.IsNullOrEmpty(tr.ToolCallId) ? Guid.NewGuid().ToString("N")[..8] : tr.ToolCallId,
                            ["name"] = tr.ToolName, ["content"] = tr.Output,
                        });
                    // A tool that returned a screenshot (the computer tool) - OpenAI can't carry an image inside a
                    // role:"tool" message, so deliver it as a following user image message. Without this the model is
                    // BLIND to the screen it just captured and will hallucinate what it "sees". The Responses/Codex
                    // path (ToCodexInput) converts this user image_url into an input_image, so both APIs get it.
                    foreach (var tr in msg.ToolResults)
                    {
                        var oaiImgs = tr.ImageDataUrls is { Count: > 0 } ? tr.ImageDataUrls : (tr.ScreenshotDataUrl != null ? [tr.ScreenshotDataUrl] : null);
                        if (oaiImgs != null)
                        {
                            var content = new JsonArray();
                            content.Add(new JsonObject { ["type"] = "text", ["text"] = (oaiImgs.Count == 1 ? "Image" : $"{oaiImgs.Count} pages") + " returned by " + tr.ToolName + ":" });
                            foreach (var imgUrl in oaiImgs)
                                content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imgUrl } });
                            arr.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                        }
                    }
                    break;
            }
        }
        return arr;
    }

    private static bool HasMessageContent(ConversationMessage msg) => msg.Role switch
    {
        MessageRole.User      => !string.IsNullOrWhiteSpace(msg.Text),
        MessageRole.Assistant => !string.IsNullOrWhiteSpace(msg.Text) || msg.ToolCalls.Count > 0,
        MessageRole.Tool      => msg.ToolResults.Count > 0,
        _                     => false,
    };

    private static JsonArray BuildTools(IReadOnlyList<ToolDefinition> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name, ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(JsonSerializer.Serialize(t.Parameters, Json)),
                },
            });
        return arr;
    }

    private static bool LooksLikeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int i = 0; while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length) return false;
        char open = s[i];
        if (open != '{' && open != '[') return false;
        int j = s.Length - 1; while (j > i && char.IsWhiteSpace(s[j])) j--;
        if (j <= i) return false;
        char close = open == '{' ? '}' : ']';
        if (s[j] != close) return false;
        int k = i + 1; while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
        if (k >= s.Length) return false;
        char a = s[k];
        return a is '{' or '[' or '"' or '-' or 't' or 'f' or 'n' || (a >= '0' && a <= '9') || a == close;
    }

    private LlmResponse ParseResponse(string raw)
    {
        if (!LooksLikeJson(raw))
            throw new InvalidOperationException($"LLM returned non-JSON body ({raw.Length} chars). First 200: {raw[..Math.Min(200, raw.Length)]}");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException ex)
        { throw new InvalidOperationException($"LLM body parse failed: {ex.Message}. First 200: {raw[..Math.Min(200, raw.Length)]}"); }
        using var _ = doc;

        var root = doc.RootElement;

        // Gemini's OpenAI-compat endpoint sometimes wraps the completion in a 1-element array
        if (root.ValueKind == JsonValueKind.Array)
        {
            var arr = root.EnumerateArray().ToList();
            if (arr.Count == 0) throw new InvalidOperationException("LLM returned an empty array response.");
            root = arr[0];
        }

        // Gemini native format (candidates → content → parts) — returned when authenticating with
        // OAuth Bearer tokens instead of API keys on generativelanguage.googleapis.com.
        if (root.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array
            && cands.GetArrayLength() > 0)
            return ParseGeminiNative(root, cands);

        // OpenAI-compatible format. The 'message' field may be an Object {role,content}
        // or — when Gemini serves OAuth — an Array of parts [{text|functionCall},…].
        var firstChoice = root.GetProperty("choices")[0];
        string? text = null; var toolCalls = new List<LlmToolCall>(); string? finishReason = null;
        if (firstChoice.TryGetProperty("finish_reason", out var fr)) finishReason = fr.GetString();

        if (firstChoice.TryGetProperty("message", out var msgRaw))
        {
            if (msgRaw.ValueKind == JsonValueKind.Array)
            {
                // Gemini-OAuth: message is a parts array directly. thoughtSignature can hang off the SAME part
                // as functionCall or a preceding thought part; carry the last-seen one forward - the API 400s
                // ("Function call is missing a thought_signature") if we don't echo it back next turn.
                string? lastSig = null;
                foreach (var part in msgRaw.EnumerateArray())
                {
                    if (part.TryGetProperty("thoughtSignature", out var tsg) && tsg.ValueKind == JsonValueKind.String)
                        lastSig = tsg.GetString();
                    if (part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                        text = (text ?? "") + partText.GetString();
                    else if (part.TryGetProperty("functionCall", out var pfc))
                    {
                        var name = pfc.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
                        var args = pfc.TryGetProperty("arguments", out var pa) ? pa.GetString() ?? "{}" : "{}";
                        toolCalls.Add(new LlmToolCall { Id = "call_" + Guid.NewGuid().ToString("N"), Name = name, ArgsJson = args, ThoughtSignature = lastSig });
                        lastSig = null;   // consumed by this call
                    }
                }
            }
            else if (msgRaw.ValueKind == JsonValueKind.Object)
            {
                var choice = msgRaw;
                if (choice.TryGetProperty("content", out var c) && c.ValueKind != JsonValueKind.Null)
                {
                    text = c.ValueKind == JsonValueKind.Array
                        ? string.Concat(c.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : null))
                        : c.GetString();
                }
                if (choice.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var id = tc.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
                        var fn = tc.GetProperty("function");
                        toolCalls.Add(new LlmToolCall
                        {
                            Id = id, Name = fn.GetProperty("name").GetString() ?? "",
                            ArgsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}",
                        });
                    }
            }
        }
        int? pt = null, ctok = null, tt = null, cached = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens",     out var x) && x.ValueKind == JsonValueKind.Number) pt   = x.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var y) && y.ValueKind == JsonValueKind.Number) ctok = y.GetInt32();
            if (usage.TryGetProperty("total_tokens",      out var z) && z.ValueKind == JsonValueKind.Number) tt   = z.GetInt32();
            if (usage.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object
                && ptd.TryGetProperty("cached_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                cached = ct.GetInt32();
        }
        return new LlmResponse { Text = text, ToolCalls = toolCalls, FinishReason = finishReason,
            PromptTokens = pt, CompletionTokens = ctok, TotalTokens = tt, CachedTokens = cached };
    }

    /// <summary>Parse the Gemini NATIVE response shape (candidates→content→parts) into the canonical
    /// <see cref="LlmResponse"/>. Gemini returns this when authenticated with OAuth Bearer tokens instead
    /// of API keys on the OpenAI-compatible endpoint. The shape is:
    /// { candidates: [{ content: { role, parts: [{text|functionCall},…] }, finishReason }], usageMetadata }</summary>
    private static LlmResponse ParseGeminiNative(JsonElement root, JsonElement candidates)
    {
        var text = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();
        string? finishReason = null;
        foreach (var cand in candidates.EnumerateArray())
        {
            if (cand.TryGetProperty("finishReason", out var fr)) finishReason = fr.GetString();
            if (cand.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                // Same thought_signature carry-forward as the streaming parser: Gemini attaches an opaque
                // signature to the part (or a preceding thought part) and REQUIRES it echoed back on the next
                // request; missing it triggers HTTP 400 "Function call is missing a thought_signature".
                string? lastSig = null;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("thoughtSignature", out var tsg) && tsg.ValueKind == JsonValueKind.String)
                        lastSig = tsg.GetString();
                    if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        text.Append(t.GetString());
                    else if (part.TryGetProperty("functionCall", out var fc))
                    {
                        var name = fc.TryGetProperty("name", out var fn) ? fn.GetString() ?? "" : "";
                        var args = "{}";
                        if (fc.TryGetProperty("args", out var fa))
                            args = fa.ValueKind == JsonValueKind.Object ? fa.GetRawText()
                                 : fa.ValueKind == JsonValueKind.String ? fa.GetString() ?? "{}" : "{}";
                        toolCalls.Add(new LlmToolCall { Id = "call_" + Guid.NewGuid().ToString("N"), Name = name, ArgsJson = args, ThoughtSignature = lastSig });
                        lastSig = null;   // consumed by this call
                    }
                }
            }
        }
        int? pt = null, ctok = null, tt = null, reasoning = null, cached = null, toolUsePrompt = null;
        if (root.TryGetProperty("usageMetadata", out var um))
        {
            if (um.TryGetProperty("promptTokenCount",     out var a) && a.TryGetInt32(out var av)) pt   = av;
            int candidateCount = 0;
            if (um.TryGetProperty("candidatesTokenCount", out var c) && c.TryGetInt32(out var cv)) candidateCount = cv;
            if (um.TryGetProperty("thoughtsTokenCount",   out var r) && r.TryGetInt32(out var rv)) reasoning = rv;
            if (um.TryGetProperty("totalTokenCount",      out var d) && d.TryGetInt32(out var dv)) tt   = dv;
            if (um.TryGetProperty("cachedContentTokenCount", out var cc) && cc.TryGetInt32(out var ccv)) cached = ccv;
            if (um.TryGetProperty("toolUsePromptTokenCount", out var tu) && tu.TryGetInt32(out var tuv)) toolUsePrompt = tuv;
            pt = InputTokenCount(pt ?? 0, toolUsePrompt ?? 0);
            ctok = GeneratedTokenCount(pt ?? 0, candidateCount, reasoning ?? 0, tt ?? 0);
        }
        return new LlmResponse { Text = text.ToString(), ToolCalls = toolCalls, FinishReason = finishReason,
            PromptTokens = pt, CompletionTokens = ctok, ReasoningTokens = reasoning, ToolUsePromptTokens = toolUsePrompt,
            TotalTokens = tt, CachedTokens = cached };
    }

    /// <summary>Extract text, TOOL CALLS and usage from a Codex SSE stream, re-shaped into the
    /// chat-completions JSON <see cref="ParseResponse"/> understands. The Responses stream carries each
    /// piece as its own event (verified on the live backend):
    ///   response.output_text.delta   → answer text, one delta at a time
    ///   response.output_item.done    → a finished item; item.type "function_call" holds call_id/name/arguments
    ///   response.completed           → usage. Its .output array is EMPTY when store=false, so tool calls
    ///                                  must be collected from output_item.done and never from here.
    /// A tool-calling turn emits NO text deltas at all: dropping the function_call items therefore handed the
    /// caller an empty message with zero tools, the tool loop ended on the spot, and the model was forced to
    /// answer "I don't have that data" about a search it had in fact just requested.</summary>
    private static string ExtractCodexSsePayload(string sseStream)
    {
        var sb = new StringBuilder();
        var toolCalls = new JsonArray();
        int? inputTokens = null, outputTokens = null, totalTokens = null, cachedTokens = null;

        foreach (var line in sseStream.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data: ") || trimmed.Length <= 6) continue;
            var json = trimmed[6..];
            if (json == "[DONE]") continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";

                if (type == "response.output_text.delta" &&
                    root.TryGetProperty("delta", out var delta))
                    sb.Append(delta.GetString());
                else if (type == "response.output_item.done" &&
                         root.TryGetProperty("item", out var item) &&
                         item.TryGetProperty("type", out var itemType) && itemType.GetString() == "function_call")
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    toolCalls.Add(new JsonObject
                    {
                        ["id"]       = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "",
                        ["type"]     = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"]      = name,
                            ["arguments"] = item.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}",
                        },
                    });
                }
                else if (type == "response.completed" &&
                         root.TryGetProperty("response", out var resp) &&
                         resp.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("input_tokens",  out var i) && i.ValueKind == JsonValueKind.Number) inputTokens  = i.GetInt32();
                    if (usage.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number) outputTokens = o.GetInt32();
                    if (usage.TryGetProperty("total_tokens",  out var tl) && tl.ValueKind == JsonValueKind.Number) totalTokens  = tl.GetInt32();
                    // Responses API reports cache credits as input_tokens_details.cached_tokens — carry it
                    // through the re-shape or the usage log shows 0 cached regardless of what the backend did.
                    if (usage.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object
                        && itd.TryGetProperty("cached_tokens", out var cch) && cch.ValueKind == JsonValueKind.Number)
                        cachedTokens = cch.GetInt32();
                }
            }
            catch { /* skip unparseable lines */ }
        }

        // Synthesize a response JSON the standard parser can consume
        var text = sb.ToString();
        var message = new JsonObject { ["role"] = "assistant" };
        message["content"] = text.Length > 0 ? JsonValue.Create(text) : null;
        if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;

        var payload = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"]       = message,
                    ["finish_reason"] = toolCalls.Count > 0 ? "tool_calls" : "stop",
                },
            },
        };
        if (inputTokens.HasValue || outputTokens.HasValue)
        {
            var usageObj = new JsonObject
            {
                ["prompt_tokens"]     = inputTokens ?? 0,
                ["completion_tokens"] = outputTokens ?? 0,
                ["total_tokens"]      = totalTokens ?? 0,
            };
            if (cachedTokens.HasValue)
                usageObj["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cachedTokens.Value };
            payload["usage"] = usageObj;
        }
        return payload.ToJsonString();
    }

    /// <summary>Cloud Code SSE response: extract the LAST Gemini-native JSON payload from the SSE stream.
    /// Each line is <c>data: {"candidates":[...],"usageMetadata":{...}}</c> — cumulative snapshots.
    /// The last one has the complete response including usage metadata.</summary>
    private static string ExtractCloudCodeSsePayload(string sseStream)
    {
        string? last = null;
        foreach (var line in sseStream.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("data: ") && trimmed.Length > 6)
            {
                var json = trimmed[6..];
                if (json != "[DONE]") last = json;
            }
        }
        return last ?? sseStream;  // fallback: return raw if no data lines found
    }

    /// <summary>Try to extract an error message from a failed Codex SSE stream.</summary>
    private static string TryParseCodexStreamError(string sseStream)
    {
        foreach (var line in sseStream.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("data: ") && trimmed.Contains("\"type\":\"error\""))
                return trimmed[6..];
        }
        // If it's not SSE, it might be a plain JSON error
        if (sseStream.TrimStart().StartsWith("{")) return sseStream;
        return sseStream;
    }

    // Public: the editor's model-catalog endpoint ("fetch models" in the profile modal) reuses this map.
    public static string ProviderBaseUrl(string provider) => provider.ToLowerInvariant() switch
    {
        "openai"      => "https://api.openai.com",
        "anthropic"   => "https://api.anthropic.com",   // Messages API (v1/messages), see AnthropicCallAsync
        "groq"        => "https://api.groq.com/openai",
        "openrouter"  => "https://openrouter.ai/api",
        "together"    => "https://api.together.xyz",
        "deepseek"    => "https://api.deepseek.com",
        "grok"        => "https://api.x.ai",
        "mistral"     => "https://api.mistral.ai",
        "perplexity"  => "https://api.perplexity.ai",
        "cohere"      => "https://api.cohere.ai/compatibility",
        "gemini"      => "https://generativelanguage.googleapis.com",
        "antigravity" => "https://generativelanguage.googleapis.com",   // Google Antigravity — OAuth-backed, same Gemini API backend
        "nvidia"      => "https://integrate.api.nvidia.com",   // OpenAI-compatible; path stays v1/chat/completions
        "alibaba"     => "https://dashscope-intl.aliyuncs.com/compatible-mode", // intl endpoint — sk-ws keys only work here; mainland keys need the Base URL field
        "oneprovider" => "https://api.oneprovider.dev",   // OpenAI-compatible gateway (Claude/GPT/… under one key); path stays v1/chat/completions
        _             => "http://localhost:11434",
    };

    private static string ProviderChatPath(string provider) => provider.ToLowerInvariant() switch
    {
        "gemini"      => "v1beta/openai/chat/completions",
        "antigravity" => "v1beta/openai/chat/completions",   // same OpenAI-compatible Gemini path
        "anthropic"   => "v1/messages",
        _             => "v1/chat/completions",
    };
}

/// <summary>Convenience wrapper to call one specific profile directly (bypass routing).</summary>
public sealed class OpenAiLlmClient : ILlmClient
{
    private readonly SingleCallLlmClient _inner;
    public OpenAiLlmClient(HttpClient http, AiProfile profile)
        => _inner = new SingleCallLlmClient(http, profile,
            profile.ApiKeys.FirstOrDefault() ?? "", profile.Models.FirstOrDefault() ?? "gpt-4o-mini");

    public Task<LlmResponse> CallAsync(string systemPrompt, IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        => _inner.CallAsync(systemPrompt, history, tools, ct);

    // A single OpenAI client has no layer routing - forward to CallAsync, matching the old default interface method.
    public Task<LlmResponse> CallWithLayerAsync(string layer, string systemPrompt, IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken ct, long userId = 0)
        => _inner.CallAsync(systemPrompt, history, tools, ct);
}
