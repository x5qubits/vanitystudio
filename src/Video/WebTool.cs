using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VanityStudio.Infra;
using VanityStudio.Llm;
using VanityStudio.Tools;
using static VanityStudio.Video.VideoText;

namespace VanityStudio.Video;

/// <summary>
/// What a web page says, for a video about what it sells: <c>read</c> opens the address in the renderer's browser
/// (scripts run, so a shop that builds its page in the browser reads as a visitor sees it) and returns its title,
/// description, prices, the product data it publishes, its main text and its pictures; <c>download</c> saves pictures
/// from it into the project's media/web/, so a video shows the real product, not a made-up one.
/// </summary>
public sealed class WebTool : ITool
{
    private readonly StudioProject _project;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public WebTool(StudioProject project) { _project = project; }

    public ToolDefinition Definition { get; } = new()
    {
        Name = "web",
        Description =
            "Read a web page for the facts of a video: read url=<address> returns its title, description, prices, the product data it " +
            "publishes, its main text and its pictures (address and pixel size). download urls=[picture addresses] saves pictures into " +
            "media/web/ and returns the paths to use in a script or doc. Use it whenever the request names a site or a product page: the " +
            "facts, the price and the pictures come from the page, never from memory. For the steps of a how-to (what to click), use " +
            "make_video action=site.",
        Parameters = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "read", "download" } },
                ["url"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "read: the page's address." },
                ["clicks"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" }, ["description"] = "read: what to click first, by its words (a tab with the specifications, a cookie banner's accept)." },
                ["urls"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" }, ["description"] = "download: picture addresses from read (at most 12)." },
            },
            ["required"] = new[] { "action" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        JsonElement a;
        try { a = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException ex) { return "Error: arguments are not valid JSON: " + ex.Message; }
        var action = a.TryGetProperty("action", out var ac) ? ac.GetString()?.Trim().ToLowerInvariant() : "";
        List<string> Arr(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
            : a.TryGetProperty(k, out var s) && s.ValueKind == JsonValueKind.String ? [s.GetString()!.Trim()] : [];
        try
        {
            return action switch
            {
                "read" => await ReadAsync(a.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "", Arr("clicks"), ct).ConfigureAwait(false),
                "download" => await DownloadAsync(Arr("urls").Concat(Arr("url")).Distinct().ToList(), ct).ConfigureAwait(false),
                _ => "Error: web needs action = read | download.",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    /// <summary>The page as text for the model (also `vanity-studio read`).</summary>
    public static async Task<string> ReadAsync(string url, List<string> clicks, CancellationToken ct)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "Error: read needs url: the page's address (https://...).";
        var cmd = new Dictionary<string, object> { { "action", "read" }, { "url", url }, { "clicks", clicks.Cast<object>().ToList() }, { "format", "landscape" } };
        var res = await Task.Run(() => StudioOps.Dispatch(cmd, l => Log.Info(l)), ct).ConfigureAwait(false);
        var page = ParseObject(Convert.ToString(res.GetValueOrDefault("page")));
        if (page.Count == 0) return "Error: the page could not be read: " + url;
        var sb = new StringBuilder();
        sb.Append($"{Str(page["title"])} ({Str(page["url"])})" + (Str(page["lang"]) is { Length: > 0 } lang ? $", language {lang}" : "") + "\n");
        if (Str(page["ogTitle"]) is { Length: > 0 } og && og != Str(page["title"])) sb.Append("Name: " + og + "\n");
        if (Str(page["description"]) is { Length: > 0 } d) sb.Append("Description: " + d + "\n");
        var price = Str(page["price"]);
        if (price is { Length: > 0 }) sb.Append($"Price (published): {price} {Str(page["currency"])}\n");
        if (page["prices"] is JsonArray ps && ps.Count > 0) sb.Append("Prices on the page: " + string.Join(" | ", ps.Select(Str)) + "\n");
        if (page["colors"] is JsonArray cs && cs.Count > 0)
            sb.Append("Brand colours (theme, then buttons and headings, most used first): " + string.Join(", ", cs.Select(Str)) + " → brand.colors (the first one or two)\n");
        if (Str(page["logo"]) is { Length: > 0 } logo) sb.Append("Logo: " + logo + " → web download it, then brand save logo=<its path> (it becomes media:logo)\n");
        if (page["headings"] is JsonArray hs && hs.Count > 0) sb.Append("Headings: " + string.Join(" · ", hs.Select(Str)) + "\n");
        if (Str(page["data"]) is { Length: > 2 } data && data != "[]") sb.Append("Product data the page publishes (JSON-LD): " + data + "\n");
        if (Str(page["text"]) is { Length: > 0 } text) sb.Append("Text:\n" + text + "\n");
        if (page["pictures"] is JsonArray pics && pics.Count > 0)
        {
            sb.Append("Pictures (download the ones the video shows):\n");
            foreach (var p in pics.OfType<JsonObject>())
                sb.Append($"- {Str(p["src"])}" + (Num(p["w"]) is > 0 ? $" ({N(Num(p["w"])!.Value)}x{N(Num(p["h"]) ?? 0)})" : "") +
                          (Str(p["alt"]) is { Length: > 0 } alt ? $" \"{alt}\"" : "") + (Str(p["where"]) is { Length: > 0 } w ? $" [{w}]" : "") + "\n");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> DownloadAsync(List<string> urls, CancellationToken ct)
    {
        if (urls.Count == 0) return "Error: download needs urls: the picture addresses from read.";
        if (urls.Count > 12) return "Error: at most 12 pictures at a time.";
        var dir = Path.Combine(_project.MediaDir, "web");
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        foreach (var url in urls)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { lines.Add($"- {url}: not a web address"); continue; }
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/png,image/jpeg,image/*;q=0.8");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) { lines.Add($"- {url}: HTTP {(int)resp.StatusCode}"); continue; }
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var mime = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!mime.StartsWith("image/")) mime = SniffImageMime(bytes);
                if (bytes.Length < 2000 || bytes.Length > 40 * 1024 * 1024) { lines.Add($"- {url}: not a usable picture ({bytes.Length} bytes)"); continue; }
                var stem = Slug(Path.GetFileNameWithoutExtension(uri.AbsolutePath), "picture");
                var ext = ExtFor(Path.GetFileName(uri.AbsolutePath), mime);
                if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".avif")) ext = ExtFor("", mime);
                var file = Path.Combine(dir, stem + ext);
                for (int i = 2; File.Exists(file) && new FileInfo(file).Length != bytes.Length; i++) file = Path.Combine(dir, $"{stem}-{i}{ext}");
                await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
                var (w, h) = ImageSize(file);
                var rel = Path.GetRelativePath(_project.Root, file).Replace('\\', '/');
                lines.Add($"- {rel}" + (w > 0 ? $" ({w}x{h}{(Math.Max(w, h) < 1000 ? ", small: fine as a layer, soft as a full-frame scene" : "")})" : ""));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lines.Add($"- {url}: {ex.Message}"); }
        }
        return "Saved in media/web/:\n" + string.Join("\n", lines) + "\nName them in a script or doc by these paths; look at one with read_file or make_video action=look.";
    }
}
