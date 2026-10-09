using System.Collections.Concurrent;
using System.Text.Json;


namespace VanityStudio.Llm;

/// <summary>
/// Keeps OAuth (subscription-auth) profiles alive so they route EXACTLY like API-key profiles: the router
/// calls <see cref="EnsureFreshAsync"/> before each candidate attempt, and an expiring access token is
/// silently renewed with the stored refresh token (RFC 6749 §6). Without this, an OAuth profile worked only
/// until its first expiry and then failed every call — which made it look excluded from layer routing and
/// fallback when it was really just carrying a dead credential.
///
/// Supported providers: OpenAI (Codex), Grok (xAI), Antigravity (Google OAuth).
/// </summary>
public static class OAuthTokenRefresher
{
    // Client ids / token endpoints mirror OAuthConfig in OrchestratorDbEditor/Api/UserSettingsApi.cs — the
    // sign-in flow lives in the editor, the refresh has to run wherever the router runs (engine + editor).
    internal const string OpenAIClientId  = "app_EMoamEEZ73f0CkXaXp7hrann";
    internal const string OpenAITokenUrl  = "https://auth.openai.com/oauth/token";
    internal const string GrokClientId    = "b1a00492-073a-47ea-816f-4c329264a828";
    internal const string GrokTokenUrl    = "https://auth.x.ai/oauth2/token";
    internal const string GoogleTokenUrl  = "https://oauth2.googleapis.com/token";
    // Antigravity's OWN Google OAuth client. Installed native-app client: the token exchange and the refresh try the
    // public PKCE form first (no secret). The installed-app secrets that the Antigravity binary ships are NOT in this
    // source; when Google refuses the public form, set them in the environment: VANITY_STUDIO_GOOGLE_SECRET,
    // VANITY_STUDIO_GOOGLE_SECRET_ALT (the Antigravity client) and VANITY_STUDIO_GOOGLE_APP_SECRET (the desktop-app client).
    internal const string GoogleClientId        = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    internal static string GoogleClientSecret    => Environment.GetEnvironmentVariable("VANITY_STUDIO_GOOGLE_SECRET") ?? VanityStudio.Infra.AgentConfig.Setting("GoogleClientSecret") ?? "";
    internal static string GoogleClientSecretAlt => Environment.GetEnvironmentVariable("VANITY_STUDIO_GOOGLE_SECRET_ALT") ?? VanityStudio.Infra.AgentConfig.Setting("GoogleClientSecretAlt") ?? "";
    // The Antigravity DESKTOP APP's own Google client (Pro tier/catalog). A profile chooses it with OAuthClient="app".
    internal const string AppClientId           = "884354919052-36trc1jjb3tguiac32ov6cod268c5blh.apps.googleusercontent.com";
    internal static string AppClientSecret       => Environment.GetEnvironmentVariable("VANITY_STUDIO_GOOGLE_APP_SECRET") ?? VanityStudio.Infra.AgentConfig.Setting("GoogleAppClientSecret") ?? "";
    private static bool  IsAppClient(AiProfile p) => string.Equals(p.OAuthClient, "app", StringComparison.OrdinalIgnoreCase);

    /// <summary>Refresh only when the token dies within this window — covers request latency + clock skew.</summary>
    private const int ExpiryMarginSeconds = 120;

    /// <summary>Set by the composition root to write refreshed tokens back to the profile's DB row
    /// (userId, profile-with-fresh-tokens). Never required — an unwired refresher still refreshes in
    /// memory, the tokens just don't survive a restart.</summary>
    public static Action<long, AiProfile>? Persist { get; set; }

    /// <summary>Set by the composition root to re-read the LATEST OAuth token fields for (userId, profileName)
    /// from the DB. Critical for providers whose refresh tokens are SINGLE-USE + ROTATING (xAI/Grok, OpenAI):
    /// concurrent callers each hold their OWN profile snapshot, so once one rotates the token every other snapshot
    /// carries the now-REVOKED one. Reloading under the lock lets a caller adopt whatever a concurrent caller just
    /// rotated instead of refreshing with a dead credential ("invalid_grant: refresh token has been revoked").
    /// Unwired → the refresher still works single-threaded, it just isn't concurrency-safe.</summary>
    public static Func<long, string, AiProfile?>? Reload { get; set; }

    // One refresh at a time per (user, profile) — parallel router calls must not race the token endpoint.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>Profile-level mirror of the client's auth rule: a stored token only counts when NO real API
    /// key is configured (the 'oauth' sentinel / blank means "authenticate with the subscription").</summary>
    public static bool UsesOAuth(AiProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.OAuthProvider) || string.IsNullOrWhiteSpace(p.OAuthAccessToken)) return false;
        return !p.ApiKeys.Any(k =>
        {
            var s = (k ?? "").Trim().Trim('\'', '"');
            return s.Length > 0 && !s.Equals("oauth", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Make sure <paramref name="p"/>'s access token is usable, refreshing it in place (and
    /// persisting) when it is expired or about to expire. Returns false ONLY when the profile is expired
    /// AND could not be refreshed — the router treats that candidate as failed and moves on, exactly like
    /// a dead API key. Non-OAuth profiles always return true. <paramref name="force"/> refreshes regardless
    /// of the stored expiry (for a 401 on a token the server revoked early).</summary>
    public static async Task<bool> EnsureFreshAsync(AiProfile p, long userId, bool force = false, CancellationToken ct = default)
    {
        if (!UsesOAuth(p)) return true;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // ExpiresAt 0 = unknown (older rows) — assume alive and let the 401 path force-refresh if not.
        bool expiring = p.OAuthExpiresAt > 0 && p.OAuthExpiresAt <= now + ExpiryMarginSeconds;
        if (!force && !expiring) return true;
        if (string.IsNullOrWhiteSpace(p.OAuthRefreshToken))
            return !force && p.OAuthExpiresAt > now;   // nothing to refresh with — usable only if not actually dead

        var gate = _locks.GetOrAdd($"{userId}/{p.Name}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // ADOPT THE LATEST TOKEN FIRST. Refresh tokens here are SINGLE-USE + ROTATING (proven live against xAI:
            // a second refresh with the prior token returns "invalid_grant: refresh token has been revoked"). Another
            // caller — this run, a parallel workflow, the editor — may have rotated the token while we waited on the
            // lock, leaving OUR snapshot holding a revoked one. Re-read the DB and take whatever was last persisted so
            // we refresh with the LIVE token (or discover it's already fresh and skip the refresh entirely).
            var hadAccess = p.OAuthAccessToken;
            if (Reload?.Invoke(userId, p.Name) is { } latest && !string.IsNullOrWhiteSpace(latest.OAuthRefreshToken))
            {
                if (!string.IsNullOrWhiteSpace(latest.OAuthAccessToken))  p.OAuthAccessToken  = latest.OAuthAccessToken;
                p.OAuthRefreshToken = latest.OAuthRefreshToken;
                if (latest.OAuthExpiresAt > 0)                            p.OAuthExpiresAt    = latest.OAuthExpiresAt;
                if (!string.IsNullOrWhiteSpace(latest.OAuthAccountId))    p.OAuthAccountId    = latest.OAuthAccountId;
            }

            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool dbFresh = p.OAuthExpiresAt == 0 || p.OAuthExpiresAt > now + ExpiryMarginSeconds;
            // A concurrent caller already refreshed → adopt-and-continue instead of burning the (now-revoked) token
            // again. For a force (401) retry we only skip re-refreshing when the DB token BOTH looks fresh AND actually
            // changed from the one the 401 came back on — otherwise we'd loop on a token the server just rejected.
            if (dbFresh && (!force || !string.Equals(hadAccess, p.OAuthAccessToken, StringComparison.Ordinal)))
                return true;
            return await RefreshAsync(p, userId, ct).ConfigureAwait(false)
                || (!force && p.OAuthExpiresAt > now);
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> RefreshAsync(AiProfile p, long userId, CancellationToken ct)
    {
        string tokenUrl;
        var form = new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = p.OAuthRefreshToken!,
        };
        switch ((p.OAuthProvider ?? "").ToLowerInvariant())
        {
            case "openai":
                tokenUrl = OpenAITokenUrl; form["client_id"] = OpenAIClientId; form["scope"] = "openid profile email";
                break;
            case "grok":
                tokenUrl = GrokTokenUrl; form["client_id"] = GrokClientId;
                break;
            case "antigravity":
                // Antigravity's own Google client. Installed native-app client → the secret is tried in the send
                // loop below (PKCE-only first, then each shipped secret). client_id only here. (Generation runs on
                // Code Assist with pluginType CLOUD_CODE @ daily-cloudcode-pa - see OpenAiLlmClient.CodeAssist*.)
                tokenUrl = GoogleTokenUrl;
                form["client_id"] = IsAppClient(p) ? AppClientId : GoogleClientId;
                break;
            default:
                // Anthropic setup-token has no refresh flow (paste-token); unknown providers likewise.
                return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var isAg = string.Equals(p.OAuthProvider, "antigravity", StringComparison.OrdinalIgnoreCase);

            async Task<(bool ok, string body)> Send()
            {
                // Antigravity: PKCE-only then each secret; other providers: one shot as before.
                var secrets = isAg
                    ? (IsAppClient(p) ? new[] { "", AppClientSecret } : new[] { "", GoogleClientSecret, GoogleClientSecretAlt })
                    : new[] { (string)null! };
                var last = "";
                foreach (var secret in secrets)
                {
                    var f = new Dictionary<string, string>(form);
                    if (isAg && secret.Length > 0) f["client_secret"] = secret;
                    using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(f) };
                    if (p.OAuthProvider == "grok")
                    {
                        req.Headers.Add("x-grok-client-version", "1.0.0");
                        req.Headers.Add("x-grok-client-surface", "Cli");
                    }
                    using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                    last = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) return (true, last);
                    if (!isAg || (!last.Contains("invalid_client") && !last.Contains("client_secret") && !last.Contains("unauthorized_client")))
                        return (false, last);
                }
                return (false, last);
            }

            var (ok, body) = await Send();
            if (!ok)
            {
                Console.WriteLine($"[llm-oauth] refresh failed for '{p.Name}': {body[..Math.Min(200, body.Length)]}");
                if (body.Contains("client_secret", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine("[llm-oauth] Google needs the Antigravity client secret to refresh this login. Store it once: /set GoogleClientSecret (or the VANITY_STUDIO_GOOGLE_SECRET environment variable), then try again.");
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                Console.WriteLine($"[llm-oauth] refresh for '{p.Name}' returned no access_token");
                return false;
            }
            long expiresIn = 3600;
            if (root.TryGetProperty("expires_in", out var ei))
            {
                if (ei.ValueKind == JsonValueKind.Number && ei.TryGetInt64(out var n)) expiresIn = n;
                else if (ei.ValueKind == JsonValueKind.String && long.TryParse(ei.GetString(), out var s)) expiresIn = s;
            }

            p.OAuthAccessToken = accessToken;
            p.OAuthExpiresAt   = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;
            // Providers may rotate the refresh token; keep the old one when they don't.
            if (root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(rt.GetString()))
                p.OAuthRefreshToken = rt.GetString();
            // The Codex backend's ChatGPT-Account-Id header comes from here — backfill it if it was never stored.
            if (string.IsNullOrWhiteSpace(p.OAuthAccountId))
                p.OAuthAccountId = ExtractAccountId(accessToken);

            try { Persist?.Invoke(userId, p); }
            catch (Exception ex) { Console.WriteLine($"[llm-oauth] refreshed '{p.Name}' but persist failed: {ex.Message}"); }

            Console.WriteLine($"[llm-oauth] refreshed '{p.Name}' ({p.OAuthProvider}) — next expiry in {expiresIn}s");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[llm-oauth] refresh failed for '{p.Name}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Account id from the access-token JWT (chatgpt_account_id / account_id / sub / email claim).
    /// Google OAuth tokens carry an "email" claim that's the natural account identifier.</summary>
    private static string? ExtractAccountId(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            while (payload.Length % 4 != 0) payload += "=";
            using var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var r = doc.RootElement;
            if (r.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object
                && auth.TryGetProperty("chatgpt_account_id", out var ca) && ca.ValueKind == JsonValueKind.String) return ca.GetString();
            if (r.TryGetProperty("account_id", out var ai) && ai.ValueKind == JsonValueKind.String) return ai.GetString();
            if (r.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String) return em.GetString();
            if (r.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String) return sub.GetString();
            return null;
        }
        catch { return null; }
    }
}
