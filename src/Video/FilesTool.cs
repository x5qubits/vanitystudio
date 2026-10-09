using System.Text;
using System.Text.Json;
using VanityStudio.Infra;
using VanityStudio.Llm;
using VanityStudio.Tools;
using static VanityStudio.Video.VideoText;

namespace VanityStudio.Video;

/// <summary>The operator's own pictures, clips and sounds wherever they are: the project, the Desktop, Downloads,
/// Pictures, Videos, Documents or any folder. list and find show what is there (kind, size, pixels, length); info
/// tells one file; import copies a file into the project's media/ so the video keeps working when the original moves.
/// A script or a doc may also name a file by its full path directly.</summary>
public sealed class FilesTool : ITool
{
    private readonly StudioProject _project;
    public FilesTool(StudioProject project) { _project = project; }

    public ToolDefinition Definition { get; } = new()
    {
        Name = "files",
        Description =
            "Find the operator's pictures, clips and sounds on this machine. list folder=<desktop|downloads|pictures|videos|documents|home|project|media|any path> " +
            "(newest first); find query=<words of the name> (searches the project, Desktop, Downloads, Pictures, Videos and Documents); " +
            "info path=<file> (kind, size, pixels, length); import path=<file> (copies it into the project's media/ and returns the name to use). " +
            "\"the picture on my desktop\" = list folder=desktop. To SEE a picture, read it with read_file.",
        Parameters = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "list", "find", "info", "import" } },
                ["folder"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "list: desktop, downloads, pictures, videos, documents, home, project, media, or a path." },
                ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "find: words of the file's name." },
                ["kind"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "picture", "clip", "sound", "any" }, ["description"] = "list, find: only this kind (default any)." },
                ["path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "info, import: the file (a full path, ~/..., Desktop/..., or a path in the project)." },
                ["as"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "import: the name to give it in media/ (default: its own name)." },
            },
            ["required"] = new[] { "action" },
        },
    };

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        JsonElement a;
        try { a = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException ex) { return Task.FromResult("Error: arguments are not valid JSON: " + ex.Message); }
        string S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var kind = S("kind").Trim().ToLowerInvariant();
        if (kind is "" or "any") kind = "";
        try
        {
            return Task.FromResult(S("action").Trim().ToLowerInvariant() switch
            {
                "list" => List(S("folder").Trim(), kind),
                "find" => Find(S("query").Trim(), kind, ct),
                "info" => Info(S("path").Trim()),
                "import" => Import(S("path").Trim(), S("as").Trim()),
                _ => "Error: files needs action = list | find | info | import.",
            });
        }
        catch (Exception ex) { return Task.FromResult("Error: " + ex.Message); }
    }

    /// <summary>The operator's well-known folders.</summary>
    public static Dictionary<string, string> KnownFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Or(Environment.SpecialFolder f, string fallback) { var p = Environment.GetFolderPath(f); return string.IsNullOrEmpty(p) ? Path.Combine(home, fallback) : p; }
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["desktop"] = Or(Environment.SpecialFolder.DesktopDirectory, "Desktop"),
            ["downloads"] = Path.Combine(home, "Downloads"),
            ["pictures"] = Or(Environment.SpecialFolder.MyPictures, "Pictures"),
            ["videos"] = Or(Environment.SpecialFolder.MyVideos, "Videos"),
            ["music"] = Or(Environment.SpecialFolder.MyMusic, "Music"),
            ["documents"] = Or(Environment.SpecialFolder.MyDocuments, "Documents"),
            ["home"] = home,
        };
    }

    private string Folder(string name)
    {
        if (name.Length == 0 || name.Equals("project", StringComparison.OrdinalIgnoreCase)) return _project.Root;
        if (name.Equals("media", StringComparison.OrdinalIgnoreCase)) return _project.MediaDir;
        if (KnownFolders().TryGetValue(name.TrimEnd('/', '\\'), out var known)) return known;
        return StudioProject.ExpandPath(name, _project.Root);
    }

    private static string KindOf(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return StudioProject.ImageExt.Contains(e) ? "picture" : StudioProject.VideoExt.Contains(e) ? "clip" : StudioProject.AudioExt.Contains(e) ? "sound" : "";
    }

    private static string Describe(string f, bool length = false)
    {
        var fi = new FileInfo(f);
        var kind = KindOf(f);
        var extra = "";
        if (kind == "picture") { var (w, h) = ImageSize(f); if (w > 0) extra = $", {w}x{h}"; }
        else if (length && kind is "clip" or "sound") { var s = MediaSeconds(f); if (s > 0) extra = $", {N(s)} s"; }
        return $"{f} ({kind}, {Size(fi.Length)}{extra}, {fi.LastWriteTime:yyyy-MM-dd HH:mm})";
    }

    private static string Size(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.#} MB" : $"{Math.Max(1, b / 1024)} KB";

    private string List(string folder, string kind)
    {
        var dir = Folder(folder);
        if (!Directory.Exists(dir)) return $"Error: there is no folder {dir}.";
        var files = SafeFiles(dir, folder.Equals("project", StringComparison.OrdinalIgnoreCase) || folder.Equals("media", StringComparison.OrdinalIgnoreCase) || folder.Length == 0 ? 4 : 1)
            .Where(f => KindOf(f) is { Length: > 0 } k && (kind.Length == 0 || k == kind))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).Take(80).ToList();
        if (files.Count == 0) return $"No {(kind.Length > 0 ? kind + "s" : "pictures, clips or sounds")} in {dir}.";
        return $"{files.Count} file(s) in {dir}, newest first:\n" + string.Join("\n", files.Select(f => "- " + Describe(f))) +
               "\nName one in a script or doc by its full path, or import it into media/ first.";
    }

    private string Find(string query, string kind, CancellationToken ct)
    {
        if (query.Length == 0) return "Error: find needs query: words of the file's name.";
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var roots = new List<string> { _project.Root };
        var k = KnownFolders();
        foreach (var name in new[] { "desktop", "downloads", "pictures", "videos", "music", "documents" }) roots.Add(k[name]);
        var hits = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var f in SafeFiles(root, 4))
            {
                if (ct.IsCancellationRequested || hits.Count >= 40) break;
                var n = Path.GetFileName(f).ToLowerInvariant();
                if (KindOf(f) is not { Length: > 0 } fk || (kind.Length > 0 && fk != kind)) continue;
                if (words.All(w => n.Contains(w)) && seen.Add(Path.GetFullPath(f))) hits.Add(f);
            }
        if (hits.Count == 0) return $"Nothing named like \"{query}\" in the project, Desktop, Downloads, Pictures, Videos, Music or Documents.";
        return $"{hits.Count} match(es):\n" + string.Join("\n", hits.OrderByDescending(File.GetLastWriteTimeUtc).Select(f => "- " + Describe(f)));
    }

    private string Info(string path)
    {
        if (path.Length == 0) return "Error: info needs path.";
        var full = StudioProject.ExpandPath(path, _project.Root);
        if (!File.Exists(full)) return $"Error: there is no file {full}.";
        return Describe(full, length: true);
    }

    private string Import(string path, string asName)
    {
        if (path.Length == 0) return "Error: import needs path: the file to copy into the project.";
        var full = StudioProject.ExpandPath(path, _project.Root);
        if (!File.Exists(full)) return $"Error: there is no file {full}.";
        if (VanityPathHelper.IsDeniedForAgent(full, _project.Root, out var denied)) return "Error: " + denied;
        Directory.CreateDirectory(_project.MediaDir);
        var name = StudioOps.SafeName(asName.Length > 0 ? asName + (Path.HasExtension(asName) ? "" : Path.GetExtension(full)) : Path.GetFileName(full));
        var target = Path.Combine(_project.MediaDir, name);
        var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
        for (int i = 2; File.Exists(target) && !SameFile(target, full); i++) target = Path.Combine(_project.MediaDir, $"{stem}-{i}{ext}");
        if (!File.Exists(target)) File.Copy(full, target);
        var rel = Path.GetRelativePath(_project.Root, target).Replace('\\', '/');
        return $"Imported {full} as {rel}. Name it in a script or doc as \"{rel}\".";
    }

    private static bool SameFile(string a, string b)
    {
        try { var fa = new FileInfo(a); var fb = new FileInfo(b); return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)); }
        catch { return false; }
    }

    private static IEnumerable<string> SafeFiles(string dir, int depth)
    {
        IEnumerable<string> files = [];
        try { files = Directory.EnumerateFiles(dir).ToList(); } catch { }
        foreach (var f in files) yield return f;
        if (depth <= 1) yield break;
        IEnumerable<string> subs = [];
        try
        {
            subs = Directory.EnumerateDirectories(dir).Where(d =>
            {
                var n = Path.GetFileName(d);
                return !n.StartsWith('.') && n is not ("node_modules" or "bin" or "obj" or "AppData" or "$RECYCLE.BIN");
            }).ToList();
        }
        catch { }
        foreach (var s in subs)
            foreach (var f in SafeFiles(s, depth - 1)) yield return f;
    }
}

/// <summary>The brand the videos speak for: brand.json (name, site, logo, colours, language) and brand.md (what it
/// sells, to whom, the offer, the voice). read is what the videographer reads before writing a script; save keeps
/// what the operator says about the brand.</summary>
public sealed class BrandTool : ITool
{
    private readonly StudioProject _project;
    public BrandTool(StudioProject project) { _project = project; }

    public ToolDefinition Definition { get; } = new()
    {
        Name = "brand",
        Description =
            "The brand of this project. read: its name, site, logo, colours and language (brand.json), what it sells, to whom and in which " +
            "voice (brand.md), and the project's pictures and clips. save: keep what the operator said about the brand (only the fields given " +
            "change; facts replaces brand.md).",
        Parameters = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "read", "save" } },
                ["name"] = new Dictionary<string, object> { ["type"] = "string" },
                ["url"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "the brand's site." },
                ["logo"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "the logo file (a path; it is used as media:logo)." },
                ["colors"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" }, ["description"] = "brand colours as #rrggbb." },
                ["language"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "the language its videos speak (code or name)." },
                ["facts"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "markdown: what it sells, to whom, the offer, the tone, words to use and avoid (replaces brand.md)." },
            },
            ["required"] = new[] { "action" },
        },
    };

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        JsonElement a;
        try { a = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException ex) { return Task.FromResult("Error: arguments are not valid JSON: " + ex.Message); }
        var action = a.TryGetProperty("action", out var ac) ? ac.GetString()?.Trim().ToLowerInvariant() : "read";
        return Task.FromResult(action == "save" ? Save(a) : Read());
    }

    public string Read()
    {
        var sb = new StringBuilder();
        var brand = _project.Brand();
        sb.Append(brand.Count == 0 ? "brand.json: not set (brand save keeps name, url, logo, colors, language).\n" : "brand.json: " + brand.ToJsonString() + "\n");
        var (logo, logoErr) = _project.Logo();
        sb.Append(logo is not null ? $"logo (media:logo): {logo.Canonical}\n" : logoErr + "\n");
        var facts = _project.BrandFacts().Trim();
        sb.Append(facts.Length == 0 ? "brand.md: empty (what it sells, to whom, the offer and the voice are unknown: ask the operator, or read their site with make_video action=site, then brand save facts=...).\n"
                                    : "brand.md:\n" + (facts.Length > 12000 ? facts[..12000] + "\n[cut]" : facts) + "\n");
        var media = _project.Media(40);
        sb.Append(media.Count == 0 ? "The project has no pictures or clips yet (files list folder=desktop finds the operator's own)."
            : "Pictures and clips in the project:\n" + string.Join("\n", media.Select(m => $"- {m.Rel} ({m.Kind}{(m.Dims.Length > 0 ? ", " + m.Dims : "")})")));
        return sb.ToString().TrimEnd();
    }

    private string Save(JsonElement a)
    {
        var brand = _project.Brand();
        var changed = new List<string>();
        foreach (var k in new[] { "name", "url", "logo", "language" })
            if (a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } val)
            {
                if (k == "logo")
                {
                    var (f, err) = _project.Resolve(val);
                    if (f is null) return "Error: logo: " + err;
                    val = f.Canonical;
                }
                brand[k] = val; changed.Add(k);
            }
        if (a.TryGetProperty("colors", out var cv) && cv.ValueKind == JsonValueKind.Array)
        {
            var colors = cv.EnumerateArray().Select(c => c.GetString() ?? "").Where(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")).ToList();
            brand["colors"] = new System.Text.Json.Nodes.JsonArray(colors.Select(c => (System.Text.Json.Nodes.JsonNode?)c).ToArray());
            changed.Add("colors");
        }
        if (changed.Count > 0) _project.SaveBrand(brand);
        if (a.TryGetProperty("facts", out var fv) && fv.ValueKind == JsonValueKind.String && fv.GetString()!.Trim().Length > 0) { _project.SaveBrandFacts(fv.GetString()!); changed.Add("facts"); }
        return changed.Count == 0 ? "Nothing to save: give name, url, logo, colors, language or facts." : $"Saved {string.Join(", ", changed)} in {Path.GetRelativePath(_project.Root, _project.ConfigDir).Replace('\\', '/')}/.";
    }
}
