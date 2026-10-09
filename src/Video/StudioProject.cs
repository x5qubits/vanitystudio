using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VanityStudio.Agent;
using VanityStudio.Infra;

namespace VanityStudio.Video;

/// <summary>A resolved file reference. <see cref="Canonical"/> names it the same way every time (its path relative to
/// the project, or its full path when it lives elsewhere), so a queued job uses exactly the file that was checked.</summary>
public sealed record FileRef(string Canonical, string Path, string Name, string Mime);

/// <summary>
/// The folder Vanity Studio works in: where a script's files are found, where the brand lives and where finished
/// videos land.
/// <list type="bullet">
/// <item><c>media/</c>: the operator's pictures, clips and logo (any file in the project can be named too);
/// <c>media/made/</c> the AI stills and clips the jobs generated.</item>
/// <item><c>videos/&lt;job&gt;-&lt;title&gt;/</c>: each finished video with its contact sheet, banner, editable
/// project and report.</item>
/// <item><c>.vanity-studio/brand.json</c> + <c>brand.md</c>: the brand (name, site, logo, colours, language) and
/// what it sells, to whom and in which voice.</item>
/// <item>job state: <c>.vanity-studio/jobs/</c> when the project folder exists, else under the studio home.</item>
/// </list>
/// </summary>
public sealed class StudioProject
{
    public string Root { get; }
    public StudioProject(string root) { Root = System.IO.Path.GetFullPath(root); }

    public string ConfigDir => PromptLibrary.ProjectDir(Root);
    public string StateDir => PromptLibrary.HasProjectDir(Root) ? ConfigDir : AgentConfig.ProjectDir(Root);
    public string JobsDir => System.IO.Path.Combine(StateDir, "jobs");
    public string MediaDir => System.IO.Path.Combine(Root, "media");
    public string MadeDir => System.IO.Path.Combine(MediaDir, "made");
    public string VideosDir => System.IO.Path.Combine(Root, "videos");
    public string BrandJsonPath => System.IO.Path.Combine(ConfigDir, "brand.json");
    public string BrandMdPath => System.IO.Path.Combine(ConfigDir, "brand.md");

    // ── brand ──────────────────────────────────────────────────────────────────────────────────────────────────
    public JsonObject Brand()
    {
        try { if (File.Exists(BrandJsonPath)) return JsonNode.Parse(File.ReadAllText(BrandJsonPath), null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? new JsonObject(); }
        catch { }
        return new JsonObject();
    }

    public string BrandFacts()
    {
        try { return File.Exists(BrandMdPath) ? File.ReadAllText(BrandMdPath) : ""; } catch { return ""; }
    }

    public void SaveBrand(JsonObject brand)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(BrandJsonPath, brand.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    public void SaveBrandFacts(string text)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(BrandMdPath, text.TrimEnd() + Environment.NewLine);
    }

    // ── files ──────────────────────────────────────────────────────────────────────────────────────────────────
    public static readonly string[] ImageExt = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".avif"];
    public static readonly string[] VideoExt = [".mp4", ".webm", ".mov", ".m4v"];
    public static readonly string[] AudioExt = [".mp3", ".wav", ".m4a", ".aac", ".ogg"];
    private static readonly string[] SkipDirs = [".git", "node_modules", "bin", "obj", ".vs", ".idea"];

    public const string FileForms = "a file's path in the project (media/shop.jpg), its name (shop.jpg), media:logo, {\"make\": \"still\", \"prompt\": ...} or {\"make\": \"clip\", \"from\": <picture>, \"prompt\": ...}";

    /// <summary>A file reference as the videographer writes it → the file, or an error the model can act on. Forms: a
    /// path relative to the project or a full path; a bare name (with or without its extension), searched in media/,
    /// the project root, then the whole project; media:logo (the brand's logo); media:&lt;name&gt; (a file in media/);
    /// [[file:ID:NAME]] (named by its NAME).</summary>
    public (FileRef? file, string? error) Resolve(string raw)
    {
        raw = (raw ?? "").Trim().Trim('"');
        if (raw.Length == 0) return (null, "the file reference is empty.");
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (null, $"'{Short(raw)}' is a web address; save the file into the project (for example media/) and name it by its path.");
        if (raw.Equals("media:logo", StringComparison.OrdinalIgnoreCase)) return Logo();
        var fm = Regex.Match(raw, @"^\[\[file:\d*:([^\]]+)\]\]$|^file:(.+)$");
        if (fm.Success) raw = (fm.Groups[1].Success ? fm.Groups[1].Value : fm.Groups[2].Value).Trim();
        if (raw.StartsWith("media:", StringComparison.OrdinalIgnoreCase))
        {
            var name = raw["media:".Length..].Trim();
            var inMedia = FindByName(name, [MediaDir], recursive: true);
            return inMedia is null ? (null, $"'{raw}' is not in media/. {Hint()}") : (Ref(inMedia), null);
        }
        if (raw.IndexOfAny(['/', '\\']) >= 0 || System.IO.Path.IsPathRooted(raw) || raw.StartsWith('~'))
        {
            var full = ExpandPath(raw, Root);
            if (VanityPathHelper.IsDeniedForAgent(full, Root, out var denied)) return (null, denied);
            return File.Exists(full) ? (Ref(full), null) : (null, $"'{raw}' does not exist. {Hint()}");
        }
        var hit = FindByName(raw, [MediaDir, Root], recursive: false) ?? FindByName(raw, [Root], recursive: true);
        return hit is null ? (null, $"'{raw}' is not a file in this project. {Hint()}") : (Ref(hit), null);
    }

    /// <summary>A path as the operator or the model writes it → a full path: ~ and ~/... are the home folder; a path
    /// starting with Desktop/, Downloads/, Pictures/, Videos/, Music/ or Documents/ that is not in the project is that
    /// folder of the operator's (OneDrive's Desktop included); anything else relative is the project's.</summary>
    public static string ExpandPath(string raw, string root)
    {
        raw = (raw ?? "").Trim().Trim('"');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (raw == "~") return home;
        if (raw.StartsWith("~/") || raw.StartsWith("~\\")) return System.IO.Path.GetFullPath(System.IO.Path.Combine(home, raw[2..]));
        if (System.IO.Path.IsPathRooted(raw)) return System.IO.Path.GetFullPath(raw);
        var inProject = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, raw));
        if (File.Exists(inProject) || Directory.Exists(inProject)) return inProject;
        var first = raw.Split('/', '\\')[0];
        if (FilesTool.KnownFolders().TryGetValue(first, out var known))
        {
            var rest = raw.Length > first.Length ? raw[(first.Length + 1)..] : "";
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(known, rest));
        }
        return inProject;
    }

    /// <summary>The brand's logo: brand.json "logo" when set, else a file named logo.* in .vanity-studio/, media/ or the root.</summary>
    public (FileRef? file, string? error) Logo()
    {
        if (VideoText.Str(Brand()["logo"]) is { Length: > 0 } set && !set.Equals("media:logo", StringComparison.OrdinalIgnoreCase))
        {
            var (f, err) = Resolve(set);
            return f is null ? (null, "media:logo: the brand's logo (" + set + ") cannot be used: " + err) : (f, null);
        }
        foreach (var dir in new[] { ConfigDir, MediaDir, Root })
        {
            if (!Directory.Exists(dir)) continue;
            var f = Directory.EnumerateFiles(dir, "logo.*").FirstOrDefault(p => ImageExt.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant()));
            if (f is not null) return (Ref(f), null);
        }
        return (null, "media:logo: this project has no logo. Put logo.png in media/ (or set it with the brand tool), or leave the logo out.");
    }

    private FileRef Ref(string full)
    {
        full = System.IO.Path.GetFullPath(full);
        var canonical = VanityPathHelper.IsInside(full, Root) ? System.IO.Path.GetRelativePath(Root, full).Replace('\\', '/') : full;
        return new FileRef(canonical, full, System.IO.Path.GetFileName(full), VideoText.MimeOf(full));
    }

    private string? FindByName(string name, IEnumerable<string> dirs, bool recursive)
    {
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = recursive ? Walk(dir) : Directory.EnumerateFiles(dir); } catch { continue; }
            var list = files.ToList();
            var exact = list.Where(f => string.Equals(System.IO.Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Length).FirstOrDefault();
            if (exact is not null) return exact;
            var stem = list.Where(f => string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase) && IsMediaExt(f)).OrderBy(f => f.Length).FirstOrDefault();
            if (stem is not null) return stem;
        }
        return null;
    }

    private static IEnumerable<string> Walk(string dir, int depth = 0)
    {
        if (depth > 6) yield break;
        IEnumerable<string> files = [];
        try { files = Directory.EnumerateFiles(dir).ToList(); } catch { }
        foreach (var f in files) yield return f;
        IEnumerable<string> subs = [];
        try { subs = Directory.EnumerateDirectories(dir).Where(d => !SkipDirs.Contains(System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)).ToList(); } catch { }
        foreach (var s in subs)
            foreach (var f in Walk(s, depth + 1)) yield return f;
    }

    private static bool IsMediaExt(string f)
    {
        var e = System.IO.Path.GetExtension(f).ToLowerInvariant();
        return ImageExt.Contains(e) || VideoExt.Contains(e) || AudioExt.Contains(e);
    }

    /// <summary>The project's pictures, clips and sounds: media/ (made ones included) first, then the rest of the project
    /// (not videos/, where finished videos land), at most <paramref name="max"/>.</summary>
    public List<(string Rel, string Kind, long Bytes, string Dims)> Media(int max = 200)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<(string, string, long, string)>();
        var videos = System.IO.Path.GetFullPath(VideosDir);
        foreach (var f in (Directory.Exists(MediaDir) ? Walk(MediaDir) : []).Concat(Walk(Root)))
        {
            if (outp.Count >= max) break;
            if (!IsMediaExt(f) || !seen.Add(f)) continue;
            var full = System.IO.Path.GetFullPath(f);
            if (full.StartsWith(videos + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (full.Contains(System.IO.Path.DirectorySeparatorChar + ".vanity-studio" + System.IO.Path.DirectorySeparatorChar + "jobs", StringComparison.OrdinalIgnoreCase)) continue;
            // an editor's previews (edits/<name>/looks/) are not material for a video
            if (full.Contains(System.IO.Path.DirectorySeparatorChar + "looks" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            var kind = ImageExt.Contains(ext) ? "picture" : VideoExt.Contains(ext) ? "clip" : "sound";
            long size = 0; try { size = new FileInfo(f).Length; } catch { }
            var dims = "";
            if (kind == "picture") { var (w, h) = VideoText.ImageSize(f); if (w > 0) dims = $"{w}x{h}"; }
            outp.Add((System.IO.Path.GetRelativePath(Root, full).Replace('\\', '/'), kind, size, dims));
        }
        return outp;
    }

    private string Hint()
    {
        var some = Media(12).Select(m => m.Rel).ToList();
        return some.Count == 0 ? "The project has no pictures or clips yet (put them in media/)." : "Files here: " + string.Join(", ", some) + (some.Count == 12 ? ", ..." : "") + ".";
    }

    private static string Short(string s) => s.Length > 80 ? s[..77] + "..." : s;
}

/// <summary>Small JSON and media helpers shared by the video code.</summary>
public static class VideoText
{
    public static JsonObject ParseObject(string? json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
    public static string? Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
    public static double? Num(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;
    public static bool? Bool(JsonNode? n) => n is JsonValue v ? v.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
    public static int? Int(JsonNode? n) => Num(n) is { } d ? (int)Math.Round(d) : null;
    public static string? Text(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonObject o => Str(o["text"]) ?? Str(o["message"]) ?? Str(o["note"]) ?? o.ToJsonString(),
        _ => n.ToJsonString(),
    };
    public static string Short(string s) => s.Length > 60 ? s[..57] + "..." : s;
    // numbers the model reads are written the same on every locale ("1.2", never "1,2")
    public static string N(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    public static string Slug(string s, string fallback)
    {
        var slug = Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
        if (slug.Length > 50) slug = slug[..50].Trim('-');
        return slug.Length == 0 ? fallback : slug;
    }
    public static (int w, int h) FormatSize(string? format) => (format ?? "reel") switch
    {
        "square" => (1080, 1080), "portrait" => (1080, 1350), "landscape" => (1920, 1080), _ => (1080, 1920),
    };
    public static string AspectOf(string? format) => format switch { "square" => "1:1", "portrait" => "4:5", "landscape" => "16:9", _ => "9:16" };
    public static string ExtFor(string fileName, string mime)
    {
        var ext = System.IO.Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (ext.Length is > 1 and <= 5) return ext;
        return (mime ?? "").ToLowerInvariant() switch
        {
            "image/png" => ".png", "image/jpeg" or "image/jpg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif", "image/svg+xml" => ".svg",
            "video/mp4" => ".mp4", "video/webm" => ".webm", "video/quicktime" => ".mov",
            "audio/mpeg" or "audio/mp3" => ".mp3", "audio/wav" or "audio/x-wav" => ".wav", "audio/mp4" or "audio/aac" => ".m4a", "audio/ogg" => ".ogg",
            "application/json" => ".json",
            _ => ".bin",
        };
    }
    public static string MimeOf(string name) => StudioOps.MimeOf(name);
    public static string SniffImageMime(byte[] b) =>
        b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? "image/jpeg"
        : b.Length > 11 && b[0] == 0x52 && b[1] == 0x49 && b[8] == 0x57 && b[9] == 0x45 ? "image/webp"
        : "image/png";

    /// <summary>A picture's size in pixels; (0, 0) when it cannot be read (an SVG, a broken file).</summary>
    public static (int w, int h) ImageSize(string path)
    {
        try { var info = SixLabors.ImageSharp.Image.Identify(path); return info is null ? (0, 0) : (info.Width, info.Height); }
        catch { return (0, 0); }
    }

    public static (int w, int h) ImageSize(byte[] bytes)
    {
        try { using var ms = new MemoryStream(bytes); var info = SixLabors.ImageSharp.Image.Identify(ms); return info is null ? (0, 0) : (info.Width, info.Height); }
        catch { return (0, 0); }
    }

    /// <summary>Length of an audio or video file in seconds through ffprobe; a WAV is measured from its header when
    /// ffprobe is missing. 0 when unknown.</summary>
    public static double MediaSeconds(string path)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffprobe",
                $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
            if (p != null)
            {
                var outTask = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(20_000)) { try { p.Kill(true); } catch { } }
                else if (p.ExitCode == 0 && double.TryParse(outTask.Result.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
                    return d;
            }
        }
        catch { }
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 44 || System.Text.Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return 0;
            int byteRate = 0; long dataBytes = 0;
            for (int at = 12; at + 8 <= b.Length;)
            {
                var id = System.Text.Encoding.ASCII.GetString(b, at, 4); var size = BitConverter.ToInt32(b, at + 4);
                if (id == "fmt " && at + 20 <= b.Length) byteRate = BitConverter.ToInt32(b, at + 16);
                if (id == "data") { dataBytes = Math.Min(size, b.Length - at - 8); break; }
                at += 8 + Math.Max(0, size) + (size & 1);
            }
            return byteRate > 0 ? (double)dataBytes / byteRate : 0;
        }
        catch { return 0; }
    }

    public static bool HasFfprobe()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffprobe", "-version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
            if (p is null) return false;
            _ = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }
}
