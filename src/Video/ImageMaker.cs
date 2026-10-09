using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VanityStudio.Llm;

namespace VanityStudio.Video;

/// <summary>
/// Draws a picture from a prompt (optionally from a picture to start from) through the AI profiles that can make
/// images: OpenAI (API key, or the ChatGPT login), the Antigravity login (Gemini image models) and Alibaba wan (API
/// key). Profiles carrying an image layer (photo_gen, product_gen, image_gen, image) come first, then every profile
/// on the "any" layer. The video's AI stills (<c>{"make": "still"}</c>) are drawn here.
/// </summary>
public static class ImageMaker
{
    private static readonly string[] ImageLayers = ["photo_gen", "product_gen", "image_gen", "image"];

    /// <summary>The profiles that can draw, in the order they are tried.</summary>
    public static List<AiProfile> Candidates(AiOptions ai)
    {
        var enabled  = (ai.Profiles ?? []).Where(p => p.Enabled && CanDraw(p)).ToList();
        var tagged   = enabled.Where(p => p.Layers.Any(l => ImageLayers.Contains(l, StringComparer.OrdinalIgnoreCase))).ToList();
        var anyLayer = enabled.Where(p => !tagged.Contains(p) && p.Layers.Any(l => string.Equals(l, "any", StringComparison.OrdinalIgnoreCase))).ToList();
        return tagged.Concat(anyLayer).ToList();
    }

    /// <summary>Whether a profile is one of the wired image providers.</summary>
    public static bool CanDraw(AiProfile p)
    {
        var prov = (p.Provider ?? "").Trim().ToLowerInvariant();
        bool hasKey = p.ApiKeys.Any(k => !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase));
        bool oauth = !string.IsNullOrWhiteSpace(p.OAuthAccessToken) || !string.IsNullOrWhiteSpace(p.OAuthRefreshToken);
        return (prov == "openai" && (hasKey || oauth)) || (prov == "alibaba" && hasKey)
            || (oauth && string.Equals(p.OAuthProvider, "antigravity", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One picture from one profile, as the provider returns it (PNG, JPEG or WebP bytes). The size is a
    /// hint: each provider draws its nearest supported size and shape.</summary>
    public static async Task<byte[]> DrawAsync(AiProfile p, string prompt, int width, int height, CancellationToken ct, byte[]? reference = null)
    {
        if (OAuthTokenRefresher.UsesOAuth(p))
            await OAuthTokenRefresher.EnsureFreshAsync(p, 0, force: false, ct).ConfigureAwait(false);
        var prov = (p.Provider ?? "").Trim().ToLowerInvariant();
        bool hasKey = p.ApiKeys.Any(k => !string.IsNullOrWhiteSpace(k));
        bool hasOAuth = !string.IsNullOrWhiteSpace(p.OAuthAccessToken);
        if (prov == "openai" && hasKey)
            return await OpenAiKeyAsync(prompt, width, height, false, p, ct, reference).ConfigureAwait(false);
        if (prov == "openai" && hasOAuth)
        {
            try { return await OpenAiOAuthAsync(prompt, width, height, p, ct, reference).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (ex.Message.Contains("401"))
            {
                // valid by its expiry but refused: refresh once and try again
                if (await OAuthTokenRefresher.EnsureFreshAsync(p, 0, force: true, ct).ConfigureAwait(false))
                    return await OpenAiOAuthAsync(prompt, width, height, p, ct, reference).ConfigureAwait(false);
                throw;
            }
        }
        if (prov == "alibaba" && hasKey)
            return await AlibabaWanAsync(prompt, width, height, p, ct, reference).ConfigureAwait(false);
        if (hasOAuth && string.Equals(p.OAuthProvider, "antigravity", StringComparison.OrdinalIgnoreCase))
            return await AntigravityAsync(prompt, width, height, p, ct, reference).ConfigureAwait(false);
        throw new NotSupportedException($"Profile '{p.Name}' ({prov}) is not a wired image provider. Use OpenAI (key or login), the Antigravity login, or Alibaba.");
    }

    // ── OpenAI API key ────────────────────────────────────────────────────────

    private static readonly (int W, int H)[] OaiSizes = [(1024, 1024), (1024, 1536), (1536, 1024)];

    private static async Task<byte[]> OpenAiKeyAsync(string prompt, int w, int h, bool transparent,
        AiProfile p, CancellationToken ct, byte[]? reference = null)
    {
        var key   = p.ApiKeys.First(k => !string.IsNullOrWhiteSpace(k));
        var model = Pick(p, "photo_gen", p.Models.FirstOrDefault(), "gpt-image-1");
        var (sw, sh) = SnapOai(w, h);
        if (reference != null)
        {
            // a picture to start from goes to the edits endpoint (multipart), the answer has the same shape
            var form = new MultipartFormDataContent
            {
                { new StringContent(model), "model" }, { new StringContent(prompt), "prompt" },
                { new StringContent($"{sw}x{sh}"), "size" }, { new StringContent("1"), "n" },
            };
            if (transparent) form.Add(new StringContent("transparent"), "background");
            var img = new ByteArrayContent(reference);
            img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(img, "image", "reference.png");
            using var edit = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/edits") { Content = form };
            edit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return await SendImageRequestAsync(edit, ct).ConfigureAwait(false);
        }
        var body = new Dictionary<string, object>
        {
            ["model"] = model, ["prompt"] = prompt,
            ["size"] = $"{sw}x{sh}", ["n"] = 1, ["response_format"] = "b64_json",
        };
        if (transparent) body["background"] = "transparent";

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations")
            { Content = Json(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var png = await SendImageRequestAsync(req, ct).ConfigureAwait(false);
        return png;
    }

    // ── OpenAI OAuth (ChatGPT/Codex) ──────────────────────────────────────────

    private static async Task<byte[]> OpenAiOAuthAsync(string prompt, int w, int h, AiProfile p, CancellationToken ct, byte[]? reference = null)
    {
        var token = p.OAuthAccessToken!;
        var model = Pick(p, "photo_gen", p.Models.FirstOrDefault(), "gpt-5.6-luna");
        var (sw, sh) = SnapOai(w, h);
        var content = new List<object>();
        if (reference != null)
            content.Add(new Dictionary<string, object> { ["type"] = "input_image", ["image_url"] = "data:image/png;base64," + Convert.ToBase64String(reference) });
        content.Add(new Dictionary<string, object> { ["type"] = "input_text", ["text"] = prompt });
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = content.ToArray(),
                }
            },
            ["stream"] = true, ["store"] = false,
            ["tools"] = new object[] { new Dictionary<string, object> { ["type"] = "image_generation", ["size"] = $"{sw}x{sh}" } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://chatgpt.com/backend-api/codex/responses")
            { Content = Json(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(p.OAuthAccountId))
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", p.OAuthAccountId);
        req.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI OAuth {(int)resp.StatusCode}: {Trim(raw, 300)}");

        string? b64 = null;
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("data: ", StringComparison.Ordinal) || t.Length <= 6) continue;
            var payload = t[6..];
            if (payload == "[DONE]") continue;
            try
            {
                using var ev = JsonDocument.Parse(payload);
                var root = ev.RootElement;
                if (root.TryGetProperty("type", out var ty) && ty.GetString() == "response.output_item.done"
                    && root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var it) && it.GetString() == "image_generation_call"
                    && item.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                    b64 = res.GetString();
            }
            catch { /* skip malformed SSE lines */ }
        }
        if (string.IsNullOrWhiteSpace(b64))
            throw new InvalidOperationException("OpenAI OAuth returned no image.");
        return Convert.FromBase64String(b64);
    }

    // ── Antigravity / Gemini OAuth ────────────────────────────────────────────

    private static readonly string[] AgBaseUrls =
        ["https://cloudcode-pa.googleapis.com", "https://daily-cloudcode-pa.googleapis.com"];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _agProject = new();

    /// <summary>The login's image models in the order they are tried: the profile's own (or the default), then the
    /// others. Google counts the image quota per model ("You have exhausted your capacity on this model"), so a login
    /// whose first model is used up for hours can still draw with the next one.</summary>
    private static readonly string[] AgImageModels = ["gemini-3.1-flash-image", "gemini-2.5-flash-image", "gemini-3-pro-image"];

    private static async Task<byte[]> AntigravityAsync(string prompt, int w, int h, AiProfile p, CancellationToken ct, byte[]? reference = null)
    {
        var models = new[] { ResolveGeminiImageModel(p) }.Concat(AgImageModels).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Exception? last = null, quota = null;
        foreach (var model in models)
        {
            try { return await AntigravityModelAsync(prompt, w, h, p, model, ct, reference).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex.Message.Contains("429") || ex.Message.Contains("RESOURCE_EXHAUSTED") || ex.Message.Contains("404") || ex.Message.Contains("NOT_FOUND"))
            {
                // this model's quota is used up (or the login does not have it): the next model has its own
                if (quota is null && (ex.Message.Contains("429") || ex.Message.Contains("RESOURCE_EXHAUSTED"))) quota = ex;
                last = ex;
            }
        }
        // a used-up quota says more than the "not found" of a model the login does not have
        throw quota ?? last ?? new InvalidOperationException("Antigravity image failed.");
    }

    private static async Task<byte[]> AntigravityModelAsync(string prompt, int w, int h, AiProfile p, string model, CancellationToken ct, byte[]? reference)
    {
        var token  = p.OAuthAccessToken!;
        var aspect = AspectFor(w, h);
        var projId = await EnsureAgProjectAsync(token, p.OAuthAccountId, ct).ConfigureAwait(false);

        var imgCfg = new Dictionary<string, object> { ["imageSize"] = "2K" };
        if (!string.IsNullOrEmpty(aspect)) imgCfg["aspectRatio"] = aspect;
        // the picture to start from travels as an inline part before the instruction
        var parts = new List<object>();
        if (reference != null)
            parts.Add(new Dictionary<string, object> { ["inlineData"] = new Dictionary<string, object> { ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(reference) } });
        parts.Add(new Dictionary<string, object> { ["text"] = prompt });

        var envelope = new Dictionary<string, object>
        {
            ["project"] = projId ?? "",
            ["requestId"] = "agent-" + Guid.NewGuid().ToString("N"),
            ["model"] = model,
            ["userAgent"] = "antigravity",
            ["requestType"] = "agent",
            ["request"] = new Dictionary<string, object>
            {
                ["contents"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["parts"] = parts.ToArray(),
                    }
                },
                ["generationConfig"] = new Dictionary<string, object>
                {
                    ["responseModalities"] = new[] { "IMAGE" },
                    ["imageConfig"] = imgCfg,
                    ["candidateCount"] = 1,
                },
                ["systemInstruction"] = new Dictionary<string, object>
                {
                    ["parts"] = new object[] { new Dictionary<string, object> { ["text"] = "You are an AI image generator. Generate images based on user descriptions." } },
                },
            },
        };

        Exception? last = null;
        foreach (var baseUrl in AgBaseUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1internal:generateContent")
                    { Content = Json(envelope) };
                AddAgHeaders(req, token);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"Antigravity {(int)resp.StatusCode}: {Trim(raw, 240)}");
                    continue;
                }
                var png = ExtractAgImage(raw);
                if (png is { Length: > 0 }) return png;
                last = new InvalidOperationException("Antigravity returned no image.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("Antigravity image failed.");
    }

    private static void AddAgHeaders(HttpRequestMessage req, string token)
    {
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("User-Agent", "antigravity/1.11.5 linux/amd64");
        req.Headers.TryAddWithoutValidation("X-Goog-Api-Client", "google-cloud-sdk vscode_cloudshelleditor/0.1");
        req.Headers.TryAddWithoutValidation("Client-Metadata", "{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"WINDOWS\",\"pluginType\":\"GEMINI\"}");
    }

    private static async Task<string?> EnsureAgProjectAsync(string token, string? accountId, CancellationToken ct)
    {
        var key = accountId ?? "default";
        if (_agProject.TryGetValue(key, out var cached)) return cached;

        var loadJson = JsonSerializer.Serialize(new { metadata = new { ideType = "ANTIGRAVITY" } });
        foreach (var baseUrl in AgBaseUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1internal:loadCodeAssist")
                    { Content = new StringContent(loadJson, Encoding.UTF8, "application/json") };
                AddAgHeaders(req, token);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var root = doc.RootElement;
                string? projId = null;
                if (root.TryGetProperty("cloudaicompanionProject", out var cp))
                    projId = cp.ValueKind == JsonValueKind.String ? cp.GetString()
                           : cp.TryGetProperty("id", out var cid) ? cid.GetString() : null;
                if (projId != null) { _agProject[key] = projId; return projId; }
            }
            catch { /* try next host */ }
        }
        return null;
    }

    private static byte[]? ExtractAgImage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.TryGetProperty("response", out var wrapped)) root = wrapped;
            if (!root.TryGetProperty("candidates", out var cands)) return null;
            foreach (var cand in cands.EnumerateArray())
            {
                if (!cand.TryGetProperty("content", out var content)) continue;
                if (!content.TryGetProperty("parts", out var parts)) continue;
                foreach (var part in parts.EnumerateArray())
                {
                    JsonElement idata;
                    if ((part.TryGetProperty("inlineData", out idata) || part.TryGetProperty("inline_data", out idata))
                        && idata.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var b64 = d.GetString();
                        if (!string.IsNullOrEmpty(b64))
                            try { return Convert.FromBase64String(b64); } catch { }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static bool IsImageModel(string m) =>
        !string.IsNullOrWhiteSpace(m) && m.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string ResolveGeminiImageModel(AiProfile p)
    {
        string? pinned = null;
        foreach (var lyr in new[] { "photo_gen", "product_gen" })
            if (p.RoleModels.TryGetValue(lyr, out var rm) && IsImageModel(rm)) { pinned = rm; break; }
        pinned ??= p.Models.FirstOrDefault(IsImageModel);
        var m = (pinned ?? "").Trim();
        if (m.StartsWith("antigravity/", StringComparison.OrdinalIgnoreCase)) m = m["antigravity/".Length..];
        return m switch
        {
            "" => "gemini-3.1-flash-image",
            "gemini-3-pro-image" or "gemini-3-pro-image-preview" or "nano-banana-pro" => "gemini-3-pro-image",
            "gemini-2.5-flash-image" or "gemini-2.5-flash-image-preview" or "nano-banana" => "gemini-2.5-flash-image",
            _ => m,
        };
    }

    // ── Alibaba wan ───────────────────────────────────────────────────────────

    private static async Task<byte[]> AlibabaWanAsync(string prompt, int w, int h, AiProfile p, CancellationToken ct, byte[]? reference = null)
    {
        var content = new List<object>();
        if (reference != null) content.Add(new { image = "data:image/png;base64," + Convert.ToBase64String(reference) });
        content.Add(new { text = prompt });
        var key   = p.ApiKeys.First(k => !string.IsNullOrWhiteSpace(k));
        var model = Pick(p, "photo_gen", p.Models.FirstOrDefault(m => m.Contains("image", StringComparison.OrdinalIgnoreCase)), "wan2.7-image-pro");
        var size  = WanSize(w, h);
        var bases = new[] { "https://dashscope-intl.aliyuncs.com", "https://dashscope.aliyuncs.com" };
        Exception? last = null;
        foreach (var baseUrl in bases)
        {
            try
            {
                var payload = new
                {
                    model,
                    input = new { messages = new[] { new { role = "user", content = content.ToArray() } } },
                    parameters = new { size, n = 1, watermark = false, seed = Random.Shared.Next(int.MaxValue) },
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v1/services/aigc/multimodal-generation/generation")
                    { Content = Json(payload) };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                req.Headers.TryAddWithoutValidation("X-DashScope-OssResourceResolve", "enable");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var bodyStr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"wan {(int)resp.StatusCode}: {Trim(bodyStr, 300)}");
                using var doc = JsonDocument.Parse(bodyStr);
                var url = ExtractWanUrl(doc.RootElement);
                if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("Alibaba wan returned no image URL.");
                using var http2 = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                byte[] bytes;
                try { bytes = await http2.GetByteArrayAsync(url, ct).ConfigureAwait(false); }
                catch (IOException ex) when (!ct.IsCancellationRequested) { throw new HttpRequestException("Image download failed: " + ex.Message, ex); }
                if (bytes is { Length: > 0 }) return bytes;
                throw new InvalidOperationException("Alibaba wan image download was empty.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("Alibaba wan failed.");
    }

    private static string ExtractWanUrl(JsonElement root)
    {
        if (root.TryGetProperty("output", out var outp) && outp.TryGetProperty("choices", out var choices))
            foreach (var ch in choices.EnumerateArray())
                if (ch.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var parts))
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.String)
                            return im.GetString() ?? "";
        return "";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<byte[]> SendImageRequestAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI {(int)resp.StatusCode}: {Trim(text, 300)}");
        using var doc = JsonDocument.Parse(text);
        var data = doc.RootElement.GetProperty("data");
        var b64  = data[0].GetProperty("b64_json").GetString() ?? throw new InvalidOperationException("No b64_json in response.");
        return Convert.FromBase64String(b64);
    }

    private static StringContent Json(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    private static string Pick(AiProfile p, string layer, string? fallback, string def)
    {
        if (p.RoleModels.TryGetValue(layer, out var rm) && !string.IsNullOrWhiteSpace(rm)) return rm;
        return fallback ?? def;
    }

    private static (int W, int H) SnapOai(int w, int h)
    {
        (int W, int H)[] sizes = [(1024, 1024), (1024, 1536), (1536, 1024)];
        double target = (double)w / h;
        return sizes.MinBy(s => Math.Abs((double)s.W / s.H - target));
    }

    private static string AspectFor(int w, int h)
    {
        double a = (double)w / Math.Max(1, h);
        (string label, double val)[] opts =
        [
            ("1:1", 1.0), ("4:5", 0.8), ("3:4", 0.75), ("2:3", 0.667), ("9:16", 0.5625),
            ("5:4", 1.25), ("4:3", 1.333), ("3:2", 1.5), ("16:9", 1.777),
        ];
        return opts.MinBy(o => Math.Abs(o.val - a)).label;
    }

    private static string WanSize(int w, int h)
    {
        double a = (double)w / Math.Max(1, h);
        if (a >= 1.25) return "1280*720";
        if (a <= 0.8) return "720*1280";
        return "1024*1024";
    }

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n] + "...";
}
