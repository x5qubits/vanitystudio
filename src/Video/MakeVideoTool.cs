using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;
using VanityStudio.Infra;
using VanityStudio.Llm;
using VanityStudio.Tools;
using static VanityStudio.Video.VideoText;

namespace VanityStudio.Video;

/// <summary>The <c>make_video</c> tool. The videographer writes the SCRIPT of a video (contract: the Studio's
/// docs/video-jobs.md, section 1): scenes in order, each a ready-made Studio block with its params, files and line.
/// Layout, motion, timing, transitions, sound and look are the Studio's (its director compiles the script in the
/// renderer's browser), so the model only picks and fills blocks.
///
/// Actions: <c>blocks</c> shows the Studio's block catalog (js/video/catalog.json, cached 10 min); <c>look</c> shows a
/// picture with a labelled 10x10 grid, so a point or a box on it is read, not guessed; <c>site</c> opens a website in a
/// headless browser, makes the clicks and lists what the page has; <c>submit</c> validates the script against the
/// catalog (every error at once, each naming its scene and field), resolves every file now and queues a job that
/// <see cref="VideoJobs"/> works in the background: the call returns at once and the finished video is reported when it
/// is ready. <c>remix</c> patches a finished job's script and renders it again, reusing what did not change;
/// <c>status</c> and <c>cancel</c> are for "how is my video" and "stop it".</summary>
public sealed class MakeVideoTool : IVisualTool
{
    private readonly StudioProject _project;
    private readonly VideoJobs _jobs;
    private readonly Func<AiOptions> _ai;
    private readonly Func<string> _request;

    /// <param name="request">The operator's message this turn answers (a reel request forces the vertical format).</param>
    public MakeVideoTool(StudioProject project, VideoJobs jobs, Func<AiOptions> ai, Func<string> request)
    {
        _project = project; _jobs = jobs; _ai = ai; _request = request;
    }

    private static readonly ConcurrentDictionary<string, (DateTime at, string json)> _catalogCache = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public ToolDefinition Definition { get; } = new()
    {
        Name = "make_video",
        Description =
            "Make a video from a script. Vanity Studio builds it from ready-made scenes (blocks) and renders it in the background " +
            "on this machine; the finished video is reported when it is ready. blocks: the scenes you can use, with their params and " +
            "files. look: a picture with a grid, to read points and boxes off it. site: a website opened in a browser, clicked " +
            "through and read page by page (a tutorial's real steps). submit: queue the script. remix: change a finished video. " +
            "status: the video jobs here. cancel: stop one, or every video still being made.",
        Parameters = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "blocks", "look", "site", "submit", "remix", "status", "cancel" } },
                ["file"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "look: the picture (its path in the project, its name, or media:logo)." },
                ["url"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "site: the page's address (https://...)." },
                ["clicks"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["description"] = "site: what to click after the page opens, in order, each by the words on it as the page writes them (at most 8)." },
                ["login"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "site: true when the page is behind a login; the login browser's session is used (the operator logs in once with /site-login)." },
                ["format"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "site: the video's format; reel or portrait opens the phone layout, else the desktop site." },
                // JSON TEXT, not an object: some providers cannot emit deeply nested free-form objects in a tool argument
                ["script"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "submit: the script as JSON text." },
                ["job"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "cancel or remix: the job number (remix also takes \"latest\")." },
                ["patches"] = new Dictionary<string, object> { ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> {
                            ["path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "dotted path into the original script, e.g. music, treatment, look, scenes[2].line, scenes[0].params.title, scenes[1].block, format, voice" },
                            ["value"] = new Dictionary<string, object> { ["description"] = "the new value (string / number / bool / object / array); null removes the field." },
                        } },
                    ["description"] = "remix: a list of {path, value} patches applied to the previous job's script (or {op:\"replace\", from, to, path?} to swap words). Scenes whose line and files did NOT change reuse their voice and AI pictures; only what changed is made again." },
            },
            ["required"] = new[] { "action" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default) =>
        (await ExecuteVisualAsync("", argsJson, ct).ConfigureAwait(false)).Output;

    public async Task<ToolResultRecord> ExecuteVisualAsync(string toolCallId, string argsJson, CancellationToken ct = default)
    {
        JsonElement args;
        try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException ex) { return Fail(toolCallId, "arguments are not valid JSON: " + ex.Message); }
        string S(string k) => args.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var action = S("action").Trim().ToLowerInvariant();
        try
        {
            switch (action)
            {
                case "blocks":
                {
                    var (cat, error) = await CatalogAsync(ct).ConfigureAwait(false);
                    return cat is null ? Fail(toolCallId, error!) : Ok(toolCallId, Describe(cat));
                }
                case "look": return Look(toolCallId, S("file").Trim());
                case "site": return await SiteAsync(toolCallId, args, S("format").Trim().ToLowerInvariant(), ct).ConfigureAwait(false);
                case "submit": return await SubmitAsync(toolCallId, args, ct).ConfigureAwait(false);
                case "remix": return await RemixAsync(toolCallId, args, ct).ConfigureAwait(false);
                case "status": return Ok(toolCallId, Status());
                case "cancel": return Ok(toolCallId, Cancel(args));
                default: return Fail(toolCallId, "make_video needs action = blocks | look | site | submit | remix | status | cancel.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail(toolCallId, "make_video: " + ex.Message); }
    }

    private static ToolResultRecord Ok(string id, string text) => new() { ToolCallId = id, ToolName = "make_video", Output = text };
    private static ToolResultRecord Fail(string id, string text) => new() { ToolCallId = id, ToolName = "make_video", Output = "Error: " + text, IsError = true };

    // ── site: a website as a browser opens it, for a tutorial's real steps ──
    // A tutorial written without seeing the site guesses its steps, its section addresses and its button words. The
    // renderer's browser opens the address, makes the clicks, and says what the page it ends on has.
    private static async Task<ToolResultRecord> SiteAsync(string id, JsonElement args, string format, CancellationToken ct)
    {
        var url = args.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? (u.GetString() ?? "").Trim() : "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Fail(id, "site needs url: the page's address (https://...).");
        var clicks = new JsonArray();
        if (args.TryGetProperty("clicks", out var cl))
        {
            if (cl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cl.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && (c.GetString() ?? "").Trim() is { Length: > 0 } w) clicks.Add(w);
            }
            else if (cl.ValueKind == JsonValueKind.String && (cl.GetString() ?? "").Trim() is { Length: > 0 } one) clicks.Add(one);
        }
        if (clicks.Count > 8) return Fail(id, $"site takes at most 8 clicks; there are {clicks.Count}.");
        var login = args.TryGetProperty("login", out var lg) && lg.ValueKind == JsonValueKind.True;
        try { return Ok(id, await SiteTextAsync(url, clicks.Select(c => Str(c)!).ToList(), login, format, ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Fail(id, "site: " + ex.Message); }
    }

    /// <summary>A site's page as the videographer reads it (also `vanity-studio site`).</summary>
    public static async Task<string> SiteTextAsync(string url, List<string> clicks, bool login, string format, CancellationToken ct)
    {
        var cmd = new Dictionary<string, object> { { "action", "site" }, { "url", url }, { "clicks", clicks.Cast<object>().ToList() }, { "login", login ? "true" : "false" }, { "format", format } };
        var res = await Task.Run(() => StudioOps.Dispatch(cmd, l => Log.Info(l)), ct).ConfigureAwait(false);
        JsonObject? page = null;
        try { page = JsonNode.Parse(Convert.ToString(res.GetValueOrDefault("page")) ?? "") as JsonObject; } catch (JsonException) { }
        if (page is null) throw new InvalidOperationException("the browser did not say what the page has.");
        return PageText(page, new JsonArray(clicks.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()), login);
    }

    /// <summary>What a page has, as the videographer reads it: each heading, button, link, field and section with the
    /// screen it is on (1 = what shows first), so a tutorial's step names the page, the clicks and the words of its target.</summary>
    internal static string PageText(JsonObject page, JsonArray clicks, bool login)
    {
        var sb = new StringBuilder();
        string Q(JsonNode? n) => "\"" + (Str(n) ?? "") + "\"";
        sb.Append($"{Str(page["title"]) ?? ""} ({Str(page["url"]) ?? ""}), {N(Num(page["screens"]) ?? 1)} screens tall");
        if (clicks.Count > 0) sb.Append(", reached by clicking " + string.Join(" > ", clicks.Select(Q)));
        if (login) sb.Append(", logged in");
        sb.Append(".\n");
        var heads = (page["heads"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(25).ToList();
        if (heads.Count > 0) sb.Append("Headings: " + string.Join("; ", heads.Select(h => $"[{N(Num(h["screen"]) ?? 1)}] {Str(h["text"])}")) + "\n");
        var actions = (page["actions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(60).ToList();
        if (actions.Count > 0)
        {
            sb.Append("Buttons and links (screen: words → where they lead):\n");
            foreach (var a in actions)
                sb.Append($"- {N(Num(a["screen"]) ?? 1)}: {Q(a["text"])}" + (Str(a["href"]) is { Length: > 0 } href ? " → " + href : " (button)") + "\n");
        }
        var fields = (page["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(20).ToList();
        if (fields.Count > 0)
            sb.Append("Form fields: " + string.Join("; ", fields.Select(f => $"{(Str(f["label"]) is { Length: > 0 } l ? l : "(no label)")} ({Str(f["type"])}{(Bool(f["required"]) == true ? ", required" : "")}, screen {N(Num(f["screen"]) ?? 1)})")) + "\n");
        var sections = (page["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(20).ToList();
        if (sections.Count > 0)
            sb.Append("Sections an address can open at: " + string.Join("; ", sections.Select(s => $"#{Str(s["id"])}" + (Str(s["heading"]) is { Length: > 0 } hd ? $" \"{hd}\"" : "") + $" (screen {N(Num(s["screen"]) ?? 1)})")) + "\n");
        sb.Append("A step on this page is screen-demo with url = this address (or address#section), clicks = the clicks that reached it, " +
                  "and target = the words of its button or link exactly as listed" + (login ? ", login = true" : "") + ".");
        return sb.ToString();
    }

    // ── look: the picture with a grid ──
    private ToolResultRecord Look(string id, string file)
    {
        if (file.Length == 0) return Fail(id, "look needs file: the picture (its path in the project, its name, or media:logo).");
        var (f, error) = _project.Resolve(file);
        if (f is null) return Fail(id, error!);
        if (!f.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Fail(id, $"{f.Name} is not a picture ({f.Mime}); look shows pictures.");
        var grid = GridPicture(f.Path);
        if (grid is null) return Fail(id, $"{f.Name} could not be read as a picture.");
        var (dataUrl, w, h) = grid.Value;
        return new ToolResultRecord
        {
            ToolCallId = id, ToolName = "make_video",
            Output = $"{f.Canonical}: {w}x{h} px, shown with a grid line every 0.1. Points and boxes are fractions 0..1 across and down; " +
                     "read them off the grid: a point is [x, y], a box is [x, y, w, h] from its top-left corner.",
            ScreenshotDataUrl = dataUrl,
        };
    }

    /// <summary>The picture (at most 1024 px on its long side) on a dark margin, with a line every tenth across and
    /// down and the fractions written along the top and the left. Null when it cannot be decoded.</summary>
    internal static (string dataUrl, int w, int h)? GridPicture(string path)
    {
        try
        {
            using var src = SKBitmap.Decode(path);
            if (src is null || src.Width <= 0 || src.Height <= 0) return null;
            int w = src.Width, h = src.Height;
            var scale = Math.Min(1.0, 1024.0 / Math.Max(w, h));
            int pw = Math.Max(1, (int)Math.Round(w * scale)), ph = Math.Max(1, (int)Math.Round(h * scale));
            using var pic = scale < 1 ? src.Resize(new SKImageInfo(pw, ph), new SKSamplingOptions(SKCubicResampler.Mitchell)) : src.Copy();
            if (pic is null) return null;
            const int m = 36;   // margin for the labels
            using var surface = SKSurface.Create(new SKImageInfo(pw + m + 8, ph + m + 8));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(22, 22, 26));
            using (var picImage = SKImage.FromBitmap(pic)) canvas.DrawImage(picImage, m, m, new SKSamplingOptions(SKFilterMode.Linear));
            using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 150), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var line = new SKPaint { Color = new SKColor(255, 255, 255, 210), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var ink = new SKPaint { Color = new SKColor(255, 230, 90), IsAntialias = true };
            using var font = new SKFont(SKTypeface.Default, 13);
            for (int k = 0; k <= 10; k++)
            {
                float x = m + pw * k / 10f, y = m + ph * k / 10f;
                canvas.DrawLine(x, m, x, m + ph, shade); canvas.DrawLine(x, m, x, m + ph, line);
                canvas.DrawLine(m, y, m + pw, y, shade); canvas.DrawLine(m, y, m + pw, y, line);
                var label = k == 0 ? "0" : k == 10 ? "1" : "." + k;
                canvas.DrawText(label, x, m - 10, SKTextAlign.Center, font, ink);
                canvas.DrawText(label, m - 6, y + 5, SKTextAlign.Right, font, ink);
            }
            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            return ("data:image/jpeg;base64," + Convert.ToBase64String(data.ToArray()), w, h);
        }
        catch { return null; }
    }

    // ── submit: validate, resolve, queue ──
    private async Task<ToolResultRecord> SubmitAsync(string id, JsonElement args, CancellationToken ct)
    {
        string text = "";
        if (args.TryGetProperty("script", out var se))
            text = se.ValueKind == JsonValueKind.String ? se.GetString() ?? "" : se.ValueKind == JsonValueKind.Object ? se.GetRawText() : "";
        if (text.Trim().Length == 0) return Fail(id, "submit needs script: the video's script as JSON text.");
        var (ids, problem, fixes) = await QueueScriptAsync(text, _request(), ct).ConfigureAwait(false);
        if (problem is not null) return Fail(id, problem);
        var head = ids.Count == 1 ? $"Video job #{ids[0]} queued" : $"{ids.Count} takes queued: " + string.Join(", ", ids.Select(x => "#" + x));
        return Ok(id, $"{head}. {(ids.Count == 1 ? "It renders" : "They render")} in the background on this machine; the finished video{(ids.Count == 1 ? " is" : "s are")} " +
                      "saved under videos/ and reported here when ready." + FixesText(fixes));
    }

    private static string FixesText(List<string> fixes) =>
        fixes.Count == 0 ? "" : "\nFixed in the script before it was queued (say so only if it changes what the operator asked for):\n- " + string.Join("\n- ", fixes);

    /// <summary>Repairs, checks and queues a script (several jobs for takes). Returns the job numbers, or the problems,
    /// and what was repaired. Used by submit and by `vanity-studio render`.</summary>
    public async Task<(List<long> ids, string? problem, List<string> fixes)> QueueScriptAsync(string text, string request, CancellationToken ct)
    {
        JsonNode? node;
        try { node = ParseScript(text); }
        catch (JsonException ex) { return ([], ScriptError(text, ex), []); }
        catch (Exception ex) { return ([], "script is not valid JSON: " + ex.Message, []); }
        if (node is not JsonObject script) return ([], "script must be one JSON object.", []);
        // the brand's colours from brand.json when the script names the brand but not its colours: without a colour (or
        // a logo) the Studio puts every film of a treatment on the same look
        if (script["brand"] is JsonObject sb && sb["colors"] is null && _project.Brand()["colors"] is JsonArray known && known.Count > 0)
            sb["colors"] = known.DeepClone();

        var (cat, catError) = await CatalogAsync(ct).ConfigureAwait(false);
        if (cat is null) return ([], catError!, []);
        // a vertical platform named in the request is a vertical video, whatever shape the script picked; not when the
        // request names its shape itself: "landscape 16:9 ... reels, posts, logos" listed reels as a feature of the editor
        // and turned a landscape tutorial into a reel (2026-10-09)
        string? vertical = null;
        if (Regex.Match(request ?? "", @"\b(reels?|tik\s?tok|shorts|stories)\b", RegexOptions.IgnoreCase) is { Success: true } asked
            && !Regex.IsMatch(request ?? "", @"\b(landscape|horizontal|widescreen|square|portrait)\b|\b(16\s*:\s*9|1\s*:\s*1|4\s*:\s*5)\b", RegexOptions.IgnoreCase)
            && Str(script["format"]) is { } shape && shape != "reel" && cat.Formats.Contains("reel"))
        {
            script["format"] = "reel";
            vertical = asked.Value;
        }
        var fixes = Repair(script, cat, _ai());
        // said out loud: a silent switch left the model fitting pictures to a shape it never chose
        if (vertical is not null) fixes.Insert(0, $"format is \"reel\": the request names {vertical} (a vertical platform)");
        var errors = Validate(script, cat, _project, _ai());
        if (errors.Count > 0)
            return ([], $"The script was not queued: {errors.Count} problem(s). Fix each one and submit again.\n- " + string.Join("\n- ", errors) + FixesText(fixes), fixes);

        var title = Str(script["title"])!.Trim();
        // takes: several variants of the SAME script with different seeds, numbered titles "(take 1 of 3)"; each comes back
        // as its own video with its own report, and the operator picks the keeper
        var takes = (int)Math.Max(1, Math.Min(4, Num(script["takes"]) ?? 1));
        var ids = new List<long>();
        var baseSeed = Num(script["seed"]);
        for (int t = 0; t < takes; t++)
        {
            var scriptN = takes == 1 ? script : (JsonObject)JsonNode.Parse(script.ToJsonString())!;
            if (takes > 1)
            {
                scriptN["title"] = title + $" (take {t + 1} of {takes})";
                // each take its own seed, so the shared script lands on a different look / bed / entrance
                scriptN["seed"] = (long)((baseSeed ?? ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFF) ^ (uint)title.GetHashCode())) + t * 7919);
                scriptN.Remove("takes");
            }
            ids.Add(_jobs.Queue(scriptN, request ?? ""));
        }
        return (ids, null, fixes);
    }

    // ── remix: patch an existing job's script and queue a new render that REUSES every voice line and AI picture whose
    // line or prompt did not change ──
    private async Task<ToolResultRecord> RemixAsync(string id, JsonElement args, CancellationToken ct)
    {
        long jobId = 0;
        var jobStr = "";
        if (args.TryGetProperty("job", out var je))
        {
            if (je.ValueKind == JsonValueKind.Number) je.TryGetInt64(out jobId);
            else if (je.ValueKind == JsonValueKind.String) { jobStr = (je.GetString() ?? "").Trim().TrimStart('#'); long.TryParse(jobStr, out jobId); }
        }
        // "latest", "last" (or missing): the most recent finished video here
        VideoJob? prev = null;
        if (jobId > 0) prev = _jobs.Store.Get(jobId);
        else if (jobStr.Equals("latest", StringComparison.OrdinalIgnoreCase) || jobStr.Equals("last", StringComparison.OrdinalIgnoreCase) || jobStr.Length == 0)
            prev = _jobs.Store.All().FirstOrDefault(j => j.Status == VideoJob.Done);
        if (prev is null) return jobId > 0
            ? Fail(id, $"There is no video job #{jobId} here.")
            : Fail(id, "remix needs job: a number, or \"latest\" for the most recent finished video (none found).");
        jobId = prev.Id;

        if (!args.TryGetProperty("patches", out var pe) || pe.ValueKind != JsonValueKind.Array || pe.GetArrayLength() == 0)
            return Fail(id, "remix needs patches: a non-empty list of {path, value} patches to apply to the previous script.");

        JsonObject script;
        try { script = (JsonObject)JsonNode.Parse(prev.ScriptJson)!; }
        catch (Exception ex) { return Fail(id, "The previous job's script is not valid JSON: " + ex.Message); }
        var applied = new List<string>();
        foreach (var p in pe.EnumerateArray())
        {
            // alt form: {"op":"replace","from":"<text>","to":"<text>"[,"path":"<subtree>"][,"caseSensitive":true]}
            if (p.TryGetProperty("op", out var opJ) && opJ.ValueKind == JsonValueKind.String && opJ.GetString() == "replace")
            {
                var from = p.TryGetProperty("from", out var fj) && fj.ValueKind == JsonValueKind.String ? fj.GetString() ?? "" : "";
                var to = p.TryGetProperty("to", out var tj) && tj.ValueKind == JsonValueKind.String ? tj.GetString() ?? "" : "";
                if (from.Length == 0) return Fail(id, "remix patches[]: op:replace needs from (the text to find).");
                var subPath = p.TryGetProperty("path", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString()?.Trim() ?? "" : "";
                var cs = p.TryGetProperty("caseSensitive", out var csJ) && csJ.ValueKind == JsonValueKind.True;
                var hits = ReplaceStrings(script, subPath, from, to, cs);
                applied.Add($"replace \"{from}\" -> \"{to}\" ({hits} hit{(hits == 1 ? "" : "s")})");
                continue;
            }
            var path = (p.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : "")?.Trim() ?? "";
            if (path.Length == 0) return Fail(id, "remix patches[]: each patch needs a path (e.g. music, scenes[2].line, scenes[0].params.title) OR an op (e.g. op:\"replace\").");
            if (!p.TryGetProperty("value", out var pv)) return Fail(id, $"remix patches[]: {path} needs a value (null removes the field).");
            try { ApplyPatch(script, path, pv.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(pv.GetRawText())); }
            catch (Exception ex) { return Fail(id, $"remix patches[].path '{path}': {ex.Message}"); }
            applied.Add(path);
        }
        script["remix_of"] = jobId;

        var (cat, catError) = await CatalogAsync(ct).ConfigureAwait(false);
        if (cat is null) return Fail(id, catError!);
        var fixes = Repair(script, cat, _ai());
        var errors = Validate(script, cat, _project, _ai());
        if (errors.Count > 0) return Fail(id, $"The remix was not queued: {errors.Count} problem(s) in the patched script.\n- " + string.Join("\n- ", errors) + FixesText(fixes));
        applied.AddRange(fixes.Select(f => "fixed: " + f));

        var title = (Str(script["title"]) ?? prev.Title ?? "Video").Trim();
        if (!title.Contains("remix", StringComparison.OrdinalIgnoreCase)) title += " (remix)";
        script["title"] = title;
        // what the previous job paid for (voice lines, made pictures and clips), offered for reuse by text and prompt
        var prevAssets = ParseObject(prev.AssetsJson);
        var reuse = new JsonObject
        {
            ["voice"] = new JsonArray(((prevAssets["voice"] as JsonObject)?.Select(kv => kv.Value?.DeepClone()) ?? []).Where(v => v is JsonObject).ToArray()),
            ["make"] = new JsonArray(((prevAssets["make"] as JsonObject)?.Select(kv => kv.Value?.DeepClone()) ?? []).Where(v => v is JsonObject).ToArray()),
        };
        var newId = _jobs.Queue(script, _request(), jobId, reuse);
        Log.Info($"[video] remix #{newId} of #{jobId} queued (patches: {string.Join(", ", applied)})");
        return Ok(id, $"Remix #{newId} of job #{jobId} queued. Patches applied: {string.Join(", ", applied)}. Unchanged voice lines and pictures are reused; " +
                      "only what changed is made again. The finished video is reported here when ready.");
    }

    /// <summary>Walk every string in `root` (or in the subtree at `subPath`), replacing every occurrence of `from`
    /// with `to`. Returns the number of hits. Case-insensitive unless `caseSensitive`.</summary>
    internal static int ReplaceStrings(JsonNode root, string subPath, string from, string to, bool caseSensitive)
    {
        JsonNode? start = root;
        if (!string.IsNullOrEmpty(subPath))
        {
            // walk the path by hand (no setter needed, we just need the node)
            var cur = (JsonNode?)root; var i = 0;
            while (i < subPath.Length && cur is not null)
            {
                if (subPath[i] == '[')
                {
                    var j = subPath.IndexOf(']', i); if (j < 0 || !int.TryParse(subPath[(i + 1)..j], out var ix)) return 0;
                    cur = (cur as JsonArray)?[ix]; i = j + 1; if (i < subPath.Length && subPath[i] == '.') i++;
                }
                else
                {
                    var j = i; while (j < subPath.Length && subPath[j] != '.' && subPath[j] != '[') j++;
                    cur = (cur as JsonObject)?[subPath[i..j]]; i = j; if (i < subPath.Length && subPath[i] == '.') i++;
                }
            }
            start = cur;
        }
        if (start is null) return 0;
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var hits = 0;
        void walk(JsonNode? n, JsonObject? parent, string? key, JsonArray? arr, int idx)
        {
            if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains(from, cmp))
            {
                var replaced = Regex.Replace(s, Regex.Escape(from), to.Replace("$", "$$"), caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                if (parent is not null && key is not null) parent[key] = replaced;
                else if (arr is not null) arr[idx] = replaced;
                hits++;
                return;
            }
            if (n is JsonObject o) foreach (var (k, child) in o.ToList()) walk(child, o, k, null, 0);
            else if (n is JsonArray a) for (int k = 0; k < a.Count; k++) walk(a[k], null, null, a, k);
        }
        walk(start, null, null, null, 0);
        return hits;
    }

    /// <summary>Apply a dotted / bracketed path onto a JSON tree. Creates missing containers as needed; value=null removes
    /// the field. Paths like "music", "scenes[2].line", "scenes[0].params.items[1]" all work.</summary>
    internal static void ApplyPatch(JsonNode root, string path, JsonNode? value)
    {
        var tokens = new List<(string? Key, int? Idx)>();
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '[') { var j = path.IndexOf(']', i); if (j < 0 || !int.TryParse(path[(i + 1)..j], out var ix)) throw new ArgumentException("bad index at " + path[i..]); tokens.Add((null, ix)); i = j + 1; if (i < path.Length && path[i] == '.') i++; }
            else { var j = i; while (j < path.Length && path[j] != '.' && path[j] != '[') j++; tokens.Add((path[i..j], null)); i = j; if (i < path.Length && path[i] == '.') i++; }
        }
        JsonNode cur = root;
        for (int t = 0; t < tokens.Count - 1; t++)
        {
            var (k, ix) = tokens[t];
            if (k is not null)
            {
                var obj = cur as JsonObject ?? throw new ArgumentException("expected an object at " + k);
                if (obj[k] is null) obj[k] = tokens[t + 1].Idx is not null ? new JsonArray() : new JsonObject();
                cur = obj[k]!;
            }
            else
            {
                var arr = cur as JsonArray ?? throw new ArgumentException($"expected an array before [{ix}]");
                while (arr.Count <= ix!.Value) arr.Add(null);
                if (arr[ix.Value] is null) arr[ix.Value] = tokens[t + 1].Idx is not null ? new JsonArray() : new JsonObject();
                cur = arr[ix.Value]!;
            }
        }
        var last = tokens[^1];
        if (last.Key is not null)
        {
            var obj = cur as JsonObject ?? throw new ArgumentException("expected an object at " + last.Key);
            if (value is null) obj.Remove(last.Key); else obj[last.Key] = value;
        }
        else
        {
            var arr = cur as JsonArray ?? throw new ArgumentException($"expected an array before [{last.Idx}]");
            if (value is null) { if (last.Idx!.Value < arr.Count) arr.RemoveAt(last.Idx.Value); }
            else { while (arr.Count <= last.Idx!.Value) arr.Add(null); arr[last.Idx.Value] = value; }
        }
    }
    private static readonly string[] ScriptKeys = { "script", "title", "format", "language", "voice", "look", "treatment", "seed", "takes", "speed", "textSoftness", "quality", "grain", "music", "brand", "scenes", "remix_of" };
    // full-frame picture blocks: a picture blown up more than this to cover the frame comes out soft (and the look's
    // grain turns it to noise): a 1200x630 screenshot in a square picture-hero, 2026-10-09
    private static readonly string[] FullFrameBlocks = { "picture-hero", "picture-transform", "picture-poster" };
    private const double MaxUpscale = 1.6;
    private static readonly string[] SceneKeys = { "block", "line", "params", "files", "look", "sfxDrop", "sfxAdd", "textIn", "textSoftness" };
    private static readonly string[] Treatments = { "bold", "calm", "editorial", "kinetic", "cinematic", "retro" };
    private static readonly string[] BrandKeys = { "name", "logo", "url", "colors" };
    private static readonly string[] MakeKeys = { "make", "prompt", "from" };
    private const string FileForms = StudioProject.FileForms;

    /// <summary>
    /// Repairs, before the checks, the slips that have only one right answer, so a model never spends a step on them:
    /// a file slot named after the wrong word when the block has one slot of that kind (device-mockup's "picture" is its
    /// "screen"), a block or a param named a letter or two off, a format said as "9:16" or "story", a number or a yes/no
    /// written as text, a single text where a list is taken, an unknown look, music or treatment (back to auto), colours
    /// that are not colours, a field a script does not have, a voice-over nobody can speak, an address on screen written as a
    /// link (https://www.site.com/#/home reads site.com). Returns what was changed;
    /// what cannot be repaired safely (a text over its limit, a missing file) is left for the checks to name.
    /// </summary>
    internal static List<string> Repair(JsonObject script, VideoCatalog cat, AiOptions ai)
    {
        var fixes = new List<string>();
        script["script"] ??= 1;
        // the format, said in other words
        if (Str(script["format"]) is { } fmt && !cat.Formats.Contains(fmt) && FormatOf(fmt) is { } shape && cat.Formats.Contains(shape))
        {
            script["format"] = shape;
            fixes.Add($"format \"{fmt}\" is \"{shape}\"");
        }
        // fields a script does not have
        foreach (var key in script.Select(kv => kv.Key).Where(k => !ScriptKeys.Contains(k)).ToList())
        {
            var near = Closest(key, ScriptKeys);
            if (near is not null && script[near] is null) { script[near] = script[key]?.DeepClone(); fixes.Add($"\"{key}\" is \"{near}\""); }
            else fixes.Add($"\"{key}\" is not part of a script: left out");
            script.Remove(key);
        }
        Coerce(script, "voice", "bool", fixes, "voice");
        foreach (var k in new[] { "seed", "takes", "speed", "textSoftness" }) Coerce(script, k, "number", fixes, k);
        if (Bool(script["voice"]) == true && VoiceMaker.Candidates(ai).Count == 0)
        {
            script["voice"] = false;
            fixes.Add("voice: no profile can speak, so the video has no voice-over (the music and the words on screen carry it)");
        }
        if (Str(script["look"]) is { } lk && lk != "auto" && !cat.Looks.Any(l => l.Id == lk))
        {
            var near = Closest(lk, cat.Looks.Select(l => l.Id));
            script["look"] = near ?? "auto";
            fixes.Add($"look \"{lk}\" is " + (near is null ? "not a look: auto" : $"\"{near}\""));
        }
        if (Str(script["music"]) is { } mu && mu is not ("auto" or "none") && !cat.Moods.Contains(mu) && !cat.Music.Contains(mu))
        {
            var near = Closest(mu, cat.Moods.Concat(cat.Music));
            script["music"] = near ?? "auto";
            fixes.Add($"music \"{mu}\" is " + (near is null ? "not a mood or a bed: auto" : $"\"{near}\""));
        }
        if (Str(script["treatment"]) is { } tr && !Treatments.Contains(tr))
        {
            var near = Closest(tr, Treatments);
            if (near is null) script.Remove("treatment"); else script["treatment"] = near;
            fixes.Add($"treatment \"{tr}\" is " + (near is null ? "not a treatment: left out" : $"\"{near}\""));
        }
        if (script["brand"] is JsonObject brand)
        {
            foreach (var key in brand.Select(kv => kv.Key).Where(k => !BrandKeys.Contains(k)).ToList()) { brand.Remove(key); fixes.Add($"brand.{key} is not part of brand: left out"); }
            if (brand["colors"] is JsonValue one && Str(one) is { } c1) brand["colors"] = new JsonArray(c1);
            if (brand["colors"] is JsonArray colors)
            {
                var good = colors.Select(Str).Where(c => c is not null && Regex.IsMatch(c, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")).ToList();
                if (good.Count != colors.Count) { fixes.Add("brand.colors: only #rrggbb colours are kept"); brand["colors"] = new JsonArray(good.Select(c => (JsonNode?)c).ToArray()); }
                if (good.Count == 0) brand.Remove("colors");
            }
        }
        if (script["scenes"] is not JsonArray scenes) return fixes;
        for (int i = 0; i < scenes.Count; i++)
        {
            if (scenes[i] is not JsonObject sc) continue;
            // the block, named a little off
            if (Str(sc["block"]) is { } bid && !cat.ById.ContainsKey(bid) && Closest(bid, cat.ById.Keys) is { } nb)
            {
                sc["block"] = nb;
                fixes.Add($"scenes[{i}]: block \"{bid}\" is \"{nb}\"");
            }
            // a list block the Studio does not have ("feature-list", "bullets", "benefits"): steps is the one that shows
            // several points, unnumbered when the script did not say
            else if (Str(sc["block"]) is { } lb && !cat.ById.ContainsKey(lb) && ListWord.IsMatch(lb)
                     && cat.ById.TryGetValue("steps", out var steps) && steps.Params.Any(p => p.Name == "items"))
            {
                sc["block"] = "steps";
                if (sc["params"] is JsonObject lp)
                {
                    if (lp["items"] is null && lp.FirstOrDefault(kv => kv.Value is JsonArray) is { Key: { } listKey })
                    {
                        lp["items"] = lp[listKey]!.DeepClone();
                        lp.Remove(listKey);
                    }
                    if (lp["numbered"] is null && steps.Params.Any(p => p.Name == "numbered")) lp["numbered"] = false;
                }
                fixes.Add($"scenes[{i}]: block \"{lb}\" is \"steps\" (the block that shows a list)");
            }
            if (Str(sc["block"]) is not { } blockId || !cat.ById.TryGetValue(blockId, out var block)) continue;
            var label = $"scenes[{i}] ({block.Id})";
            foreach (var key in sc.Select(kv => kv.Key).Where(k => !SceneKeys.Contains(k)).ToList())
            {
                // a param or a file slot written at the scene's level goes where it belongs
                if (block.Params.Any(p => p.Name == key)) { (sc["params"] as JsonObject ?? (JsonObject)(sc["params"] = new JsonObject()))[key] = sc[key]?.DeepClone(); fixes.Add($"{label}: {key} is a param: moved into params"); }
                else if (block.Files.Any(f => f.Name == key)) { (sc["files"] as JsonObject ?? (JsonObject)(sc["files"] = new JsonObject()))[key] = sc[key]?.DeepClone(); fixes.Add($"{label}: {key} is a file slot: moved into files"); }
                else fixes.Add($"{label}: \"{key}\" is not part of a scene: left out");
                sc.Remove(key);
            }
            if (Str(sc["look"]) is { } sl && !cat.Looks.Any(l => l.Id == sl)) { sc.Remove("look"); fixes.Add($"{label}: look \"{sl}\" is not a look: the film's look"); }
            if (sc["params"] is JsonObject ps)
                foreach (var name in ps.Select(kv => kv.Key).ToList())
                {
                    if (block.Params.Any(p => p.Name == name)) continue;
                    if (name == "links" && block.Params.Any(p => p.Name == "url")) continue;
                    var value = ps[name]?.DeepClone();
                    ps.Remove(name);
                    // a file given as a param goes into its slot
                    if (block.Files.Any(f => f.Name == name)) { (sc["files"] as JsonObject ?? (JsonObject)(sc["files"] = new JsonObject()))[name] = value; fixes.Add($"{label}: params.{name} is a file slot: moved into files"); continue; }
                    var near = Closest(name, block.Params.Select(p => p.Name));
                    if (near is not null && ps[near] is null) { ps[near] = value; fixes.Add($"{label}: params.{name} is params.{near}"); }
                    else fixes.Add($"{label}: params.{name}: {block.Id} has no such param ({string.Join(", ", block.Params.Select(p => p.Name))}): left out");
                }
            if (sc["params"] is JsonObject pz)
                foreach (var (name, p) in block.Params)
                    if (pz[name] is { } v) Coerce(pz, name, p.Type, fixes, $"{label}.params.{name}", p);
            TidyAddresses(sc, block, fixes, label);
            if (sc["files"] is JsonObject files)
                foreach (var slotName in files.Select(kv => kv.Key).ToList())
                {
                    if (block.Files.Any(f => f.Name == slotName)) continue;
                    var value = files[slotName];
                    // the kind of what was given: a made still or a picture file is an image, a clip or a video file a video
                    var kind = value is JsonObject mk ? (Str(mk["make"]) == "clip" ? "video" : "image")
                             : Str(value) is { } r ? (StudioProject.VideoExt.Contains(Path.GetExtension(r).ToLowerInvariant()) ? "video" : "image") : "";
                    var free = block.Files.Where(f => files[f.Name] is null).ToList();
                    var target = free.Where(f => f.S.Kind == kind).Select(f => f.Name).ToList() is { Count: 1 } same ? same[0]
                               : free.Count == 1 ? free[0].Name : Closest(slotName, free.Select(f => f.Name));
                    files.Remove(slotName);
                    if (target is not null) { files[target] = value?.DeepClone(); fixes.Add($"{label}: files.{slotName} is files.{target}"); }
                    else fixes.Add($"{label}: files.{slotName}: {block.Id} has no such slot ({string.Join(", ", block.Files.Select(f => f.Name))}): left out");
                }
        }
        return fixes;
    }

    // A web address in a text: a domain with a letters-only ending, then maybe a port and a path, a query, a fragment (not an
    // e-mail's domain, not the *emphasis* around it). On screen it reads the way people type it: "photovideoeditor.com/app/#/home"
    // did not fit a title card's label, and a step showed the #/route (2026-10-09).
    private static readonly Regex Address = new(
        @"(?<![\w@./-])(?:https?://)?(?:www\.)?((?:[a-z0-9-]+\.)+[a-z]{2,}(?::\d+)?(?:/[\w\-./%~+=&!$:@]*)?)(?:[?#][\w\-./%~+=&!$:@#?]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><paramref name="said"/> with each address in it without https://, www., its query, its #route and its closing
    /// slash, or null when there is none or it already reads that way. The path stays: github.com/owner/repo means the repo.</summary>
    internal static string? Shown(string said)
    {
        var shown = Address.Replace(said, m => m.Groups[1].Value.TrimEnd('/'));
        return shown == said ? null : shown;
    }

    // the addresses in a scene's line and in its texts and lists shown on screen, the way people type them
    private static void TidyAddresses(JsonObject sc, VideoCatalog.Block block, List<string> fixes, string label)
    {
        if (Str(sc["line"]) is { } line && Shown(line) is { } l) { sc["line"] = l; fixes.Add($"{label}.line: the address reads \"{l}\""); }
        if (sc["params"] is not JsonObject ps) return;
        foreach (var (name, p) in block.Params)
        {
            if (OpensAddress(name)) continue;
            if (p.Type == "string" && Str(ps[name]) is { } said && Shown(said) is { } shown)
            {
                ps[name] = shown;
                fixes.Add($"{label}.params.{name}: \"{said}\" is shown as \"{shown}\"");
            }
            else if (p.Type == "list" && ps[name] is JsonArray items)
                for (int k = 0; k < items.Count; k++)
                    if (Str(items[k]) is { } item && Shown(item) is { } tidy)
                    {
                        items[k] = tidy;
                        fixes.Add($"{label}.params.{name}[{k}]: \"{item}\" is shown as \"{tidy}\"");
                    }
        }
    }

    // a param the block opens or reads as an address (a screen's url) or finds on the page (its target): kept as written
    private static bool OpensAddress(string name) =>
        name is "target" or "links" || name.Contains("url", StringComparison.OrdinalIgnoreCase) || name.Contains("href", StringComparison.OrdinalIgnoreCase)
        || name.Contains("src", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex ListWord = new(@"list|feature|bullet|benefit|check|point|highlight|pros", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // the format named the way people say it
    private static string? FormatOf(string said) => Regex.Replace(said.ToLowerInvariant(), @"[\s_-]+", "") switch
    {
        "9:16" or "916" or "vertical" or "story" or "stories" or "tiktok" or "short" or "shorts" or "reels" or "phone" or "1080x1920" => "reel",
        "1:1" or "11" or "squared" or "instagram" or "post" or "1080x1080" => "square",
        "4:5" or "45" or "feed" or "facebook" or "1080x1350" => "portrait",
        "16:9" or "169" or "horizontal" or "wide" or "widescreen" or "youtube" or "1920x1080" => "landscape",
        _ => null,
    };

    /// <summary>A value written in the wrong JSON type, made the type the field takes when that is unambiguous.</summary>
    private static void Coerce(JsonObject o, string key, string type, List<string> fixes, string label, VideoCatalog.Param? p = null)
    {
        var v = o[key];
        if (v is null) return;
        var s = Str(v)?.Trim();
        switch (type)
        {
            case "number" when s is not null && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d):
                o[key] = d; fixes.Add($"{label}: \"{s}\" is the number {N(d)}"); break;
            case "bool" when s is "true" or "false" or "yes" or "no":
                o[key] = s is "true" or "yes"; fixes.Add($"{label}: \"{s}\" is {(s is "true" or "yes" ? "true" : "false")}"); break;
            case "list" when s is not null:
                o[key] = new JsonArray(s); fixes.Add($"{label}: one text is a list of one"); break;
            case "enum" when s is not null && p is not null && !p.Options.Contains(s) && Closest(s, p.Options) is { } opt:
                o[key] = opt; fixes.Add($"{label}: \"{s}\" is \"{opt}\""); break;
        }
    }

    /// <summary>The one name <paramref name="name"/> was meant to be: the same letters in another case, one containing the
    /// other, or one or two letters off; null when none or several fit.</summary>
    private static string? Closest(string name, IEnumerable<string> names)
    {
        var all = names.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        var lower = name.ToLowerInvariant().Replace("_", "-");
        var exact = all.Where(n => n.ToLowerInvariant() == lower).ToList();
        if (exact.Count == 1) return exact[0];
        var near = all.Where(n => Distance(n.ToLowerInvariant(), lower) <= (lower.Length >= 6 ? 2 : 1)).ToList();
        if (near.Count == 1) return near[0];
        var contains = all.Where(n => lower.Length >= 3 && (n.ToLowerInvariant().Contains(lower) || lower.Contains(n.ToLowerInvariant()))).ToList();
        return contains.Count == 1 ? contains[0] : null;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Checks the script against the catalog and returns every problem, each naming its scene and field
    /// (<c>scenes[2] (screen-demo).params.target: ...</c>). Every file reference is resolved now and rewritten to its
    /// canonical form, so the queued job uses exactly the files that were checked.</summary>
    internal static List<string> Validate(JsonObject script, VideoCatalog cat, StudioProject project, AiOptions ai)
    {
        var errors = new List<string>();
        foreach (var (key, _) in script)
            if (!ScriptKeys.Contains(key)) errors.Add($"\"{key}\": not part of a script (a script has {string.Join(", ", ScriptKeys)}).");
        if (Num(script["script"]) != 1) errors.Add("script: must be 1 (the version of the script format).");
        var title = Str(script["title"])?.Trim() ?? "";
        if (title.Length == 0) errors.Add("title: give the video a short title.");
        else if (title.Length > 100) errors.Add($"title: {title.Length} characters; at most 100.");
        var format = Str(script["format"]);
        if (format is null || !cat.Formats.Contains(format))
            errors.Add($"format: {(format is null ? "missing" : $"\"{format}\" is not a format")}; use one of: {string.Join(", ", cat.Formats)}.");
        if (script["language"] is { } lang && Str(lang) is null) errors.Add("language: the language's code or name as text, e.g. \"ro\".");
        if (script["voice"] is { } vo && Bool(vo) is null) errors.Add("voice: true or false.");
        if (script["look"] is { } lk && (Str(lk) is not { } look || (look != "auto" && !cat.Looks.Any(l => l.Id == look))))
            errors.Add($"look: {Show(lk)} is not a look; use auto or one of: {string.Join(", ", cat.Looks.Select(l => l.Id))}.");
        if (script["treatment"] is { } tr && (Str(tr) is not { } treat || !Treatments.Contains(treat)))
            errors.Add($"treatment: {Show(tr)} is not a treatment; use one of: {string.Join(", ", Treatments)}.");
        if (script["seed"] is { } sd && Num(sd) is null)
            errors.Add("seed: a whole number (keeps the same film the same across renders; omit to let the title pick).");
        if (script["takes"] is { } tk && (Num(tk) is not { } takesN || takesN < 1 || takesN > 4 || takesN != Math.Floor(takesN)))
            errors.Add("takes: a whole number 1 to 4 (how many variants to render; each take uses its own seed so they come out different).");
        if (script["speed"] is { } sp && (Num(sp) is not { } spN || spN < 0.4 || spN > 2.5))
            errors.Add("speed: a number between 0.4 and 2.5 (1 is normal; >1 compresses holds so the film reads faster; <1 lets it breathe; voice is NOT re-synthesised).");
        if (script["textSoftness"] is { } ts && (Num(ts) is not { } tsN || tsN < 0 || tsN > 3))
            errors.Add("textSoftness: 0..3 (0 razor-sharp text, 1 default, 2 softer film look, 3 cinematic blur). Set once at script level or per scene (scenes[i].textSoftness).");
        if (script["grain"] is { } gr && (Str(gr) is not { } grS || !new[] { "none", "subtle", "film" }.Contains(grS)))
            errors.Add("grain: none | subtle | film. The film grain the look lays over picture scenes: none (clean, the default), subtle (a third of it), film (the look's own; for a cinematic or retro film of real photographs).");
        if (script["quality"] is { } qty && (Str(qty) is not { } qtyS || !new[] { "low", "medium", "high", "ultra" }.Contains(qtyS)))
            errors.Add("quality: low | medium | high | ultra. Low ~6 Mbps (file-size sensitive), medium ~11 Mbps (default), high ~19 Mbps (archive), ultra ~28 Mbps (zero visible compression).");
        if (script["music"] is { } mu && (Str(mu) is not { } music || (music is not ("auto" or "none") && !cat.Moods.Contains(music) && !cat.Music.Contains(music))))
            errors.Add($"music: {Show(mu)} is not auto, none, a mood ({string.Join(", ", cat.Moods)})" + (cat.Music.Count > 0 ? $" or a music bed ({string.Join(", ", cat.Music.Take(30))})." : "."));
        if (script["brand"] is { } brandNode)
        {
            if (brandNode is not JsonObject brand) errors.Add("brand: an object with name, logo, url and colors (all optional).");
            else
            {
                foreach (var (key, _) in brand)
                    if (!BrandKeys.Contains(key)) errors.Add($"brand.{key}: not part of brand (brand has name, logo, url, colors).");
                if (brand["name"] is { } bn && Str(bn) is null) errors.Add("brand.name: text.");
                if (brand["url"] is { } bu && Str(bu) is null) errors.Add("brand.url: text, the site's address.");
                if (brand["colors"] is { } bc && (bc is not JsonArray colors || colors.Any(c => Str(c) is not { } hex || !Regex.IsMatch(hex, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$"))))
                    errors.Add("brand.colors: a list of colours written as \"#rrggbb\".");
                if (brand["logo"] is { } bl)
                {
                    if (Str(bl) is not { } logoRef) errors.Add("brand.logo: a file reference (its path in the project, its name, or media:logo).");
                    else
                    {
                        var (f, err) = project.Resolve(logoRef);
                        if (f is null) errors.Add("brand.logo: " + err);
                        else if (!IsImage(f)) errors.Add($"brand.logo: {f.Name} is not a picture.");
                        else brand["logo"] = f.Canonical;
                    }
                }
            }
        }
        if (script["scenes"] is not JsonArray scenes || scenes.Count == 0)
        {
            errors.Add("scenes: the script needs its scenes, in order, one per moment of the request.");
            return errors;
        }

        bool stills = false, clips = false;
        for (int i = 0; i < scenes.Count; i++)
        {
            if (scenes[i] is not JsonObject sc) { errors.Add($"scenes[{i}]: a scene is an object {{\"block\": ..., \"line\": ..., \"params\": {{...}}, \"files\": {{...}}}}."); continue; }
            var blockId = Str(sc["block"]);
            if (blockId is null || !cat.ById.TryGetValue(blockId, out var block))
            {
                // the names right here: sent to look them up, a model guessed more names, probed with a made-up block and
                // searched the operator's files for a catalog (2026-10-09)
                errors.Add($"scenes[{i}]: {(blockId is null ? "no block" : $"unknown block \"{blockId}\"")}; the only blocks are: {string.Join(", ", cat.ById.Keys)}" +
                           (cat.ById.ContainsKey("steps") ? "; points or features in a list are steps (2-5 items, \"numbered\": false)" : "") + ".");
                continue;
            }
            var label = $"scenes[{i}] ({block.Id})";
            if (block.Overlay && i == 0)
                errors.Add($"{label}: an overlay rides on the previous scene, so it cannot be the first scene; put it after the scene it labels.");
            foreach (var (key, _) in sc)
                if (!SceneKeys.Contains(key)) errors.Add($"{label}: \"{key}\" is not part of a scene (a scene has {string.Join(", ", SceneKeys)}).");
            if (sc["sfxDrop"] is { } drop && drop is not JsonArray) errors.Add($"{label}.sfxDrop: a list of sfx names, e.g. [\"whoosh\",\"ding\"].");
            if (sc["sfxAdd"] is { } addSfx && addSfx is not JsonArray) errors.Add($"{label}.sfxAdd: a list of {{sfx, at, volume?, dur?}} entries, e.g. [{{\"sfx\":\"ding\",\"at\":\"word:Websisco\"}}].");
            if (sc["textIn"] is { } tin && tin is not JsonObject) errors.Add($"{label}.textIn: an object like {{\"type\":\"fade\",\"dur\":0.5}} overriding the text-in on every text layer of this scene.");
            if (sc["look"] is { } sl && (Str(sl) is not { } slv || !cat.Looks.Any(l => l.Id == slv)))
                errors.Add($"{label}.look: {Show(sl)} is not a look; use one of: {string.Join(", ", cat.Looks.Select(l => l.Id))}, or leave it out to use the film's look.");
            if (sc["line"] is { } ln)
            {
                if (Str(ln) is not { } line) errors.Add($"{label}.line: the scene's sentence as text.");
                else
                {
                    var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                    if (words > 37) errors.Add($"{label}.line: {words} words is more than one scene can carry (at most 15 s at about 2.5 words a second); split it over two scenes.");
                    // a block that shows the line on screen when its text param is empty holds the line to that param's limit
                    var shown = line.Trim();
                    foreach (var (name, p) in block.Params)
                        if (p.Line && p.Max is { } lmax && shown.Length > lmax && (Str((sc["params"] as JsonObject)?[name])?.Trim().Length ?? 0) == 0)
                            errors.Add($"{label}.line: {block.Id} shows the line on screen as params.{name} (empty here), and it is {shown.Length} characters; " +
                                       $"params.{name} takes at most {N(lmax)}. Write the on-screen words in params.{name} (at most {N(lmax)} characters) or shorten the line.");
                }
            }
            if (sc["params"] is { } pn)
            {
                if (pn is not JsonObject ps) errors.Add($"{label}.params: an object of the block's params.");
                else foreach (var (name, value) in ps)
                {
                    var spec = block.Params.FirstOrDefault(p => p.Name == name).P;
                    // the page's links are the renderer's own (it measures them on the page it captures for a block with an
                    // address), so a script that carries them back is not refused for them
                    if (spec is null && name == "links" && block.Params.Any(p => p.Name == "url")) continue;
                    if (spec is null)
                    {
                        errors.Add($"{label}.params.{name}: unknown param; {block.Id} takes " +
                                   (block.Params.Count > 0 ? string.Join(", ", block.Params.Select(p => p.Name)) + "." : "no params."));
                        continue;
                    }
                    if (CheckParam(spec, value) is { } problem) errors.Add($"{label}.params.{name}: {problem}");
                }
                // a value the block shows on its own (an offer's figure) is said once: the text it must not repeat in is refused
                if (pn is JsonObject pz)
                    foreach (var (name, p) in block.Params)
                    {
                        if (p.NotIn is not { Count: > 0 } || Str(pz[name])?.Trim() is not { Length: > 0 } val) continue;
                        foreach (var other in p.NotIn)
                        {
                            var own = Str(pz[other])?.Trim() ?? "";
                            var shownLine = own.Length == 0 && block.Params.Any(x => x.Name == other && x.P.Line);
                            var text = shownLine ? Str(sc["line"])?.Trim() ?? "" : own;
                            if (text.Length > 0 && Repeats(val, text))
                                errors.Add($"{label}.{(shownLine ? "line" : "params." + other)}: repeats params.{name} (\"{val}\"), which {block.Id} already shows " +
                                           $"on its own, largest; leave it out of the {(shownLine ? "line" : other)}.");
                        }
                    }
            }
            var given = new HashSet<string>(StringComparer.Ordinal);
            if (sc["files"] is { } fn)
            {
                if (fn is not JsonObject files) errors.Add($"{label}.files: an object of the block's file slots.");
                else foreach (var (slotName, value) in files.ToList())
                {
                    var slot = block.Files.FirstOrDefault(s => s.Name == slotName).S;
                    var at = $"{label}.files.{slotName}";
                    if (slot is null)
                    {
                        errors.Add($"{at}: unknown file slot; {block.Id} takes " +
                                   (block.Files.Count > 0 ? string.Join(", ", block.Files.Select(s => s.Name)) + "." : "no files."));
                        continue;
                    }
                    if (value is null) continue;   // null = no file: the block's fallback
                    given.Add(slotName);
                    if (Str(value) is { } reference)
                    {
                        var (f, err) = project.Resolve(reference);
                        if (f is null) { errors.Add($"{at}: {err}"); continue; }
                        if (slot.Kind == "image" && !IsImage(f)) { errors.Add($"{at}: {f.Name} is not a picture; this slot takes a picture."); continue; }
                        // a full-frame picture must be big enough to cover the frame without being blown up
                        if (slot.Kind == "image" && FullFrameBlocks.Contains(block.Id) && ImageSize(f.Path) is var (iw, ih) && iw > 0 && ih > 0)
                        {
                            var (fw, fh) = FormatSize(Str(script["format"]));
                            var up = Math.Max((double)fw / iw, (double)fh / ih);
                            if (up > MaxUpscale)
                                errors.Add($"{at}: {f.Name} is {iw}x{ih}, and {block.Id} fills the {fw}x{fh} frame with it, so it would be blown up {up.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}x and look soft. " +
                                           "Use a larger picture, put this one where it is shown as a card (offer-poster's picture, a device-mockup screen), or show the site live with screen-demo.");
                        }
                        if (slot.Kind == "video" && !IsVideo(f)) { errors.Add($"{at}: {f.Name} is not a video clip; this slot takes a clip (a file, or {{\"make\": \"clip\", \"from\": <picture>, \"prompt\": ...}})."); continue; }
                        files[slotName] = f.Canonical;
                    }
                    else if (value is JsonObject make)
                    {
                        foreach (var (key, _) in make)
                            if (!MakeKeys.Contains(key)) errors.Add($"{at}.{key}: not part of a make (a make has make, prompt, from).");
                        var kind = Str(make["make"]);
                        if (kind is not ("still" or "clip")) { errors.Add($"{at}.make: {Show(make["make"])} is not still or clip."); continue; }
                        if (kind == "still" && slot.Kind == "video") errors.Add($"{at}.make: this slot takes a clip; a still is a picture.");
                        if (kind == "clip" && slot.Kind == "image") errors.Add($"{at}.make: this slot takes a picture; make a still.");
                        if ((Str(make["prompt"])?.Trim().Length ?? 0) == 0) errors.Add($"{at}.prompt: say what to make, in one sentence.");
                        if (kind == "still") stills = true; else clips = true;
                        if (make["from"] is { } fromNode)
                        {
                            if (Str(fromNode) is not { } fromRef) errors.Add($"{at}.from: a picture's file reference (its path in the project, its name, or media:logo).");
                            else
                            {
                                var (f, err) = project.Resolve(fromRef);
                                if (f is null) errors.Add($"{at}.from: {err}");
                                else if (!IsImage(f)) errors.Add($"{at}.from: {f.Name} is not a picture.");
                                else make["from"] = f.Canonical;
                            }
                        }
                        else if (kind == "clip") errors.Add($"{at}.from: a clip is made from a picture; name it in from.");
                    }
                    else errors.Add($"{at}: a file is {FileForms}.");
                }
            }
            foreach (var (slotName, slot) in block.Files)
                if (slot.Required && !given.Contains(slotName))
                    errors.Add($"{label}.files.{slotName}: {block.Id} needs {(slot.Kind == "video" ? "a clip" : slot.Kind == "image" ? "a picture" : "a file")} here: {FileForms}.");
        }

        // what the job will pay for must have a profile that can make it: said now, not after the job is queued
        try
        {
            if (Bool(script["voice"]) == true && VoiceMaker.Candidates(ai).Count == 0)
                errors.Add("voice: no AI profile can speak the lines (a Gemini, OpenAI or Alibaba API-key profile with the voice role: /voice <profile>); set \"voice\": false.");
            var drawers = ImageMaker.Candidates(ai);
            if (stills && drawers.Count == 0)
                errors.Add("files: no AI profile can make pictures (OpenAI, the Antigravity login or Alibaba); use the project's own files instead of {\"make\": \"still\"}.");
            else if (stills && drawers.All(c => VideoJobs.IsSpent(c.Name, out _)))
                errors.Add("files: the image quota of " + string.Join(", ", drawers.Select(c => { VideoJobs.IsSpent(c.Name, out var u); return $"{c.Name} (until {u:HH:mm})"; })) +
                           " is used up, so no picture can be made now: use the site's own pictures (web read / web download), screens of the site " +
                           "(screen-demo), the project's files, or blocks that need no picture, instead of {\"make\": \"still\"}.");
            if (clips && ClipMaker.Candidates(ai).Count == 0)
                errors.Add("files: no AI profile can make clips (an Alibaba DashScope API key); use the project's own clips or a block that needs no clip.");
        }
        catch { }   // a profile lookup that fails never blocks the submit: prepare reports what could not be made
        return errors;
    }

    private static bool IsImage(FileRef f) => f.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    private static bool IsVideo(FileRef f) => f.Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="text"/> says <paramref name="value"/> again: the number in the value (its digits,
    /// separators ignored) as a number in the text, or, for a value without digits, the value's words.</summary>
    internal static bool Repeats(string value, string text)
    {
        static string Digits(string s) => Regex.Replace(s, @"\D", "");
        static IEnumerable<string> Numbers(string s) => Regex.Matches(s, @"\d[\d.,\s]*\d|\d").Select(m => Digits(m.Value)).Where(d => d.Length > 0);
        var nums = Numbers(value).ToList();
        if (nums.Count == 0) return text.Replace("*", "").Contains(value.Replace("*", "").Trim(), StringComparison.OrdinalIgnoreCase);
        var inText = Numbers(text).ToHashSet();
        return nums.Any(inText.Contains);
    }

    /// <summary>One param value against its catalog type; null when it fits (null = the param's default).</summary>
    internal static string? CheckParam(VideoCatalog.Param p, JsonNode? v)
    {
        if (v is null) return null;
        switch (p.Type)
        {
            case "string":
                if (Str(v) is not { } s) return "must be text.";
                if (p.Max is { } max && s.Length > max) return $"{s.Length} characters; at most {N(max)}.";
                return null;
            case "number":
                if (Num(v) is not { } d) return "must be a number.";
                if ((p.Min is { } lo && d < lo) || (p.Max is { } hi && d > hi))
                    return p.Min is not null && p.Max is not null ? $"must be between {N(p.Min.Value)} and {N(p.Max.Value)}."
                         : p.Min is not null ? $"must be at least {N(p.Min.Value)}." : $"must be at most {N(p.Max!.Value)}.";
                return null;
            case "bool":
                return Bool(v) is null ? "must be true or false." : null;
            case "enum":
                return Str(v) is { } e && p.Options.Contains(e) ? null : $"{Show(v)} is not one of: {string.Join(", ", p.Options)}.";
            case "list":
                if (v is not JsonArray a) return "must be a list.";
                if ((p.Min is { } lmin && a.Count < lmin) || (p.Max is { } lmax && a.Count > lmax))
                    return $"needs {Range(p.Min, p.Max)} items; it has {a.Count}.";
                for (int k = 0; k < a.Count; k++)
                {
                    if (p.Of == "string" && Str(a[k]) is null) return $"item {k} must be text.";
                    if (p.ItemMax is { } im && Str(a[k]) is { } item && item.Trim().Length > im) return $"item {k} is {item.Trim().Length} characters; each item takes at most {N(im)}.";
                    if (p.Of == "number" && Num(a[k]) is null) return $"item {k} must be a number.";
                }
                return null;
            case "point":
                if (v is not JsonArray pt || pt.Count != 2 || pt.Any(x => Num(x) is not (>= 0 and <= 1)))
                    return "must be [x, y], two fractions between 0 and 1 (across, down); read them off make_video action=look.";
                return null;
            case "box":
                if (v is not JsonArray bx || bx.Count != 4 || bx.Any(x => Num(x) is not (>= 0 and <= 1)))
                    return "must be [x, y, w, h], four fractions between 0 and 1 (left, top, width, height); read them off make_video action=look.";
                double bxx = Num(bx[0])!.Value, bxy = Num(bx[1])!.Value, bxw = Num(bx[2])!.Value, bxh = Num(bx[3])!.Value;
                if (bxw <= 0 || bxh <= 0) return "needs a width and a height above 0.";
                if (bxx + bxw > 1.0001 || bxy + bxh > 1.0001) return "runs off the picture: x + w and y + h must stay at most 1.";
                return null;
            default:
                return null;
        }
    }

    private static string Range(double? min, double? max) =>
        min is not null && max is not null ? $"{N(min.Value)}-{N(max.Value)}" : min is not null ? $"at least {N(min.Value)}" : $"at most {N(max!.Value)}";
    private static string Show(JsonNode? v) => v is null ? "null" : Str(v) is { } s ? $"\"{s}\"" : v.ToJsonString();
    /// <summary>The script arrives as a string the model typed; a trailing comma, one brace too many at the end or a
    /// stray tail after the root object are its usual slips (motion_studio lost five calls to them in run #281). Those
    /// are repaired; a real break still throws, and <see cref="ScriptError"/> shows the model the spot.</summary>
    public static JsonNode? ParseScript(string text)
    {
        var lenient = new JsonNodeOptions();
        var docOpts = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        try { return JsonNode.Parse(text, lenient, docOpts); }
        catch (JsonException)
        {
            // the first complete {...} at the top level, string-aware: what follows it (an extra brace, a comma, prose) is dropped
            int start = text.IndexOf('{');
            if (start < 0) throw;
            int depth = 0; bool inStr = false, esc = false;
            for (int i = start; i < text.Length; i++)
            {
                char ch = text[i];
                if (inStr) { if (esc) esc = false; else if (ch == '\\') esc = true; else if (ch == '"') inStr = false; continue; }
                if (ch == '"') inStr = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    if (i == text.Length - 1) throw;   // nothing to drop: the break is inside
                    return JsonNode.Parse(text.Substring(start, i - start + 1), lenient, docOpts);
                }
            }
            throw;
        }
    }

    internal static string ScriptError(string text, JsonException ex)
    {
        long line = ex.LineNumber ?? 0, col = ex.BytePositionInLine ?? 0;
        int at = 0;
        for (long l = 0; l < line && at < text.Length; l++) { int nl = text.IndexOf('\n', at); if (nl < 0) { at = text.Length; break; } at = nl + 1; }
        at = (int)Math.Min(text.Length, at + col);
        int from = Math.Max(0, at - 70), to = Math.Min(text.Length, at + 40);
        var snippet = text.Substring(from, at - from) + " <<HERE>> " + text.Substring(at, to - at);
        return "script is not valid JSON: " + ex.Message + "\nnear: " + snippet.Replace("\n", " ").Replace("\r", "") + "\nFix the JSON at that spot and submit again.";
    }

    // ── status / cancel ──
    private string Status()
    {
        var jobs = _jobs.Store.All().Take(10).ToList();
        if (jobs.Count == 0) return "No video jobs here yet.";
        return "Video jobs here, newest first:\n" + string.Join("\n", jobs.Select(j => "- " + VideoJobs.Line(j)));
    }

    private string Cancel(JsonElement args)
    {
        long id = 0;
        if (args.TryGetProperty("job", out var je))
        {
            if (je.ValueKind == JsonValueKind.Number) je.TryGetInt64(out id);
            else if (je.ValueKind == JsonValueKind.String) long.TryParse((je.GetString() ?? "").Trim().TrimStart('#'), out id);
        }
        if (id > 0) return _jobs.Cancel(id);
        // "stop" without a number: every video still being made here
        var open = _jobs.Store.All().Where(j => j.Open && !j.Finished).ToList();
        if (open.Count == 0) return "No video is being made here; there is nothing to cancel.";
        return string.Join("\n", open.Select(j => _jobs.Cancel(j.Id)));
    }

    // ── the catalog ──
    /// <summary>The Studio's address: VANITY_STUDIO_URL, else the StudioUrl setting, else photovideoeditor.com/app/.</summary>
    public static string StudioUrl()
    {
        var u = Environment.GetEnvironmentVariable("VANITY_STUDIO_URL");
        if (string.IsNullOrWhiteSpace(u)) u = AgentConfig.Setting("StudioUrl");
        if (string.IsNullOrWhiteSpace(u)) u = StudioOps.DefaultStudioUrl;
        u = u.Trim();
        return u.EndsWith('/') ? u : u + "/";
    }

    /// <summary>The Studio's block catalog, from <c>&lt;studio&gt;js/video/catalog.json</c>, cached 10 minutes.</summary>
    public static async Task<(VideoCatalog? cat, string? error)> CatalogAsync(CancellationToken ct)
    {
        var studio = StudioUrl();
        string json;
        if (_catalogCache.TryGetValue(studio, out var c) && c.at > DateTime.UtcNow.AddMinutes(-10)) json = c.json;
        else
        {
            try
            {
                using var resp = await _http.GetAsync(studio + "js/video/catalog.json", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return (null, NoCatalog(studio, $"HTTP {(int)resp.StatusCode}"));
                json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (null, NoCatalog(studio, ex.Message)); }
        }
        try
        {
            var cat = VideoCatalog.Parse(json);
            if (cat.Blocks.Count == 0) return (null, NoCatalog(studio, "it lists no blocks"));
            _catalogCache[studio] = (DateTime.UtcNow, json);
            return (cat, null);
        }
        catch (Exception ex) { return (null, NoCatalog(studio, "what it serves there is not a catalog: " + ex.Message)); }
    }

    private static string NoCatalog(string studio, string why) =>
        $"The Studio at {studio} has no video catalog ({why}). No video can be made until js/video/catalog.json is reachable there; tell the operator (is this machine online?).";

    /// <summary>The catalog as compact text for a model: per block its id, category, summary, use when / avoid when,
    /// files and params (type, default, options, limits); then the looks, moods, music and formats.</summary>
    public static string Describe(VideoCatalog cat)
    {
        var sb = new StringBuilder();
        sb.Append($"Vanity Studio video blocks (catalog v{cat.Version}). A script is {{\"script\": 1, \"title\": \"...\", \"format\": \"reel\", \"language\": \"ro\", \"voice\": false, \"look\": \"<the look whose mood fits, or auto>\", \"music\": \"<the bed whose mood fits, or none>\", ");
        sb.Append("\"brand\": {\"name\": \"<the brand's name>\", \"logo\": \"media:logo\", \"url\": \"<its site>\"}, \"scenes\": [...]}. ");
        sb.Append($"A scene is {{\"block\": \"<id>\", \"line\": \"<its sentence>\", \"params\": {{...}}, \"files\": {{\"<slot>\": <file>}}}}. ");
        sb.Append("Every param has a default: give only what the request needs.\n");
        sb.Append("Text limits are hard: every text, list and list item below says how many characters or items it takes, and submit refuses " +
                  "anything longer and names it. A param marked \"shows the line\" puts the scene's line on screen when it is left empty, so the line " +
                  "must then fit that limit too. Write on-screen text to fit; a spoken line can be longer than the words on screen.\n");
        sb.Append("formats: " + string.Join(" | ", cat.Formats.Select(f => f is "reel" or "square" or "portrait" or "landscape" ? $"{f} {FormatSize(f).w}x{FormatSize(f).h}" : f)) + "\n");
        sb.Append("treatment (optional, picks the stance of the film: look pool, music mood, caption emphasis): " +
                  "bold (bright looks, upbeat beds, underline on *marked* words) | calm (paper looks, calm beds, plain) | " +
                  "editorial (serif looks, calm/warm beds, highlight on *marked*) | kinetic (bold looks, upbeat beds, box on *marked*) | " +
                  "cinematic (dark premium looks, cinematic beds, plain) | retro (coral/daisy/poster, warm beds, circle on *marked*)\n");
        sb.Append("looks: auto" + string.Concat(cat.Looks.Select(l => " | " + l.Id + (l.Summary.Length > 0 ? " (" + l.Summary + ")" : ""))) + "\n");
        sb.Append("music: auto | none | moods: " + string.Join(", ", cat.Moods) +
                  (cat.Music.Count > 0 ? " | beds: " + string.Join(", ", cat.Music.Select(m => cat.MusicMoods.TryGetValue(m, out var mood) ? $"{m} ({mood})" : m)) : "") + "\n");
        sb.Append("seed (optional): a whole number keeps the same film across renders; omit to let the title pick, so a second render gives a different look / bed / entrance.\n");
        sb.Append("takes (optional, 1..4): render N variants of the SAME script, each with its own seed, so the operator picks the keeper. Use 2-3 for an ad the operator will A/B test; use 1 (the default) for a tutorial or a logo reveal.\n");
        sb.Append("speed (optional, 0.4..2.5): 1 is normal; >1 shortens silent holds and makes the film read faster; <1 lets it breathe. Voice lines keep their recorded length - speed only compresses the gaps.\n");
        sb.Append("textSoftness (optional, 0..3; default 1): dials the automatic drop-shadow under big text. 0 = razor sharp; 1 = default; 2 = softer film look; 3 = cinematic blur. Also settable per scene (scenes[i].textSoftness).\n");
        sb.Append("quality (optional, low | medium | high | ultra; default medium): H.264 bitrate tier for the exported MP4. low ~6 Mbps (small file), medium ~11 Mbps (default), high ~19 Mbps, ultra ~28 Mbps (no visible compression on fast motion / highlight bands).\n");
        sb.Append("grain (optional, none | subtle | film; default none): the film grain the look lays over picture scenes. none keeps pictures, screenshots and product shots clean; subtle is a third of it; film is the look's own, only for a cinematic or retro film of real photographs.\n");
        sb.Append("a scene may name its own look (scenes[i].look = <look id>) for a multi-act film: a bold open, a calm middle, a cinematic close.\n");
        sb.Append("a scene may drop block-added SFX (scenes[i].sfxDrop = [\"whoosh\",\"ding\"]), add extra SFX anchored to a word or a time (scenes[i].sfxAdd = [{\"sfx\":\"ding\",\"at\":\"word:Websisco\",\"volume\":0.6}]; `at` is a number of seconds within the scene, or \"word:<text>\" / \"line:<text>\" / \"scene-end\"), and/or override the text-in on all its text layers (scenes[i].textIn = {\"type\":\"fade\"|\"rise\"|\"drop\"|\"pop\"|\"blur\"|\"slide\"|\"slideRight\"|\"typewriter\"|\"scramble\", \"dur\": 0.6, \"stagger\": 0.05, \"by\": \"char\"|\"word\"|\"line\"}).\n");
        sb.Append("\nremix (action=remix job=<id> patches=[{path, value}, ...]): change ONE or a FEW fields of an already-rendered job and render again. Scenes whose line and files did NOT change reuse their voice and AI pictures (0 AI calls). Common patches:\n" +
                  "  - swap the bed:                 {\"path\":\"music\", \"value\":\"<bed id | mood | auto | none>\"}\n" +
                  "  - change treatment or look:     {\"path\":\"treatment\", \"value\":\"calm\"}  or  {\"path\":\"look\", \"value\":\"deep-navy\"}\n" +
                  "  - swap a scene's picture:       {\"path\":\"scenes[0].files.picture\", \"value\":\"media/photo.png\"}\n" +
                  "  - rewrite a line:               {\"path\":\"scenes[2].line\", \"value\":\"the new sentence\"}\n" +
                  "  - edit a param:                 {\"path\":\"scenes[1].params.headline\", \"value\":\"Site *gata* azi\"}\n" +
                  "  - change a scene's block:       {\"path\":\"scenes[3].block\", \"value\":\"cta-close\"}\n" +
                  "  - add a scene at the end:       {\"path\":\"scenes[6]\", \"value\":{\"block\":\"footage\",\"line\":\"...\",\"files\":{\"clip\":\"media/clip.mp4\"}}}\n" +
                  "  - remove a scene:               {\"path\":\"scenes[4]\", \"value\":null}\n" +
                  "  - turn voice off:               {\"path\":\"voice\", \"value\":false}\n" +
                  "  - drop a sound effect:          {\"path\":\"scenes[2].sfxDrop\", \"value\":[\"whoosh\"]}\n" +
                  "  - drop every SFX on a scene:    {\"path\":\"scenes[2].sfxDrop\", \"value\":[\"*\"]}\n" +
                  "  - speed the film up:            {\"path\":\"speed\", \"value\":1.3}\n" +
                  "  - slow it down:                 {\"path\":\"speed\", \"value\":0.8}\n" +
                  "  - soften too-crisp text:        {\"path\":\"textSoftness\", \"value\":2}\n" +
                  "  - raise quality (less blocking): {\"path\":\"quality\", \"value\":\"high\"}\n" +
                  "  - add an SFX on a word:         {\"path\":\"scenes[2].sfxAdd\", \"value\":[{\"sfx\":\"ding\",\"at\":\"word:Websisco\"}]}\n" +
                  "  - swap one word everywhere:     {\"op\":\"replace\", \"from\":\"website\", \"to\":\"landing page\"}\n" +
                  "  - swap one word in one scene:   {\"op\":\"replace\", \"path\":\"scenes[2]\", \"from\":\"ieftin\", \"to\":\"gratuit\"}\n" +
                  "  - change text-in on one scene:  {\"path\":\"scenes[0].textIn\", \"value\":{\"type\":\"fade\",\"dur\":0.5}}\n" +
                  "The patched script is validated against the catalog; a bad patch is rejected naming its path.\n");
        sb.Append("a file: " + FileForms + "\n");
        foreach (var b in cat.Blocks)
        {
            sb.Append($"\n## {b.Id} ({b.Category}{(b.Natural is { } nat ? $", about {N(nat)} s" : "")}" +
                      (b.Overlay ? ", an overlay on the previous scene, never the first scene" : "") + ")\n");
            if (b.Summary.Length > 0) sb.Append(b.Summary + "\n");
            if (b.UseWhen.Length > 0) sb.Append("use when: " + b.UseWhen + "\n");
            if (b.AvoidWhen.Length > 0) sb.Append("avoid when: " + b.AvoidWhen + "\n");
            if (b.Files.Count > 0)
                sb.Append("files: " + string.Join("; ", b.Files.Select(f => $"{f.Name} ({f.S.Kind}, {(f.S.Required ? "required" : "optional")})" + (f.S.Summary.Length > 0 ? ": " + f.S.Summary : ""))) + "\n");
            if (b.Params.Count > 0)
            {
                sb.Append("params:\n");
                foreach (var (name, p) in b.Params)
                    sb.Append($"- {name}: {TypeText(p)}" + (p.Default is not null ? ", default " + p.Default.ToJsonString() : "") + (p.Summary.Length > 0 ? ". " + p.Summary : "") + "\n");
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Every block in one line: what it shows, when to use it, its params with their limits, its file slots. It goes into the
    /// instructions every turn, so a script is written from the real blocks: told to look them up, a model guessed names
    /// ("feature-list", "list"), sent an empty script and a made-up block to read the refusal, and searched the operator's
    /// files for a catalog (2026-10-09).
    /// </summary>
    public static string Brief(VideoCatalog cat) =>
        $"formats: {string.Join(", ", cat.Formats)}. looks: auto, {string.Join(", ", cat.Looks.Select(l => l.Id))}. music: auto, none, " +
        $"{string.Join(", ", cat.Moods)}.\n" + string.Join("\n", cat.Blocks.Select(b =>
    {
        var what = b.Summary.Split(". ")[0].TrimEnd('.');
        var use = b.UseWhen.Length > 0 ? $" Use for: {Clip(b.UseWhen, 90)}." : "";
        var ps = b.Params.Select(x => x.P.Type switch
        {
            "string" => x.P.Max is { } m ? $"{x.Name} (≤{N(m)} chars)" : x.Name,
            "list" => $"{x.Name} ({(x.P.Min is not null || x.P.Max is not null ? Range(x.P.Min, x.P.Max) + " " : "")}items{(x.P.ItemMax is { } im ? $", each ≤{N(im)} chars" : "")})",
            "enum" => $"{x.Name} ({string.Join("|", x.P.Options)})",
            "bool" => $"{x.Name} (true|false)",
            _ => x.Name,
        });
        var files = b.Files.Select(f => f.Name + (f.S.Required ? " (required)" : ""));
        return $"- {b.Id}{(b.Overlay ? " (an overlay on the previous scene, never first)" : "")}: {what}.{use} params: {string.Join(", ", ps)}" +
               (b.Files.Count > 0 ? $"; files: {string.Join(", ", files)}" : "");
    }));

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    private static string TypeText(VideoCatalog.Param p) => p.Type switch
    {
        "string" => "text" + (p.Max is { } m ? $", at most {N(m)} characters" : "") + (p.Line ? ", shows the line" : "") +
                    (p.NotIn is { Count: > 0 } ni ? ", never repeated in " + string.Join(" or ", ni) : ""),
        "number" => "number" + (p.Min is not null || p.Max is not null ? " " + Range(p.Min, p.Max) : ""),
        "bool" => "true or false",
        "enum" => "one of " + string.Join(" | ", p.Options),
        "list" => $"list of {(p.Min is not null || p.Max is not null ? Range(p.Min, p.Max) + " " : "")}{(p.Of.Length > 0 ? p.Of : "item")}s" +
                  (p.ItemMax is { } im ? $", each at most {N(im)} characters" : ""),
        "point" => "point [x, y] in fractions 0..1 of the picture (read it off make_video action=look)",
        "box" => "box [x, y, w, h] in fractions 0..1 of the picture (read it off make_video action=look)",
        _ => p.Type,
    };
}

/// <summary>The Studio's block catalog (contract section 5): formats, looks, moods, music beds and the blocks with their
/// file slots and typed params.</summary>
public sealed class VideoCatalog
{
    public sealed record Slot(string Kind, bool Required, string Summary);
    /// <summary>A block's param. <c>ItemMax</c>: the characters each item of a list takes; <c>Line</c>: the block shows the
    /// scene's line here when the param is left empty, so the line is held to <c>Max</c>.</summary>
    public sealed record Param(string Type, JsonNode? Default, double? Min, double? Max, List<string> Options, string Of, string Summary,
        double? ItemMax = null, bool Line = false, List<string>? NotIn = null);
    public sealed class Block
    {
        public string Id = "", Category = "", Summary = "", UseWhen = "", AvoidWhen = "";
        public double? Natural;
        /// <summary>An overlay rides on the previous scene (lower-third): never the first scene.</summary>
        public bool Overlay;
        public List<(string Name, Slot S)> Files = new();
        public List<(string Name, Param P)> Params = new();
    }

    public int Version;
    public List<string> Formats = new();
    public List<(string Id, string Summary)> Looks = new();
    public List<string> Moods = new();
    public List<string> Music = new();
    /// <summary>Each music bed's character (catalog "musicMoods": bed id → its mood in words).</summary>
    public Dictionary<string, string> MusicMoods = new(StringComparer.Ordinal);
    public List<Block> Blocks = new();
    public Dictionary<string, Block> ById = new(StringComparer.Ordinal);

    public static VideoCatalog Parse(string json)
    {
        var root = JsonNode.Parse(json, new JsonNodeOptions(), new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
                   ?? throw new JsonException("the catalog is not a JSON object");
        static List<string> Ids(JsonNode? n) => (n as JsonArray ?? new JsonArray())
            .Select(x => Str(x) ?? Str((x as JsonObject)?["id"])).Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToList();
        var cat = new VideoCatalog
        {
            Version = (int)(Num(root["version"]) ?? 1),
            Formats = Ids(root["formats"]),
            Moods = Ids(root["moods"]),
            Music = Ids(root["music"]),
        };
        if (cat.Formats.Count == 0) cat.Formats = new List<string> { "reel", "square", "portrait", "landscape" };
        foreach (var (bed, mood) in root["musicMoods"] as JsonObject ?? new JsonObject())
            if (Str(mood) is { Length: > 0 } words) cat.MusicMoods[bed] = words;
        foreach (var l in root["looks"] as JsonArray ?? new JsonArray())
        {
            var id = Str(l) ?? Str((l as JsonObject)?["id"]);
            if (!string.IsNullOrEmpty(id)) cat.Looks.Add((id, Str((l as JsonObject)?["summary"]) ?? ""));
        }
        foreach (var bn in root["blocks"] as JsonArray ?? new JsonArray())
        {
            if (bn is not JsonObject b || Str(b["id"]) is not { Length: > 0 } id) continue;
            var block = new Block
            {
                Id = id, Category = Str(b["category"]) ?? "", Summary = Str(b["summary"]) ?? "",
                UseWhen = Str(b["useWhen"]) ?? "", AvoidWhen = Str(b["avoidWhen"]) ?? "",
                Natural = Num(b["natural"]), Overlay = Bool(b["overlay"]) == true,
            };
            foreach (var (name, fv) in b["files"] as JsonObject ?? new JsonObject())
                if (fv is JsonObject f)
                    block.Files.Add((name, new Slot(Str(f["kind"]) ?? "", Bool(f["required"]) == true, Str(f["summary"]) ?? "")));
            foreach (var (name, pv) in b["params"] as JsonObject ?? new JsonObject())
                if (pv is JsonObject p)
                    block.Params.Add((name, new Param(Str(p["type"]) ?? "string", p["default"]?.DeepClone(),
                        Num(p["min"]), Num(p["max"]), Ids(p["options"]), Str(p["of"]) ?? "",
                        Str(p["summary"]) ?? "", Num(p["itemMax"]), Bool(p["line"]) == true, Ids(p["notIn"]))));
            cat.Blocks.Add(block);
            cat.ById[id] = block;
        }
        return cat;
    }
}
