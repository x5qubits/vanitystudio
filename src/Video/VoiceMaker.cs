using System.Text;
using System.Text.Json;
using VanityStudio.Infra;
using VanityStudio.Llm;

namespace VanityStudio.Video;

/// <summary>What a speech render produced: playable audio bytes (WAV or MP3) plus who made them.</summary>
public sealed record VoiceResult(byte[] Bytes, string Mime, string ProviderId, string Model, string ProfileName);

/// <summary>
/// Text-to-speech for a video's spoken lines (<c>"voice": true</c>). A profile speaks only when the operator opted it
/// in: it carries the <c>voice</c> layer, pins a voice model (<c>RoleModels.voice</c>), or lists a model whose id
/// contains "tts" (no silent default: an unasked provider once narrated Romanian with the wrong accent). Adapters:
/// Gemini (API key, native TTS models, default gemini-2.5-flash-preview-tts; PCM wrapped into WAV), Alibaba DashScope
/// (qwen3-tts-flash; a temporary URL that is downloaded) and OpenAI (gpt-4o-mini-tts, WAV). All three need an API
/// key: subscription logins cannot reach the speech endpoints.
/// </summary>
public static class VoiceMaker
{
    public sealed record Candidate(string ProviderId, AiProfile Profile, IReadOnlyList<string> Keys, string Model)
    {
        public string Name => Profile.Name;
    }

    public static Candidate? Resolve(AiProfile p)
    {
        if (p is null || !p.Enabled) return null;
        var keys = (p.ApiKeys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k) && !k.Trim().Equals("oauth", StringComparison.OrdinalIgnoreCase)
                     && !k.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (keys.Count == 0) return null;
        var prov = (p.Provider ?? "").Trim().ToLowerInvariant();
        if (prov is not ("gemini" or "alibaba" or "openai")) return null;
        var pinned = p.RoleModels is { Count: > 0 } && p.RoleModels.TryGetValue("voice", out var pin) && !string.IsNullOrWhiteSpace(pin) ? pin.Trim() : null;
        var listed = (p.Models ?? []).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m) && m.Contains("tts", StringComparison.OrdinalIgnoreCase));
        var carries = (p.Layers ?? []).Any(l => string.Equals(l, "voice", StringComparison.OrdinalIgnoreCase));
        var model = pinned ?? listed ?? (carries ? DefaultModel(prov) : null);
        return model is null ? null : new Candidate(prov, p, keys, model);
    }

    public static string DefaultModel(string provider) => provider switch
    {
        "gemini" => "gemini-2.5-flash-preview-tts",
        "alibaba" => "qwen3-tts-flash",
        _ => "gpt-4o-mini-tts",
    };

    /// <summary>The profiles that can speak: those carrying the voice layer first, then the others, in config order.</summary>
    public static List<Candidate> Candidates(AiOptions ai)
    {
        var profiles = ai.Profiles ?? [];
        bool Carries(AiProfile p) => (p.Layers ?? []).Any(l => string.Equals(l, "voice", StringComparison.OrdinalIgnoreCase));
        return profiles.Where(Carries).Concat(profiles.Where(p => !Carries(p)))
            .Select(Resolve).Where(c => c is not null).Cast<Candidate>()
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>Speech through the first candidate that works. Throws only when every candidate fails.</summary>
    public static async Task<VoiceResult> SpeakAsync(AiOptions ai, string text, CancellationToken ct, Action<string>? log = null)
    {
        var candidates = Candidates(ai);
        if (candidates.Count == 0)
            throw new InvalidOperationException("No AI profile can speak the lines. Add a Gemini, OpenAI or Alibaba API-key profile and give it the voice role: /voice <profile> (see /roles).");
        Exception? last = null;
        foreach (var c in candidates)
        {
            try
            {
                var (bytes, mime) = c.ProviderId switch
                {
                    "gemini" => await GeminiTtsAsync(c.Model, text, c.Keys, ct).ConfigureAwait(false),
                    "alibaba" => await QwenTtsAsync(c.Model, text, c.Keys, ct).ConfigureAwait(false),
                    _ => await OpenAiTtsAsync(c.Model, text, c.Keys, c.Profile.BaseUrl, ct).ConfigureAwait(false),
                };
                if (bytes.Length == 0) throw new InvalidOperationException("the provider returned no audio");
                return new VoiceResult(bytes, mime, c.ProviderId, c.Model, c.Name);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke($"voice on {c.Name} ({c.ProviderId}) failed: {ex.Message}"); last = ex; }
        }
        throw new InvalidOperationException($"All {candidates.Count} voice profile(s) failed. Last error: {last?.Message}", last);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    private static string VoiceName(string provider, string fallback) =>
        AgentConfig.Setting(provider switch { "gemini" => "GeminiVoice", "alibaba" => "QwenVoice", _ => "OpenAiVoice" }) is { Length: > 0 } v ? v : fallback;

    /// <summary>Gemini native TTS: generateContent with AUDIO modality; the answer is raw 24 kHz mono 16-bit PCM
    /// (base64), wrapped into a WAV here so the Studio and ffprobe can read it.</summary>
    private static async Task<(byte[] bytes, string mime)> GeminiTtsAsync(string model, string text, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var body = new
        {
            contents = new[] { new { parts = new[] { new { text } } } },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = VoiceName("gemini", "Kore") } } },
            },
        };
        Exception? last = null;
        foreach (var key in keys)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(key)}")
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"gemini tts {(int)resp.StatusCode}: {Clip(json, 200)}");
                using var doc = JsonDocument.Parse(json);
                var part = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0];
                var inline = part.GetProperty("inlineData");
                var pcm = Convert.FromBase64String(inline.GetProperty("data").GetString() ?? "");
                var mime = inline.TryGetProperty("mimeType", out var mv) ? mv.GetString() ?? "" : "";
                var rate = 24000;
                var ri = mime.IndexOf("rate=", StringComparison.OrdinalIgnoreCase);
                if (ri >= 0 && int.TryParse(new string(mime[(ri + 5)..].TakeWhile(char.IsDigit).ToArray()), out var r) && r > 0) rate = r;
                return (WrapPcmAsWav(pcm, rate, 1, 16), "audio/wav");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("gemini tts: no usable key");
    }

    /// <summary>Qwen TTS on DashScope (intl first, then the Beijing region): the answer carries a temporary URL of the
    /// finished audio, downloaded before it expires.</summary>
    private static async Task<(byte[] bytes, string mime)> QwenTtsAsync(string model, string text, IReadOnlyList<string> keys, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var key in keys)
            foreach (var baseUrl in ClipMaker.DashScopeBases)
            {
                try
                {
                    var body = new { model, input = new { text, voice = VoiceName("alibaba", "Cherry") } };
                    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v1/services/aigc/multimodal-generation/generation")
                    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                    using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"qwen tts {(int)resp.StatusCode}: {Clip(json, 200)}");
                    using var doc = JsonDocument.Parse(json);
                    var url = doc.RootElement.GetProperty("output").GetProperty("audio").GetProperty("url").GetString();
                    if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("qwen tts returned no audio url");
                    var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                    return (bytes, url.Contains(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
            }
        throw last ?? new InvalidOperationException("qwen tts: no usable key");
    }

    /// <summary>OpenAI speech (audio/speech) as WAV, so its length is read from the header even without ffprobe.</summary>
    private static async Task<(byte[] bytes, string mime)> OpenAiTtsAsync(string model, string text, IReadOnlyList<string> keys, string? baseUrl, CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
        if (!root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root += "/v1";
        Exception? last = null;
        foreach (var key in keys)
        {
            try
            {
                var body = new { model, input = text, voice = VoiceName("openai", "alloy"), response_format = "wav" };
                using var req = new HttpRequestMessage(HttpMethod.Post, root + "/audio/speech")
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"openai tts {(int)resp.StatusCode}: {Clip(Encoding.UTF8.GetString(bytes), 200)}");
                return (bytes, "audio/wav");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("openai tts: no usable key");
    }

    /// <summary>Minimal RIFF/WAV header around raw PCM so every downstream tool can read it.</summary>
    private static byte[] WrapPcmAsWav(byte[] pcm, int sampleRate, short channels, short bitsPerSample)
    {
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels);
        w.Write(sampleRate); w.Write(byteRate); w.Write(blockAlign); w.Write(bitsPerSample);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        return ms.ToArray();
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
