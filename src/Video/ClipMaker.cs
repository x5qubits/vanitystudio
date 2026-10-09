using System.Text.Json.Nodes;
using VanityStudio.Llm;

namespace VanityStudio.Video;

/// <summary>
/// Image-to-video for a video's AI clips (<c>{"make": "clip", "from": picture, "prompt": motion}</c>): one picture
/// plus a motion prompt becomes a short clip on Alibaba DashScope's Wan i2v family (async video-synthesis API). The
/// picture goes up through DashScope's own upload (getPolicy, then an OSS PostObject), the task is polled until it
/// finishes, and the clip is downloaded. A profile is a candidate when its provider is alibaba with an API key (or
/// DASHSCOPE_API_KEY / ALIBABA_API_KEY is set); the model is the profile's video_gen pin, else any listed id with
/// "i2v" in it, else wan2.2-i2v-flash.
/// </summary>
public static class ClipMaker
{
    internal static readonly string[] DashScopeBases = { "https://dashscope-intl.aliyuncs.com", "https://dashscope.aliyuncs.com" };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public sealed record Candidate(AiProfile? Profile, IReadOnlyList<string> Keys, string Model)
    {
        public string Name => Profile?.Name ?? "DASHSCOPE_API_KEY";
    }

    public static List<Candidate> Candidates(AiOptions ai)
    {
        var outp = new List<Candidate>();
        foreach (var p in ai.Profiles ?? [])
        {
            if (!p.Enabled || !string.Equals(p.Provider, "alibaba", StringComparison.OrdinalIgnoreCase)) continue;
            var keys = (p.ApiKeys ?? []).Where(k => !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)).ToList();
            if (keys.Count == 0) continue;
            var pinned = p.RoleModels is { Count: > 0 } && p.RoleModels.TryGetValue("video_gen", out var pin) && !string.IsNullOrWhiteSpace(pin) ? pin.Trim() : null;
            var model = pinned ?? (p.Models ?? []).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m) && !m.Contains('/') && m.Contains("i2v", StringComparison.OrdinalIgnoreCase));
            outp.Add(new Candidate(p, keys, model ?? "wan2.2-i2v-flash"));
        }
        var env = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY") ?? Environment.GetEnvironmentVariable("ALIBABA_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) outp.Add(new Candidate(null, [env], "wan2.2-i2v-flash"));
        return outp;
    }

    /// <summary>A clip from a picture through the first candidate that works.</summary>
    public static async Task<byte[]> RenderAsync(AiOptions ai, (byte[] bytes, string mime) image, string prompt, CancellationToken ct, Action<string>? onStatus = null)
    {
        var candidates = Candidates(ai);
        if (candidates.Count == 0)
            throw new InvalidOperationException("No AI profile can make clips: add an Alibaba (DashScope) API-key profile with /key alibaba, or set DASHSCOPE_API_KEY.");
        Exception? last = null;
        foreach (var c in candidates)
        {
            try { return await WanImageToVideoAsync(c.Model, image, prompt, null, c.Keys, ct, onStatus).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { onStatus?.Invoke($"{c.Name} failed: {ex.Message}"); last = ex; }
        }
        throw new InvalidOperationException($"All {candidates.Count} clip profile(s) failed. Last error: {last?.Message}", last);
    }

    /// <summary>ONE image + a MOTION PROMPT → a short clip (Wan i2v). resolution maps to the API parameter
    /// ("480p"/"720p"/"1080p" → upper-case); 1080P when not given.</summary>
    public static async Task<byte[]> WanImageToVideoAsync(string model, (byte[] bytes, string mime) image, string prompt, string? resolution,
        IReadOnlyList<string> keys, CancellationToken ct, Action<string>? onStatus = null)
    {
        var errors = new List<string>();
        foreach (var key in keys)
            foreach (var baseUrl in DashScopeBases)
            {
                try
                {
                    onStatus?.Invoke("uploading the picture");
                    var policy = await GetPolicyAsync(baseUrl, model, key, ct).ConfigureAwait(false);
                    var imgUrl = await OssUploadAsync(policy, image, ct).ConfigureAwait(false);
                    return await I2vTaskAsync(baseUrl, model, imgUrl, prompt, resolution, key, ct, onStatus).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex.Message.Contains("DataInspectionFailed")) { throw; }
                catch (Exception ex)
                {
                    var line = $"{(baseUrl.Contains("-intl") ? "intl" : "cn")}: {Clip(ex.Message, 300)}";
                    if (!errors.Contains(line)) errors.Add(line);
                }
            }
        throw new InvalidOperationException(Failure("wan i2v", model, errors));
    }

    /// <summary>Alibaba's refusal as one plain sentence that names the fix; a key belongs to ONE region, so the other
    /// region's 401 InvalidApiKey is noise next to a real refusal and is dropped then.</summary>
    private static string Failure(string what, string model, IReadOnlyList<string> errors)
    {
        var real = errors.Where(e => !e.Contains("InvalidApiKey", StringComparison.OrdinalIgnoreCase)).ToList();
        bool Has(string code) => real.Any(e => e.Contains(code, StringComparison.OrdinalIgnoreCase));
        var why =
            Has("Arrearage") ? $"Alibaba refuses {model} for this account (\"Arrearage\"): an unpaid balance, or a model this account may not use without paid billing." :
            Has("AllocationQuota.FreeTierOnly") ? $"the free quota for {model} is used up and the account is set to stop there (Stop-on-Exhaust in Model Studio)." :
            Has("AccessDenied.Unpurchased") ? $"this Alibaba account may not use {model}: pay-as-you-go is not enabled for Model Studio." :
            Has("Model not exist") ? $"{model} is not offered in this API key's Alibaba region." :
            real.Count == 0 && errors.Count > 0 ? "Alibaba rejects the API key in both regions (Singapore and Beijing): mistyped, revoked, or made in another region." :
            null;
        var detail = string.Join(" · ", real.Count > 0 ? real : errors);
        return why is null ? $"Alibaba {what} failed. {detail}" : $"Alibaba {what} failed: {why} ({detail})";
    }

    private static async Task<JsonNode> GetPolicyAsync(string baseUrl, string model, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/uploads?action=getPolicy&model={Uri.EscapeDataString(model)}");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"getPolicy {(int)resp.StatusCode}: {Clip(body, 300)}");
        return JsonNode.Parse(body)?["data"] ?? throw new InvalidOperationException("getPolicy: no data in response");
    }

    // OSS PostObject: the form FIELDS precede the file part, the file field is named "file" and comes last. Built by
    // hand: OSS rejects .NET's MultipartFormDataContent output (quoted boundary, text/plain headers on the fields).
    private static async Task<string> OssUploadAsync(JsonNode policy, (byte[] bytes, string mime) img, CancellationToken ct)
    {
        string S(string k) => policy[k]?.ToString() ?? "";
        var ext = img.mime.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png"
                : img.mime.Contains("webp", StringComparison.OrdinalIgnoreCase) ? "webp" : "jpg";
        var name = $"ref_0_{Guid.NewGuid():N}.{ext}";
        var oKey = $"{S("upload_dir")}/{name}";
        var boundary = Guid.NewGuid().ToString("N");
        var sb = new System.Text.StringBuilder();
        void F(string k, string v) => sb.Append("--").Append(boundary)
            .Append("\r\nContent-Disposition: form-data; name=\"").Append(k).Append("\"\r\n\r\n").Append(v).Append("\r\n");
        F("OSSAccessKeyId", S("oss_access_key_id"));
        F("Signature", S("signature"));
        F("policy", S("policy"));
        F("x-oss-object-acl", S("x_oss_object_acl"));
        F("x-oss-forbid-overwrite", S("x_oss_forbid_overwrite"));
        F("key", oKey);
        F("success_action_status", "200");
        sb.Append("--").Append(boundary)
          .Append("\r\nContent-Disposition: form-data; name=\"file\"; filename=\"").Append(name)
          .Append("\"\r\nContent-Type: application/octet-stream\r\n\r\n");
        var head = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        var tail = System.Text.Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        var body = new byte[head.Length + img.bytes.Length + tail.Length];
        Buffer.BlockCopy(head, 0, body, 0, head.Length);
        Buffer.BlockCopy(img.bytes, 0, body, head.Length, img.bytes.Length);
        Buffer.BlockCopy(tail, 0, body, head.Length + img.bytes.Length, tail.Length);
        var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
        using var resp = await Http.PostAsync(S("upload_host"), content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OSS upload {(int)resp.StatusCode}: {Clip(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), 300)}");
        return "oss://" + oKey;
    }

    private static async Task<byte[]> I2vTaskAsync(string baseUrl, string model, string imageUrl, string prompt, string? resolution, string key,
        CancellationToken ct, Action<string>? onStatus)
    {
        // Two request shapes: the newer one carries the image in input.media as a typed first_frame entry, the older one
        // as input.img_url (2.1-2.6 on img_url, 2.7 on media). Start from the version guess; when Alibaba names the field
        // it wanted (a free rejection, before anything is generated), send the other shape once.
        var modern = !(model.Contains("2.0", StringComparison.Ordinal) || model.Contains("2.1", StringComparison.Ordinal)
                    || model.Contains("2.2", StringComparison.Ordinal) || model.Contains("2.5", StringComparison.Ordinal)
                    || model.Contains("2.6", StringComparison.Ordinal));
        try { return await I2vShapeAsync(baseUrl, model, imageUrl, prompt, resolution, key, ct, onStatus, modern).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(modern ? "input.img_url" : "input.media", StringComparison.OrdinalIgnoreCase))
        {
            onStatus?.Invoke("Alibaba wants the other request format for this model; sending it again");
            return await I2vShapeAsync(baseUrl, model, imageUrl, prompt, resolution, key, ct, onStatus, !modern).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> I2vShapeAsync(string baseUrl, string model, string imageUrl, string prompt, string? resolution, string key,
        CancellationToken ct, Action<string>? onStatus, bool modern)
    {
        var input = modern
            ? new JsonObject { ["prompt"] = prompt, ["media"] = new JsonArray(new JsonObject { ["type"] = "first_frame", ["url"] = imageUrl }) }
            : new JsonObject { ["img_url"] = imageUrl, ["prompt"] = prompt };
        var res = string.IsNullOrWhiteSpace(resolution) ? "1080P" : resolution!.ToUpperInvariant();
        // legacy wan2.2 only knows 480P/1080P, so a 720P ask maps up there instead of a 400
        var parameters = new JsonObject { ["prompt_extend"] = true, ["resolution"] = !modern && res == "720P" ? "1080P" : res };
        var payload = new JsonObject { ["model"] = model, ["input"] = input, ["parameters"] = parameters };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/services/aigc/video-generation/video-synthesis");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        req.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
        req.Headers.TryAddWithoutValidation("X-DashScope-OssResourceResolve", "enable");
        req.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if (body.Contains("DataInspectionFailed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Alibaba's content filter rejected the picture; try a tamer one. (DataInspectionFailed)");
            throw new InvalidOperationException($"wan i2v submit {(int)resp.StatusCode}: {Clip(body, 400)} [sent model={model}]");
        }
        var taskId = JsonNode.Parse(body)?["output"]?["task_id"]?.ToString();
        if (string.IsNullOrWhiteSpace(taskId)) throw new InvalidOperationException("wan i2v returned no task id: " + Clip(body, 300));

        var lastNote = "";
        for (int polls = 0; ; polls++)
        {
            if (polls > 200) throw new InvalidOperationException("wan i2v task never completed (~17 min)");
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            using var pReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/tasks/{taskId}");
            pReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var pResp = await Http.SendAsync(pReq, ct).ConfigureAwait(false);
            var pBody = await pResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!pResp.IsSuccessStatusCode) throw new InvalidOperationException($"wan i2v poll {(int)pResp.StatusCode}: {Clip(pBody, 300)}");
            var output = JsonNode.Parse(pBody)?["output"];
            var status = output?["task_status"]?.ToString() ?? "";
            if (status == "SUCCEEDED")
            {
                var url = output?["results"]?["video_url"]?.ToString() ?? output?["video_url"]?.ToString();
                if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("wan i2v succeeded but returned no video url: " + Clip(pBody, 300));
                onStatus?.Invoke("downloading the clip");
                var bytes = await Http.GetByteArrayAsync(url!, ct).ConfigureAwait(false);
                if (bytes.Length == 0) throw new InvalidOperationException("wan i2v video download came back empty");
                return bytes;
            }
            if (status is "FAILED" or "CANCELED" or "UNKNOWN")
            {
                var msg = output?["message"]?.ToString() ?? Clip(pBody, 300);
                if (msg.Contains("DataInspectionFailed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Alibaba's content filter rejected the picture; try a tamer one. (DataInspectionFailed)");
                throw new InvalidOperationException($"wan i2v task {status}: {Clip(msg, 300)}");
            }
            var note = status == "PENDING" ? "waiting in the Alibaba queue" : "rendering the clip";
            if (note != lastNote) { onStatus?.Invoke(note); lastNote = note; }
        }
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
