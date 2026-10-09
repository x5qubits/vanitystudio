using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using VanityStudio.Infra;
using VanityStudio.Llm;

namespace VanityStudio.Auth;

/// <summary>Endpoints for the subscription logins. The client ids are the public ones the providers' own CLIs use
/// (OpenAI Codex CLI, grok-build, Google Antigravity) and live in <see cref="OAuthTokenRefresher"/>, which needs
/// them for the refresh too; this class only adds the sign-in endpoints.</summary>
public static class OAuthConfig
{
    // OpenAI Codex (ChatGPT subscription) - device-code flow.
    public const string OpenAIDeviceCodeUrl  = "https://auth.openai.com/api/accounts/deviceauth/usercode";
    public const string OpenAIDevicePollUrl  = "https://auth.openai.com/api/accounts/deviceauth/token";
    public const string OpenAIVerifyUrl      = "https://auth.openai.com/codex/device";
    public const string OpenAIDeviceRedirect = "https://auth.openai.com/deviceauth/callback";

    // Grok (xAI) - RFC 8628 device-code flow.
    public const string GrokScopes        = "openid profile email offline_access grok-cli:access api:access conversations:read conversations:write";
    public const string GrokDeviceCodeUrl = "https://auth.x.ai/oauth2/device/code";
    public const string GrokVerifyUrl     = "https://accounts.x.ai/oauth2/device";

    // Antigravity (Google) - authorization code + PKCE on a loopback redirect.
    public const string GoogleAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string GoogleScopes  = "openid https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/cclog https://www.googleapis.com/auth/experimentsandconfigs https://www.googleapis.com/auth/aicode";

    public static bool     IsApp(string? client)       => string.Equals(client, "app", StringComparison.OrdinalIgnoreCase);
    public static string   ClientIdFor(string? client) => IsApp(client) ? OAuthTokenRefresher.AppClientId : OAuthTokenRefresher.GoogleClientId;
    /// <summary>The token exchange tries the public (PKCE-only) form first, then any installed-app secret the operator
    /// set in the environment (see OAuthTokenRefresher); none are shipped in this source.</summary>
    public static string[] SecretsFor(string? client)  => (IsApp(client)
        ? new[] { "", OAuthTokenRefresher.AppClientSecret }
        : new[] { "", OAuthTokenRefresher.GoogleClientSecret, OAuthTokenRefresher.GoogleClientSecretAlt })
        .Where((s, i) => i == 0 || s.Length > 0).Distinct().ToArray();

    /// <summary>Default models per login, so a fresh profile is usable at once; `/model` changes it.</summary>
    public static string[] DefaultModels(string provider) => provider.ToLowerInvariant() switch
    {
        "openai"      => ["gpt-5.5", "gpt-5.5-mini"],
        "grok"        => ["grok-4.5"],
        // Flash with high thinking first (Antigravity's own default agent model): flash-low wrote scripts with guessed
        // blocks and empty submits (2026-10-09); pro waits tens of seconds in the queue before its first token. A name
        // the tier does not serve is jailed by the router and the next one is tried, so the list degrades by itself.
        "antigravity" => ["gemini-3.8-flash-high", "gemini-3.8-flash-medium", "gemini-3.8-flash-low", "gemini-3.1-pro-high"],
        "anthropic"   => ["claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-4-5-20251001"],
        _             => [],
    };
}

/// <summary>What a login needs from the console: a line out, and a line in.</summary>
public sealed class LoginUi
{
    public Action<string> Say { get; init; } = Console.WriteLine;
    public Action<string> Emphasis { get; init; } = Console.WriteLine;
    public Func<string, CancellationToken, Task<string?>> Ask { get; init; } = (prompt, _) => { Console.Write(prompt); return Task.FromResult(Console.ReadLine()); };
}

/// <summary>The interactive sign-in flows, ported from the editor's AI-profiles API into the console. Each returns a
/// profile carrying the tokens; the caller saves it.</summary>
public static class OAuthFlows
{
    // ── OpenAI (ChatGPT / Codex) ───────────────────────────────────────────────────────────────────────────────────

    public static async Task<AiProfile> LoginOpenAiAsync(string profileName, LoginUi ui, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var dcResp = await http.PostAsync(OAuthConfig.OpenAIDeviceCodeUrl,
            new StringContent($"{{\"client_id\":\"{OAuthTokenRefresher.OpenAIClientId}\"}}", Encoding.UTF8, "application/json"), ct);
        var dcJson = await dcResp.Content.ReadAsStringAsync(ct);
        if (!dcResp.IsSuccessStatusCode) throw new InvalidOperationException($"Device code request failed ({(int)dcResp.StatusCode}): {Head(dcJson)}");
        using var dcDoc = JsonDocument.Parse(dcJson);
        var dcRoot = dcDoc.RootElement;
        var deviceAuthId = Str(dcRoot, "device_auth_id");
        var userCode = Str(dcRoot, "user_code") ?? Str(dcRoot, "usercode");
        var intervalMs = Math.Max(2000, (int)(ReadLong(dcRoot, "interval", 5) * 1000));
        if (string.IsNullOrWhiteSpace(deviceAuthId) || string.IsNullOrWhiteSpace(userCode))
            throw new InvalidOperationException("Device code response was missing device_auth_id or user_code.");

        ui.Say("");
        ui.Say($"  1. Open  {OAuthConfig.OpenAIVerifyUrl}");
        ui.Emphasis($"  2. Enter the code  {userCode}");
        ui.Say("  3. Sign in with the ChatGPT account whose subscription the agent should use.");
        ui.Say("");
        TryOpenBrowser(OAuthConfig.OpenAIVerifyUrl);
        ui.Say("  Waiting for the approval (Ctrl+C to cancel)...");

        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(intervalMs, ct);
            using var pollResp = await http.PostAsync(OAuthConfig.OpenAIDevicePollUrl,
                new StringContent($"{{\"device_auth_id\":\"{deviceAuthId}\",\"user_code\":\"{userCode}\"}}", Encoding.UTF8, "application/json"), ct);
            var pollBody = await pollResp.Content.ReadAsStringAsync(ct);

            if (IsPending(pollBody) || pollResp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) continue;
            if (!pollResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Authorization failed ({(int)pollResp.StatusCode}): {Head(pollBody)}");

            using var pollDoc = JsonDocument.Parse(pollBody);
            var authCode = Str(pollDoc.RootElement, "authorization_code");
            var verifier = Str(pollDoc.RootElement, "code_verifier");
            if (string.IsNullOrWhiteSpace(authCode) || string.IsNullOrWhiteSpace(verifier))
                throw new InvalidOperationException("Device token response was missing authorization_code or code_verifier.");

            using var tokenResp = await http.PostAsync(OAuthTokenRefresher.OpenAITokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["code"]          = authCode,
                ["redirect_uri"]  = OAuthConfig.OpenAIDeviceRedirect,
                ["client_id"]     = OAuthTokenRefresher.OpenAIClientId,
                ["code_verifier"] = verifier,
            }), ct);
            var tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
            if (!tokenResp.IsSuccessStatusCode) throw new InvalidOperationException($"Token exchange failed ({(int)tokenResp.StatusCode}): {Head(tokenBody)}");
            return ProfileFromTokens(profileName, "OpenAI", "openai", tokenBody);
        }
        throw new TimeoutException("The device code expired before the login was approved. Run /login openai again.");
    }

    private static bool IsPending(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var err)) return false;
            string? type = err.ValueKind == JsonValueKind.Object ? (Str(err, "type") ?? Str(err, "code")) : err.ValueKind == JsonValueKind.String ? err.GetString() : null;
            return type is "authorization_pending" or "deviceauth_authorization_pending" or "slow_down";
        }
        catch { return false; }
    }

    // ── Grok (xAI) ─────────────────────────────────────────────────────────────────────────────────────────────────

    public static async Task<AiProfile> LoginGrokAsync(string profileName, LoginUi ui, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline)
        {
            var (deviceCode, userCode, verifyUrl, intervalMs) = await GrokRequestDeviceCodeAsync(http, ct);
            ui.Say("");
            ui.Say($"  1. Open  {verifyUrl}");
            ui.Emphasis($"  2. Confirm the code  {userCode}");
            ui.Say("  3. Sign in with the X / xAI account whose subscription the agent should use.");
            ui.Say("");
            TryOpenBrowser(verifyUrl);
            ui.Say("  Waiting for the approval (Ctrl+C to cancel)...");

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(intervalMs, ct);
                var pollMsg = new HttpRequestMessage(HttpMethod.Post, OAuthTokenRefresher.GrokTokenUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
                        ["device_code"] = deviceCode,
                        ["client_id"]   = OAuthTokenRefresher.GrokClientId,
                    }),
                };
                pollMsg.Headers.Add("x-grok-client-version", "1.0.0");
                pollMsg.Headers.Add("x-grok-client-surface", "Cli");
                using var pollResp = await http.SendAsync(pollMsg, ct);
                var pollBody = await pollResp.Content.ReadAsStringAsync(ct);
                if (pollResp.IsSuccessStatusCode)
                    return ProfileFromTokens(profileName, "Grok", "grok", pollBody);

                var err = ErrorCode(pollBody);
                if (err is "authorization_pending" or "slow_down") continue;
                if (err == "expired_token") { ui.Say("  The code expired; requesting a fresh one..."); break; }
                if (err == "access_denied") throw new InvalidOperationException("Authorization denied: the request was rejected.");
                throw new InvalidOperationException($"Grok authorization failed ({(int)pollResp.StatusCode}): {Head(pollBody)}");
            }
        }
        throw new TimeoutException("The login was not approved in time. Run /login grok again.");
    }

    private static async Task<(string DeviceCode, string UserCode, string VerifyUrl, int IntervalMs)> GrokRequestDeviceCodeAsync(HttpClient http, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, OAuthConfig.GrokDeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = OAuthTokenRefresher.GrokClientId,
                ["scope"]     = OAuthConfig.GrokScopes,
                ["referrer"]  = "grok-build",
            }),
        };
        req.Headers.Add("x-grok-client-version", "1.0.0");
        req.Headers.Add("x-grok-client-surface", "Ui");
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Grok device code request failed ({(int)resp.StatusCode}): {Head(body)}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var deviceCode = Str(root, "device_code");
        var userCode = Str(root, "user_code");
        var verify = Str(root, "verification_uri") ?? Str(root, "verification_uri_complete") ?? $"{OAuthConfig.GrokVerifyUrl}?user_code={userCode}";
        var interval = Math.Max(2000, (int)(ReadLong(root, "interval", 5) * 1000));
        if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode))
            throw new InvalidOperationException("Grok device code response was missing device_code or user_code.");
        return (deviceCode, userCode, verify, interval);
    }

    private static string? ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var e)) return null;
            return e.ValueKind == JsonValueKind.String ? e.GetString() : Str(e, "error") ?? Str(e, "code");
        }
        catch { return null; }
    }

    // ── Antigravity (Google) ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Google sign-in on a loopback redirect. The browser comes back to a listener on localhost; when that
    /// cannot happen (a remote shell), the operator pastes the redirected URL instead - both are accepted.</summary>
    public static async Task<AiProfile> LoginAntigravityAsync(string profileName, string client, LoginUi ui, CancellationToken ct)
    {
        var verifier  = Pkce.GenerateVerifier();
        var challenge = Pkce.ComputeChallenge(verifier);
        var state     = Guid.NewGuid().ToString("N");
        var port      = FreePort();
        var redirect  = $"http://localhost:{port}/auth/callback";
        var authUrl   = $"{OAuthConfig.GoogleAuthUrl}?client_id={Uri.EscapeDataString(OAuthConfig.ClientIdFor(client))}"
                      + $"&redirect_uri={Uri.EscapeDataString(redirect)}&response_type=code"
                      + $"&scope={Uri.EscapeDataString(OAuthConfig.GoogleScopes)}"
                      + $"&code_challenge={challenge}&code_challenge_method=S256&access_type=offline&prompt=consent&state={state}";

        ui.Say("");
        ui.Say("  1. Sign in with Google in the browser window that opens (the Antigravity consent screen).");
        ui.Say("  2. The browser returns to this machine on " + redirect + " and the login completes by itself.");
        ui.Say("     If it cannot reach this machine, paste the full redirected URL (or just the code) below.");
        ui.Say("");
        ui.Say("  " + authUrl);
        ui.Say("");
        TryOpenBrowser(authUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        var listener = ListenForCodeAsync(port, state, cts.Token);
        var pasted   = ui.Ask("  Paste the redirected URL or code here (or wait for the browser): ", cts.Token);
        var done     = await Task.WhenAny((Task)listener, pasted);
        string code;
        if (done == listener)
        {
            code = await listener;
            ui.Say("");
            ui.Say("  Browser returned the authorization code.");
        }
        else
        {
            // Something was typed before the browser came back. Only a real code (or the redirected URL) counts; a
            // stray Enter or a word typed while waiting is ignored and the login keeps waiting for the browser.
            var raw = (await pasted ?? "").Trim();
            var ix = raw.IndexOf("code=", StringComparison.Ordinal);
            var candidate = ix >= 0 ? Uri.UnescapeDataString(raw[(ix + 5)..].Split('&')[0]) : raw;
            if (candidate.Length >= 20 && !candidate.Contains(' '))
                code = candidate;
            else
            {
                if (raw.Length > 0) ui.Say("  That is not an authorization code; still waiting for the browser...");
                code = await listener;
                ui.Say("  Browser returned the authorization code.");
            }
        }
        cts.Cancel();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var (ok, body) = await GoogleExchangeCodeAsync(http, code, verifier, redirect, client, CancellationToken.None);
        if (!ok) throw new InvalidOperationException("Token exchange failed: " + Head(body));
        using var d = JsonDocument.Parse(body);
        var r = d.RootElement;
        var email = JwtClaim(Str(r, "id_token"), "email");
        var profile = new AiProfile
        {
            Name = profileName, Provider = "Antigravity", Enabled = true, DisableThinking = true, JsonMode = true,
            OAuthProvider = "antigravity", OAuthClient = client,
            OAuthAccessToken = Str(r, "access_token"), OAuthRefreshToken = Str(r, "refresh_token"),
            OAuthExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ReadLong(r, "expires_in", 3600),
            OAuthAccountId = email, ApiKeys = ["oauth"], Models = OAuthConfig.DefaultModels("antigravity"), Layers = ["any"],
        };
        if (string.IsNullOrWhiteSpace(profile.OAuthAccessToken)) throw new InvalidOperationException("Google returned no access token.");
        return profile;
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<string> ListenForCodeAsync(int port, string expectedState, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { throw new InvalidOperationException("Could not open the loopback listener: " + ex.Message); }
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            var q = ctx.Request.QueryString;
            var code = q["code"]; var state = q["state"]; var error = q["error"];
            string html; bool success = false;
            if (!string.IsNullOrEmpty(error)) html = Page(false, "Google returned an error: " + WebUtility.HtmlEncode(error));
            else if (string.IsNullOrEmpty(code) || state != expectedState) html = Page(false, "Missing code or state mismatch. Return to the terminal and try again.");
            else { html = Page(true, "Antigravity is connected. You can close this tab and return to the terminal."); success = true; }
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
            if (success) return code!;
        }
        throw new OperationCanceledException(ct);
    }

    private static string Page(bool ok, string message) =>
        $"<!DOCTYPE html><html><head><meta charset='utf-8'><title>vanity-studio</title><style>body{{margin:0;display:flex;align-items:center;justify-content:center;min-height:100vh;background:#111;color:#eee;font-family:system-ui,sans-serif;text-align:center}}.card{{background:#1a1a1a;border-radius:12px;padding:40px 32px;max-width:420px}}.s{{font-size:48px;color:{(ok ? "#4ade80" : "#f87171")}}}</style></head><body><div class='card'><div class='s'>{(ok ? "&#10003;" : "&#10007;")}</div><h2>{(ok ? "Connected" : "Login failed")}</h2><p>{message}</p></div></body></html>";

    private static async Task<(bool ok, string body)> GoogleExchangeCodeAsync(HttpClient http, string code, string verifier, string redirectUri, string client, CancellationToken ct)
    {
        var last = "";
        foreach (var secret in OAuthConfig.SecretsFor(client))
        {
            var fields = new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["code"]          = code,
                ["client_id"]     = OAuthConfig.ClientIdFor(client),
                ["redirect_uri"]  = redirectUri,
                ["code_verifier"] = verifier,
            };
            if (secret.Length > 0) fields["client_secret"] = secret;
            using var resp = await http.PostAsync(OAuthTokenRefresher.GoogleTokenUrl, new FormUrlEncodedContent(fields), ct);
            last = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return (true, last);
            if (!last.Contains("invalid_client") && !last.Contains("client_secret") && !last.Contains("unauthorized_client"))
                return (false, last);
        }
        return (false, last);
    }

    // ── Anthropic (paste a key or token) ───────────────────────────────────────────────────────────────────────────

    /// <summary>Anthropic has no device-code flow here: the operator pastes either an API key (sk-ant-api...) or a
    /// bearer token (sk-ant-oat...). A key goes through the x-api-key header; a token through Authorization: Bearer.
    /// Whether a subscription token is accepted outside Anthropic's own tools is the provider's decision, not this
    /// agent's; an API key is the documented path.</summary>
    public static AiProfile AnthropicFromSecret(string profileName, string secret)
    {
        secret = secret.Trim().Trim('"', '\'');
        if (secret.Length == 0) throw new ArgumentException("An Anthropic key or token is required.");
        bool isToken = secret.StartsWith("sk-ant-oat", StringComparison.OrdinalIgnoreCase);
        return isToken
            ? new AiProfile
            {
                Name = profileName, Provider = "Anthropic", Enabled = true, DisableThinking = true, JsonMode = true, OAuthProvider = "anthropic",
                OAuthAccessToken = secret, ApiKeys = ["oauth"], Models = OAuthConfig.DefaultModels("anthropic"), Layers = ["any"],
            }
            : new AiProfile
            {
                Name = profileName, Provider = "Anthropic", Enabled = true, DisableThinking = true, JsonMode = true,
                ApiKeys = [secret], Models = OAuthConfig.DefaultModels("anthropic"), Layers = ["any"],
            };
    }

    // ── shared ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static AiProfile ProfileFromTokens(string profileName, string provider, string oauthProvider, string tokenBody)
    {
        using var doc = JsonDocument.Parse(tokenBody);
        var root = doc.RootElement;
        var access = Str(root, "access_token");
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidOperationException("The token response had no access_token.");
        return new AiProfile
        {
            Name = profileName, Provider = provider, Enabled = true, DisableThinking = true, JsonMode = true,
            OAuthProvider = oauthProvider,
            OAuthAccessToken = access,
            OAuthRefreshToken = Str(root, "refresh_token"),
            OAuthExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ReadLong(root, "expires_in", 3600),
            OAuthAccountId = AccountId(access),
            ApiKeys = ["oauth"], Models = OAuthConfig.DefaultModels(oauthProvider), Layers = ["any"],
        };
    }

    /// <summary>Account id from the access-token JWT (chatgpt_account_id / account_id / email / sub).</summary>
    public static string? AccountId(string? jwt)
    {
        var payload = JwtPayload(jwt);
        if (payload is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            if (r.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object
                && auth.TryGetProperty("chatgpt_account_id", out var ca) && ca.ValueKind == JsonValueKind.String) return ca.GetString();
            return Str(r, "account_id") ?? Str(r, "email") ?? Str(r, "sub");
        }
        catch { return null; }
    }

    public static string? JwtClaim(string? jwt, string claim)
    {
        var payload = JwtPayload(jwt);
        if (payload is null) return null;
        try { using var doc = JsonDocument.Parse(payload); return Str(doc.RootElement, claim); }
        catch { return null; }
    }

    private static string? JwtPayload(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            while (payload.Length % 4 != 0) payload += "=";
            return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }
        catch { return null; }
    }

    public static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch { /* the URL is printed; the operator opens it by hand */ }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long ReadLong(JsonElement e, string name, long dflt)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return dflt;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return dflt;
    }

    private static string Head(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
