using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VanityStudio.Infra;
using VanityStudio.Llm;
using VanityStudio.Tools;
using static VanityStudio.Video.VideoText;

namespace VanityStudio.Video;

/// <summary>
/// A Studio project (a .vstudio.json doc, the same JSON the Studio's video API takes) as files the agent can edit:
/// <c>edits/&lt;name&gt;/doc.json</c> with its pictures, clips and sounds beside it in <c>edits/&lt;name&gt;/media/</c>.
/// Opening a project writes the data URLs it carries out to files, so the doc stays small and readable; rendering
/// ships every local file the doc names to the renderer.
/// </summary>
public static class StudioDoc
{
    private static readonly string[] SceneKeys = ["image", "video"];
    private static readonly string[] LayerKeys = ["image", "video", "src", "model", "rive"];

    /// <summary>Every place a doc names a file (the fields the Studio's tools/render.mjs mounts), with a setter.</summary>
    public static IEnumerable<(string value, Action<string> set)> MediaRefs(JsonObject doc)
    {
        static IEnumerable<(string, Action<string>)> Fields(JsonObject? o, IEnumerable<string> keys)
        {
            if (o is null) yield break;
            foreach (var k in keys)
                if (Str(o[k]) is { Length: > 0 } v) yield return (v, x => o[k] = x);
            if (Str(o["lottie"]) is { Length: > 0 } lot && !lot.TrimStart().StartsWith('{')) yield return (lot, x => o["lottie"] = x);
        }
        var scenes = (doc["scenes"] as JsonArray ?? doc["clips"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        foreach (var s in scenes)
        {
            foreach (var r in Fields(s, SceneKeys)) yield return r;
            foreach (var l in (s["layers"] as JsonArray ?? s["overlays"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                foreach (var r in Fields(l, LayerKeys)) yield return r;
        }
        foreach (var l in (doc["layers"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            foreach (var r in Fields(l, LayerKeys)) yield return r;
        foreach (var a in (doc["audio"] as JsonArray ?? doc["audios"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (Str(a["src"]) is { Length: > 0 } v) { var o = a; yield return (v, x => o["src"] = x); }
    }

    private static bool IsRemote(string v) => Regex.IsMatch(v, "^(https?:|data:|blob:|media:|job:)", RegexOptions.IgnoreCase);

    /// <summary>A file a doc names → the file on this machine, or null: a path relative to the doc, to the project or a
    /// full path (~ and Desktop/... included); media:logo and media:&lt;name&gt; are the project's (the Studio's own
    /// media library is not there when it renders headless). URLs and data URLs are no files here.</summary>
    public static string? LocalFile(string value, string docDir, StudioProject project)
    {
        if (value.StartsWith("media:", StringComparison.OrdinalIgnoreCase)) return project.Resolve(value).file?.Path;
        if (IsRemote(value)) return null;
        if (Path.IsPathRooted(value) || value.StartsWith('~')) { var full = StudioProject.ExpandPath(value, project.Root); return File.Exists(full) ? full : null; }
        foreach (var cand in new[] { Path.Combine(docDir, value), Path.Combine(project.Root, value), StudioProject.ExpandPath(value, project.Root) })
            if (File.Exists(cand)) return Path.GetFullPath(cand);
        return null;
    }

    /// <summary>The doc as the renderer gets it: every local file it names (see <see cref="LocalFile"/>) becomes
    /// <c>job:&lt;name&gt;</c>, and <paramref name="files"/> says which file each name is. A value that is no file is left
    /// as written (a URL, a data URL, a library id).</summary>
    public static JsonObject ForRenderer(JsonObject doc, string docDir, StudioProject project, Dictionary<string, string> files)
    {
        var copy = (JsonObject)doc.DeepClone();
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var (value, set) in MediaRefs(copy).ToList())
        {
            var full = LocalFile(value, docDir, project);
            if (full is null) continue;
            if (!byPath.TryGetValue(full, out var name))
            {
                name = $"{n++}-{StudioOps.SafeName(Path.GetFileName(full))}";
                byPath[full] = name;
                files[name] = full;
            }
            set("job:" + name);
        }
        return copy;
    }

    /// <summary>The files a doc names that are not there (local paths only), for a check before a render.</summary>
    public static List<string> Missing(JsonObject doc, string docDir, StudioProject project) =>
        MediaRefs(doc).Select(r => r.value)
            .Where(v => (v.StartsWith("media:", StringComparison.OrdinalIgnoreCase) || !IsRemote(v)) && !Regex.IsMatch(v, @"^[a-z0-9_-]+$", RegexOptions.IgnoreCase))
            .Where(v => LocalFile(v, docDir, project) is null)
            .Distinct().ToList();

    /// <summary>Writes the data URLs a doc carries (an exported project has every file inside it) out to
    /// <paramref name="mediaDir"/> and points the doc at them (media/&lt;n&gt;.&lt;ext&gt;, relative to the doc).</summary>
    public static int Externalize(JsonObject doc, string mediaDir)
    {
        int n = 0;
        var seen = new Dictionary<string, string>();
        foreach (var (value, set) in MediaRefs(doc).ToList())
        {
            if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) continue;
            var key = value.Length + ":" + value.GetHashCode();
            if (!seen.TryGetValue(key, out var rel))
            {
                var mime = value[5..value.IndexOf(';')];
                Directory.CreateDirectory(mediaDir);
                var name = $"{++n:000}{ExtFor("", mime)}";
                File.WriteAllBytes(Path.Combine(mediaDir, name), Convert.FromBase64String(value[(comma + 1)..]));
                rel = "media/" + name;
                seen[key] = rel;
            }
            set(rel);
        }
        return n;
    }

    /// <summary>A short outline of a doc for the model: its size, length and what each scene and layer shows.</summary>
    public static string Outline(JsonObject doc)
    {
        var sb = new StringBuilder();
        var keys = string.Join(", ", doc.Select(kv => kv.Key));
        sb.Append("top-level fields: " + keys + "\n");
        foreach (var k in new[] { "width", "height", "fps", "format", "duration", "music", "look" })
            if (doc[k] is JsonValue v) sb.Append($"{k}: {v.ToJsonString()}\n");
        var scenes = (doc["scenes"] as JsonArray ?? doc["clips"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        sb.Append($"{(doc["scenes"] is not null ? "scenes" : "clips")}: {scenes.Count}\n");
        for (int i = 0; i < scenes.Count && i < 40; i++)
        {
            var s = scenes[i];
            var what = Str(s["video"]) is { } vid ? "video " + vid + (Num(s["from"]) is { } fr ? $" from {N(fr)} s" : "")
                     : Str(s["image"]) is { } im ? "image " + im : s["card"] is not null ? "card " + (Str(s["color"]) ?? "") : "";
            var layers = (s["layers"] as JsonArray ?? s["overlays"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            var texts = layers.Select(l => Str(l["text"])).Where(t => t is { Length: > 0 }).Take(3).Select(t => "\"" + Short(t!) + "\"");
            var dur = Num(s["duration"]) ?? Num(s["dur"]);
            sb.Append($"  [{i}] {(dur is { } d ? N(d) + " s" : "")} {Short(what)}" + (Str(s["transition"]) is { } tr ? $" · in: {tr}" : "") +
                      (layers.Count > 0 ? $" · {layers.Count} layer(s): " + string.Join(", ", layers.Select(l => Str(l["kind"]) ?? Str(l["type"]) ?? "?").Distinct()) : "") +
                      (texts.Any() ? " · text " + string.Join(", ", texts) : "") + "\n");
        }
        if (doc["layers"] is JsonArray gl) sb.Append($"timeline layers: {gl.Count}" + (gl.Count > 0 ? " (" + string.Join(", ", gl.OfType<JsonObject>().Select(l => Str(l["kind"]) ?? "?").Distinct()) + ")" : "") + "\n");
        if ((doc["audio"] ?? doc["audios"]) is JsonArray au)
            sb.Append($"audio: {au.Count} (" + string.Join(", ", au.OfType<JsonObject>().Select(a =>
                Str(a["music"]) is { } m ? "music " + m : Str(a["sfx"]) is { } fx ? "sfx " + fx : Bool(a["voice"]) == true ? "voice " + Short(Str(a["src"]) ?? "") : Short(Str(a["src"]) ?? "?"))) + ")\n");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// The <c>edit_video</c> tool: a full editor over the Studio's own video API, for anything a script's ready-made blocks
/// cannot say. Open a finished video's project (or any .vstudio.json) or write a doc from scratch, change it (patches or
/// the whole doc), look at it (validate, one frame, a contact sheet), then render it as a background job. The doc is the
/// Studio's: its reference is <c>docs</c>.
/// </summary>
public sealed class EditVideoTool : IVisualTool
{
    private readonly StudioProject _project;
    private readonly VideoJobs _jobs;
    private readonly Func<AiOptions> _ai;
    private static string? _docsCache;

    public EditVideoTool(StudioProject project, VideoJobs jobs, Func<AiOptions> ai) { _project = project; _jobs = jobs; _ai = ai; }

    public string EditsDir => Path.Combine(_project.Root, "edits");

    public ToolDefinition Definition { get; } = new()
    {
        Name = "edit_video",
        Description =
            "Edit a video at the level of the Studio's own project doc (clips, layers, text, keyframes, transitions, audio, 3D): for changes a " +
            "make_video script cannot say, a video the operator made by hand, or cutting the operator's own footage. Workflow: docs (the doc " +
            "reference, read it once) → open a finished job or a .vstudio.json, or save a new doc → patch or save (speak makes a voice file, " +
            "make an AI still or clip for the doc) → validate / frame / sheet " +
            "to look → render. Docs live in edits/<name>/doc.json (read it with read_file); their files sit in edits/<name>/media/, and a doc " +
            "may also name any file by its full path.",
        Parameters = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "docs", "list", "open", "show", "save", "patch", "speak", "make", "validate", "frame", "sheet", "render" } },
                ["text"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "speak: the words to say; the voice file lands in the doc's media/ (a doc speaks only from a voice file)." },
                ["kind"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "still", "clip" }, ["description"] = "make: an AI picture (still) or an AI clip made from a picture (clip)." },
                ["prompt"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "make: what to draw (a still: a real photograph of the moment, no words in it) or how the picture moves (a clip)." },
                ["from"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "make: the picture to start from (required for a clip)." },
                ["name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "the doc's name in edits/ (open: the name to give it; the others: which doc)." },
                ["source"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "open: a finished job's number, or the path of a .vstudio.json / doc.json file." },
                ["doc"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "save: the whole doc as JSON text (a new doc, or one replacing the old)." },
                ["patches"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "object" },
                    ["description"] = "patch: [{\"path\": \"scenes[2].layers[0].text\", \"value\": ...}] (null removes) or {\"op\": \"replace\", \"from\": ..., \"to\": ..., \"path\"?}." },
                ["t"] = new Dictionary<string, object> { ["type"] = "number", ["description"] = "frame: the moment in seconds." },
                ["times"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "number" }, ["description"] = "sheet: the moments to show (default: 8 spread over the video)." },
                ["title"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "render: the video's title (default: the doc's name)." },
            },
            ["required"] = new[] { "action" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default) =>
        (await ExecuteVisualAsync("", argsJson, ct).ConfigureAwait(false)).Output;

    public async Task<ToolResultRecord> ExecuteVisualAsync(string id, string argsJson, CancellationToken ct = default)
    {
        JsonElement a;
        try { a = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException ex) { return Fail(id, "arguments are not valid JSON: " + ex.Message); }
        string S(string k) => a.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "" : "";
        var action = S("action").Trim().ToLowerInvariant();
        var name = Slug(S("name"), "");
        try
        {
            switch (action)
            {
                case "docs": return Ok(id, await DocsAsync(ct).ConfigureAwait(false));
                case "list": return Ok(id, List());
                case "open": return Open(id, S("source").Trim(), name);
                case "show":
                {
                    var (doc, path, err) = Load(name);
                    return doc is null ? Fail(id, err!) : Ok(id, $"{Rel(path!)}\n" + StudioDoc.Outline(doc));
                }
                case "save": return Save(id, name, S("doc"));
                case "patch": return Patch(id, name, a);
                case "validate":
                case "frame":
                case "sheet": return await ProbeAsync(id, action, name, a, ct).ConfigureAwait(false);
                case "speak": return await SpeakAsync(id, name, S("text"), ct).ConfigureAwait(false);
                case "make": return await MakeAsync(id, name, S("kind").Trim().ToLowerInvariant(), S("prompt"), S("from").Trim(), ct).ConfigureAwait(false);
                case "render": return Render(id, name, S("title").Trim());
                default: return Fail(id, "edit_video needs action = docs | list | open | show | save | patch | validate | frame | sheet | render.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail(id, "edit_video: " + ex.Message); }
    }

    private static ToolResultRecord Ok(string id, string text, string? image = null) => new() { ToolCallId = id, ToolName = "edit_video", Output = text, ScreenshotDataUrl = image };
    private static ToolResultRecord Fail(string id, string text) => new() { ToolCallId = id, ToolName = "edit_video", Output = "Error: " + text, IsError = true };
    private string Rel(string p) => Path.GetRelativePath(_project.Root, p).Replace('\\', '/');
    private string DocPath(string name) => Path.Combine(EditsDir, name, "doc.json");

    /// <summary>The Studio's video API reference (its own docs()), cached for the session.</summary>
    public static async Task<string> DocsAsync(CancellationToken ct)
    {
        if (_docsCache is not null) return _docsCache;
        var res = await Task.Run(() => StudioOps.Dispatch(new Dictionary<string, object> { { "action", "probe" }, { "op", "docs" }, { "studio_url", MakeVideoTool.StudioUrl() } }, l => Log.Info(l)), ct).ConfigureAwait(false);
        var r = ParseObject(Convert.ToString(res.GetValueOrDefault("result")));
        var docs = Str(r["docs"]) ?? (r["docs"] is { } node ? node.ToJsonString() : null);
        if (string.IsNullOrWhiteSpace(docs)) throw new InvalidOperationException("the Studio did not give its video API reference: " + Convert.ToString(res.GetValueOrDefault("page_log")));
        return _docsCache = docs;
    }

    private string List()
    {
        var sb = new StringBuilder();
        if (Directory.Exists(EditsDir))
            foreach (var d in Directory.GetDirectories(EditsDir).OrderByDescending(Directory.GetLastWriteTimeUtc))
                if (File.Exists(Path.Combine(d, "doc.json"))) sb.Append($"- {Path.GetFileName(d)}: {Rel(Path.Combine(d, "doc.json"))} (changed {Directory.GetLastWriteTime(d):yyyy-MM-dd HH:mm})\n");
        var done = _jobs.Store.All().Where(j => j.Status == VideoJob.Done && j.Project is not null).Take(10).ToList();
        if (done.Count > 0) sb.Append("Finished videos with a project to open (open source=<number>):\n" + string.Join("\n", done.Select(j => $"- #{j.Id} \"{j.Title}\": {j.Project}")) + "\n");
        return sb.Length == 0 ? "No docs in edits/ yet and no finished video with a project. Open a .vstudio.json, or save a new doc (read docs first)." : sb.ToString().TrimEnd();
    }

    private (JsonObject? doc, string? path, string? error) Load(string name)
    {
        if (name.Length == 0) return (null, null, "name: which doc (edit_video action=list shows them).");
        var path = DocPath(name);
        if (!File.Exists(path)) return (null, null, $"there is no doc \"{name}\" in edits/ (edit_video action=list shows them).");
        try { return (MakeVideoTool.ParseScript(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("not an object"), path, null); }
        catch (JsonException ex) { return (null, path, $"{Rel(path)} is not valid JSON: {ex.Message}"); }
    }

    private void Write(string path, JsonObject doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private ToolResultRecord Open(string id, string source, string name)
    {
        if (source.Length == 0) return Fail(id, "open needs source: a finished job's number or a .vstudio.json path.");
        string file;
        var num = source.TrimStart('#');
        if (long.TryParse(num, out var jobId))
        {
            var job = _jobs.Store.Get(jobId);
            if (job is null) return Fail(id, $"there is no video job #{jobId} here.");
            if (job.Project is null) return Fail(id, $"job #{jobId} has no project file ({job.Status}).");
            file = Path.Combine(_project.Root, job.Project);
            if (name.Length == 0) name = Slug(job.Title, "video-" + jobId);
        }
        else
        {
            var (f, err) = _project.Resolve(source);
            if (f is null) return Fail(id, err!);
            file = f.Path;
            if (name.Length == 0) name = Slug(Regex.Replace(Path.GetFileName(file), @"(\.vstudio)?\.json$", "", RegexOptions.IgnoreCase), "video");
        }
        JsonObject doc;
        try { doc = MakeVideoTool.ParseScript(File.ReadAllText(file)) as JsonObject ?? throw new JsonException("not a JSON object"); }
        catch (Exception ex) { return Fail(id, $"{Path.GetFileName(file)} is not a Studio project: {ex.Message}"); }
        // a wrapped export ({ doc: {...} } or { project: {...} }) opens as its doc
        if (doc["doc"] is JsonObject inner) doc = (JsonObject)inner.DeepClone();
        var target = DocPath(name);
        var dir = Path.GetDirectoryName(target)!;
        var written = StudioDoc.Externalize(doc, Path.Combine(dir, "media"));
        // files the doc names relative to where it was keep working from its new place
        var srcDir = Path.GetDirectoryName(file)!;
        foreach (var (value, set) in StudioDoc.MediaRefs(doc).ToList())
            if (!Regex.IsMatch(value, "^(https?:|data:|blob:|media:|job:)", RegexOptions.IgnoreCase) && !Path.IsPathRooted(value)
                && !File.Exists(Path.Combine(dir, value)) && File.Exists(Path.Combine(srcDir, value)))
                set(Path.GetFullPath(Path.Combine(srcDir, value)));
        Write(target, doc);
        return Ok(id, $"Opened {Rel(file)} as {Rel(target)} ({written} embedded file(s) written to {Rel(Path.Combine(dir, "media"))}/).\n" + StudioDoc.Outline(doc) +
                      "\nRead the whole doc with read_file, change it with patch (or save), look with frame / sheet, then render.");
    }

    private ToolResultRecord Save(string id, string name, string text)
    {
        if (name.Length == 0) return Fail(id, "save needs name: the doc's name in edits/.");
        if (text.Trim().Length == 0) return Fail(id, "save needs doc: the whole doc as JSON text.");
        JsonObject doc;
        try { doc = MakeVideoTool.ParseScript(text) as JsonObject ?? throw new JsonException("the doc must be one JSON object"); }
        catch (JsonException ex) { return Fail(id, MakeVideoTool.ScriptError(text, ex)); }
        var path = DocPath(name);
        Write(path, doc);
        var missing = StudioDoc.Missing(doc, Path.GetDirectoryName(path)!, _project);
        return Ok(id, $"Saved {Rel(path)}.\n" + StudioDoc.Outline(doc) + (missing.Count > 0 ? "\nFiles it names that are not there: " + string.Join(", ", missing) : "") +
                      "\nvalidate checks it against the Studio; frame / sheet show it.");
    }

    private ToolResultRecord Patch(string id, string name, JsonElement a)
    {
        var (doc, path, err) = Load(name);
        if (doc is null) return Fail(id, err!);
        if (!a.TryGetProperty("patches", out var pe) || pe.ValueKind != JsonValueKind.Array || pe.GetArrayLength() == 0)
            return Fail(id, "patch needs patches: [{\"path\": ..., \"value\": ...}].");
        var applied = new List<string>();
        foreach (var p in pe.EnumerateArray())
        {
            if (p.TryGetProperty("op", out var op) && op.GetString() == "replace")
            {
                var from = p.TryGetProperty("from", out var fj) ? fj.GetString() ?? "" : "";
                var to = p.TryGetProperty("to", out var tj) ? tj.GetString() ?? "" : "";
                if (from.Length == 0) return Fail(id, "op:replace needs from.");
                var sub = p.TryGetProperty("path", out var sp) ? sp.GetString() ?? "" : "";
                var hits = MakeVideoTool.ReplaceStrings(doc, sub, from, to, p.TryGetProperty("caseSensitive", out var cs) && cs.ValueKind == JsonValueKind.True);
                applied.Add($"replace \"{from}\" -> \"{to}\" ({hits})");
                continue;
            }
            var path2 = p.TryGetProperty("path", out var pp) ? pp.GetString()?.Trim() ?? "" : "";
            if (path2.Length == 0 || !p.TryGetProperty("value", out var pv)) return Fail(id, "each patch needs path and value (null removes the field).");
            try { MakeVideoTool.ApplyPatch(doc, path2, pv.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(pv.GetRawText())); }
            catch (Exception ex) { return Fail(id, $"patch {path2}: {ex.Message}"); }
            applied.Add(path2);
        }
        Write(path!, doc);
        return Ok(id, $"Patched {Rel(path!)}: {string.Join(", ", applied)}. validate / frame / sheet to look, render when it is right.");
    }

    private async Task<ToolResultRecord> ProbeAsync(string id, string op, string name, JsonElement a, CancellationToken ct)
    {
        var (doc, path, err) = Load(name);
        if (doc is null) return Fail(id, err!);
        var dir = Path.GetDirectoryName(path!)!;
        var files = new Dictionary<string, string>();
        var forPage = StudioDoc.ForRenderer(doc, dir, _project, files);
        var cmd = new Dictionary<string, object> { { "action", "probe" }, { "op", op }, { "doc_json", forPage.ToJsonString() }, { "media", files }, { "studio_url", MakeVideoTool.StudioUrl() } };
        if (op == "frame") { cmd["t"] = a.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0; cmd["scale"] = 0.5; }
        if (op == "sheet")
        {
            cmd["scale"] = 0.25;
            if (a.TryGetProperty("times", out var ts) && ts.ValueKind == JsonValueKind.Array)
                cmd["times"] = ts.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList();
        }
        var res = await Task.Run(() => StudioOps.Dispatch(cmd, l => Log.Info(l)), ct).ConfigureAwait(false);
        var r = ParseObject(Convert.ToString(res.GetValueOrDefault("result")));
        var warnings = (r["warnings"] as JsonArray ?? new JsonArray()).Select(Text).Where(x => x is { Length: > 0 }).Take(10).ToList();
        if (Bool(r["ok"]) != true)
        {
            var errors = (r["errors"] as JsonArray ?? new JsonArray()).Select(Text).Where(x => x is { Length: > 0 }).ToList();
            if (errors.Count == 0) errors.Add("the Studio page gave no result: " + Convert.ToString(res.GetValueOrDefault("page_log")));
            return Fail(id, $"{Rel(path!)} is not right yet ({errors.Count} problem(s)):\n- " + string.Join("\n- ", errors) + (warnings.Count > 0 ? "\nwarnings:\n- " + string.Join("\n- ", warnings) : ""));
        }
        var warn = warnings.Count > 0 ? "\nwarnings:\n- " + string.Join("\n- ", warnings) : "";
        if (op == "validate") return Ok(id, $"{Rel(path!)} is valid." + warn);
        var dataUrl = Str(r["dataUrl"]);
        if (dataUrl is null) return Fail(id, "the Studio drew no picture.");
        var looks = Path.Combine(dir, "looks");
        Directory.CreateDirectory(looks);
        var file = Path.Combine(looks, op == "frame" ? $"frame-{N(Num(r["t"]) ?? (a.TryGetProperty("t", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetDouble() : 0))}s.png" : "sheet.jpg");
        try { File.WriteAllBytes(file, Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..])); } catch { }
        var duration = Num(r["duration"]) ?? 0;
        var what = op == "frame" ? $"the frame at {S(a, "t")} s" : "the contact sheet" + (r["times"] is JsonArray tms ? " at " + string.Join(", ", tms.Select(x => N(Num(x) ?? 0))) + " s" : "");
        return Ok(id, $"{Rel(path!)}: {what} of {N(duration)} s (saved as {Rel(file)})." + warn, dataUrl);
    }

    private static string S(JsonElement a, string k) => a.TryGetProperty(k, out var v) ? v.ToString() : "";

    // the next free name in a doc's media/: voice-1.wav, still-2.png ...
    private static string NextFile(string mediaDir, string stem, string ext)
    {
        Directory.CreateDirectory(mediaDir);
        for (int i = 1; ; i++) { var f = Path.Combine(mediaDir, $"{stem}-{i}{ext}"); if (!File.Exists(f)) return f; }
    }

    private async Task<ToolResultRecord> SpeakAsync(string id, string name, string text, CancellationToken ct)
    {
        var (doc, path, err) = Load(name);
        if (doc is null) return Fail(id, err!);
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) return Fail(id, "speak needs text: the words to say.");
        var r = await VoiceMaker.SpeakAsync(_ai(), text, ct, l => Log.Warn("[voice] " + l)).ConfigureAwait(false);
        var file = NextFile(Path.Combine(Path.GetDirectoryName(path!)!, "media"), "voice", ExtFor("", r.Mime));
        await File.WriteAllBytesAsync(file, r.Bytes, ct).ConfigureAwait(false);
        var rel = "media/" + Path.GetFileName(file);
        var seconds = MediaSeconds(file);
        return Ok(id, $"Spoken by {r.ProfileName} ({r.Model}): {rel}, {N(seconds)} s. Add it to the doc's audio: {{\"src\": \"{rel}\", \"start\": <seconds into the video>, \"voice\": true}} " +
                      "(music with \"duck\": true dips under it), and give the scenes it covers at least that long.");
    }

    private async Task<ToolResultRecord> MakeAsync(string id, string name, string kind, string prompt, string from, CancellationToken ct)
    {
        var (doc, path, err) = Load(name);
        if (doc is null) return Fail(id, err!);
        if (kind is not ("still" or "clip")) return Fail(id, "make needs kind: still or clip.");
        if (prompt.Trim().Length == 0) return Fail(id, "make needs prompt.");
        var dir = Path.GetDirectoryName(path!)!;
        (byte[] bytes, string mime)? picture = null;
        if (from.Length > 0)
        {
            var full = StudioDoc.LocalFile(from, dir, _project);
            if (full is null) return Fail(id, $"from: '{from}' is not a file here.");
            picture = (await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false), MimeOf(full));
        }
        var media = Path.Combine(dir, "media");
        if (kind == "clip")
        {
            if (picture is null) return Fail(id, "a clip is made from a picture: give from.");
            var bytes = await ClipMaker.RenderAsync(_ai(), picture.Value, prompt, ct, s => Log.Info("[clip] " + s)).ConfigureAwait(false);
            var clip = NextFile(media, "clip", ".mp4");
            await File.WriteAllBytesAsync(clip, bytes, ct).ConfigureAwait(false);
            return Ok(id, $"Clip made: media/{Path.GetFileName(clip)} ({N(MediaSeconds(clip))} s). Use it as a scene {{\"video\": \"media/{Path.GetFileName(clip)}\", \"dur\": ...}}.");
        }
        var (fw, fh) = FormatSize(Str(doc["format"]));
        var all = ImageMaker.Candidates(_ai());
        var candidates = all.Where(c => !VideoJobs.IsSpent(c.Name, out _)).ToList();
        if (all.Count > 0 && candidates.Count == 0)
            return Fail(id, "the image quota of " + string.Join(", ", all.Select(c => { VideoJobs.IsSpent(c.Name, out var u); return $"{c.Name} (until {u:HH:mm})"; })) +
                            " is used up: use the site's pictures (web download) or the operator's files.");
        if (candidates.Count == 0) return Fail(id, "no AI profile can make pictures (OpenAI, the Antigravity login or Alibaba).");
        Exception? last = null;
        foreach (var p in candidates)
        {
            try
            {
                var bytes = await ImageMaker.DrawAsync(p, prompt + " No text, no letters, no logos, no watermarks anywhere in the picture.", fw, fh, ct, picture?.bytes).ConfigureAwait(false);
                bytes = VideoJobs.CropTo(bytes, (double)fw / fh);
                var still = NextFile(media, "still", ExtFor("", SniffImageMime(bytes)));
                await File.WriteAllBytesAsync(still, bytes, ct).ConfigureAwait(false);
                var (w, h) = ImageSize(still);
                return Ok(id, $"Still drawn by {p.Name}: media/{Path.GetFileName(still)} ({w}x{h}). Use it as a scene {{\"image\": \"media/{Path.GetFileName(still)}\", ...}} or an image layer. Look at it with read_file.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (VideoJobs.ResetIn(ex.Message) is { TotalSeconds: > 120 } reset) VideoJobs.MarkSpent(p.Name, reset);
            }
        }
        return Fail(id, "no profile could draw it: " + ErrorText(last?.Message ?? ""));
    }

    private ToolResultRecord Render(string id, string name, string title)
    {
        var (doc, path, err) = Load(name);
        if (doc is null) return Fail(id, err!);
        var missing = StudioDoc.Missing(doc, Path.GetDirectoryName(path!)!, _project);
        if (missing.Count > 0) return Fail(id, "the doc names files that are not there: " + string.Join(", ", missing));
        var jobId = _jobs.QueueDoc(Rel(path!), title.Length > 0 ? title : name);
        return Ok(id, $"Video job #{jobId} queued: {Rel(path!)} renders in the background on this machine; the finished video is saved under videos/ and reported here when ready.");
    }
}
