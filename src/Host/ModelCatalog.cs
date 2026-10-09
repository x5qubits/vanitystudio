using System.Net.Http.Headers;
using System.Text.Json;
using VanityStudio.Llm;

namespace VanityStudio.Host;

/// <summary>What models a profile's credentials can actually use, asked from the provider, never guessed.</summary>
public static class ModelCatalog
{
    public static async Task<List<string>> ListAsync(AiProfile p, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var provider = p.Provider.ToLowerInvariant();
        var key = p.ApiKeys.Select(k => k.Trim().Trim('"', '\'')).FirstOrDefault(k => k.Length > 0 && !k.Equals("oauth", StringComparison.OrdinalIgnoreCase));
        var oauth = OAuthTokenRefresher.UsesOAuth(p) ? p.OAuthAccessToken : null;
        var bearer = key ?? oauth;
        var baseUrl = string.IsNullOrWhiteSpace(p.BaseUrl) ? SingleCallLlmClient.ProviderBaseUrl(provider) : p.BaseUrl.TrimEnd('/');
        var models = new List<string>();

        if (provider == "gemini" && key is not null)
        {
            var json = await http.GetStringAsync("https://generativelanguage.googleapis.com/v1beta/models?pageSize=200&key=" + Uri.EscapeDataString(key), ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var n)) models.Add((n.GetString() ?? "").Replace("models/", ""));
            return models.OrderBy(x => x).ToList();
        }
        if (provider == "antigravity" && oauth is not null)
        {
            // Cloud Code's fetchAvailableModels, with the same User-Agent the transport sends (the tier is gated on it).
            // Shape seen live: { models: { "<id>": {...} }, buckets: [ { modelId: "<id>", ... } ] }.
            foreach (var url in new[] { "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels", "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels" })
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauth);
                    req.Headers.TryAddWithoutValidation("User-Agent", SingleCallLlmClient.AntigravityUserAgent);
                    using var resp = await http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    Infra.Log.Info($"[models] {url} -> HTTP {(int)resp.StatusCode}: {body[..Math.Min(2000, body.Length)]}");
                    if (!resp.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("models", out var map) && map.ValueKind == JsonValueKind.Object)
                        foreach (var prop in map.EnumerateObject()) if (prop.Name.Length > 0) models.Add(prop.Name);
                    if (root.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
                        foreach (var b in buckets.EnumerateArray())
                            if (b.TryGetProperty("modelId", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } id) models.Add(id);
                    Collect(root, models);
                    if (models.Count > 0) return models.Distinct().OrderBy(x => x).ToList();
                }
                catch (Exception ex) { Infra.Log.Warn($"[models] {url}: {ex.Message}"); }
            }
            // The catalog endpoint answered nothing usable: the names the Antigravity app itself offers.
            return
            [
                "gemini-3.8-flash-low", "gemini-3.8-flash-medium", "gemini-3.8-flash-high", "gemini-3.1-pro-high", "gemini-3.1-pro", "gemini-3-pro", "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash",
                "gemini-2.5-pro", "gemini-2.5-flash", "claude-sonnet-4-6", "claude-opus-4-6", "gpt-oss-120b-medium",
                "(the catalog call returned nothing; these are the names the Antigravity app offers - see logs/vanity-studio.log)",
            ];
        }
        if (provider == "anthropic")
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");
            req.Headers.Add("anthropic-version", "2023-06-01");
            if (key is not null) req.Headers.Add("x-api-key", key);
            else if (oauth is not null) { req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauth); req.Headers.Add("anthropic-beta", "oauth-2025-04-20"); }
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id)) models.Add(id.GetString() ?? "");
            return models.Where(m => m.Length > 0).OrderBy(x => x).ToList();
        }
        if (provider == "ollama" || baseUrl.Contains("11434"))
        {
            var json = await http.GetStringAsync(baseUrl + "/api/tags", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var n)) models.Add(n.GetString() ?? "");
            return models.OrderBy(x => x).ToList();
        }
        if (provider == "openai" && oauth is not null && key is null)
            throw new InvalidOperationException("A ChatGPT login has no public model catalog; use /model with one of: " + string.Join(", ", Auth.OAuthConfig.DefaultModels("openai")));

        // OpenAI-compatible: GET {base}/v1/models (or {base}/models when the base already ends in /v1)
        {
            var path = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/models" : "/v1/models";
            if (provider == "gemini") path = "/v1beta/openai/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id)) models.Add(id.GetString() ?? "");
            return models.Where(m => m.Length > 0).OrderBy(x => x).ToList();
        }
    }

    private static void Collect(JsonElement el, List<string> into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Name is "models" or "availableModels" && p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var m in p.Value.EnumerateArray())
                        {
                            if (m.ValueKind == JsonValueKind.String) into.Add(m.GetString() ?? "");
                            else if (m.ValueKind == JsonValueKind.Object && (m.TryGetProperty("name", out var n) || m.TryGetProperty("id", out n) || m.TryGetProperty("model", out n)) && n.ValueKind == JsonValueKind.String)
                                into.Add(n.GetString() ?? "");
                        }
                    else if (p.Name == "models" && p.Value.ValueKind == JsonValueKind.Object)
                        foreach (var m in p.Value.EnumerateObject()) into.Add(m.Name);
                    else Collect(p.Value, into);
                }
                break;
            case JsonValueKind.Array:
                foreach (var e in el.EnumerateArray()) Collect(e, into);
                break;
        }
    }
}
