using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using VanityStudio.Llm;
using VanityStudio.Tools;
using VanityStudio.Infra;

namespace VanityStudio.Tools;

/// <summary>Reads file contents with line numbering and range pagination. Renders images and PDFs; parses Jupyter notebooks.</summary>
public sealed class ReadFileTool : ITool, IVisualTool
{
    public const int MaxLineChars = 2000;
    private readonly string _baseDir;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    public ReadFileTool(string? baseDir = null)
    {
        _baseDir = baseDir ?? Directory.GetCurrentDirectory();
    }

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "read_file",
        Description = "Reads a file from the local filesystem. You can access any file directly by using this tool.\n" +
                      "Assume this tool is able to read all files on the machine. If the User provides a path to a file assume that path is valid. It is okay to read a file that does not exist; an error will be returned.\n" +
                      "Usage:\n" +
                      "- The file_path may be absolute or relative to the working directory\n" +
                      "- By default, it reads up to 2000 lines starting from the beginning of the file\n" +
                      "- Once a search hit or a plan step names the region you need, read that whole region in one call. Do not walk a file in small slices: every call is a full turn.\n" +
                      "- Several files, or several regions, go in ONE call through files instead of one call each.\n" +
                      "- Results are returned using cat -n format, with line numbers starting at 1\n" +
                      "- This tool allows you to read images (eg PNG, JPG, etc). When reading an image file the contents are presented visually.\n" +
                      "- This tool can read PDF files (.pdf). For large PDFs (more than 10 pages), you MUST provide the pages parameter to read specific page ranges (e.g., pages: \"1-5\"). Reading a large PDF without the pages parameter will fail. Maximum 20 pages per request.\n" +
                      "- This tool can read Jupyter notebooks (.ipynb files) and returns all cells with their outputs, combining code, text, and visualizations.\n" +
                      "- This tool can only read files, not directories. To list files in a directory, use the glob or bash tool.\n" +
                      "- You will regularly be asked to read screenshots. If the user provides a path to a screenshot, ALWAYS use this tool to view the file at the path. This tool will work with all temporary file paths.\n" +
                      "- If you read a file that exists but has empty contents you will receive a message in place of file contents.\n" +
                      "- Do NOT re-read a file you just edited to verify — edit_file/write_files would have errored if the change failed.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["file_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The absolute path to the file to read. Omit it when you pass files." },
                ["files"]     = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["description"] = "Several files or regions in ONE call, text and pictures alike. Each entry: {\"file_path\": \"...\", \"offset\": n, \"limit\": n}, offset and limit optional. Up to " + MaxListEntries + " entries; one file may appear more than once with different ranges. Results come back in the order given, each under a line that names the file and the lines returned.",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["file_path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The absolute path to the file to read" },
                            ["offset"]    = new Dictionary<string, object> { ["type"] = "number", ["description"] = "The line number to start reading from" },
                            ["limit"]     = new Dictionary<string, object> { ["type"] = "number", ["description"] = "The number of lines to read" },
                        },
                        ["required"] = new[] { "file_path" },
                    },
                },
                ["offset"]    = new Dictionary<string, object> { ["type"] = "number", ["description"] = "The line number to start reading from. Only provide if the file is too large to read at once" },
                ["limit"]     = new Dictionary<string, object> { ["type"] = "number", ["description"] = "The number of lines to read. Only provide if the file is too large to read at once." },
                ["pages"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Page range for PDF files (e.g., \"1-5\", \"3\", \"10-20\"). Only applicable to PDF files. Maximum 20 pages per request." },
            },
            // No top-level "required": a call names either file_path or files, and the tool says so when both are missing.
        },
    };

    /// <summary>How many entries one list read takes.</summary>
    public const int MaxListEntries = 12;
    /// <summary>What a list read returns in total. It stays under the cap the history puts on ONE tool result
    /// (<see cref="core.Chat.HistoryLifecycleManager.MaxToolOutputChars"/>): past that cap a result is cut head and
    /// tail with the middle removed, which for source text is a silent hole. So the list is budgeted HERE, on line
    /// boundaries, and every entry that was cut short says where to continue.</summary>
    public const int ListBudgetChars = VanityStudio.Agent.HistoryLifecycleManager.MaxToolOutputChars - 3_000;

    /// <summary>Several files or regions in ONE call. Null when the call carries no list.
    /// A tool that takes one file makes every further file a further turn, and each turn re-sends the whole task
    /// so far: in one project build 579 of 2,798 turns were a read or a search straight after another one, and
    /// half of all file reads were a second or later read of a file already opened in that task (2026-10-04).
    /// write_files and edit_file already take arrays; reading now does too.</summary>
    /// <param name="images">Where the pictures in the list go, when the caller can show pictures. A list of two
    /// attached screenshots used to come back as two "read this one on its own" notes, and the next turn was spent
    /// reading them again one by one (2026-10-05).</param>
    private string? ReadList(JsonElement root, List<string>? images = null)
    {
        if (!root.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array) return null;
        var entries = new List<(string Path, int Offset, int Limit)>();
        foreach (var e in filesEl.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.String) { var p = e.GetString() ?? ""; if (p.Length > 0) entries.Add((p, 1, 2000)); continue; }
            if (e.ValueKind != JsonValueKind.Object) continue;
            var path = e.TryGetProperty("file_path", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() ?? "" : "";
            if (path.Length == 0) continue;
            int offset = e.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.Number ? Math.Max(1, off.GetInt32()) : 1;
            int limit  = e.TryGetProperty("limit",  out var lim) && lim.ValueKind == JsonValueKind.Number ? Math.Clamp(lim.GetInt32(), 1, 8000) : 2000;
            entries.Add((path, offset, limit));
        }
        if (entries.Count == 0) return "Error: files is empty. Give each entry a file_path.";
        int skipped = Math.Max(0, entries.Count - MaxListEntries);
        if (skipped > 0) entries = entries.Take(MaxListEntries).ToList();

        var sb = new StringBuilder();
        int remaining = ListBudgetChars;
        for (int i = 0; i < entries.Count; i++)
        {
            var (path, offset, limit) = entries[i];
            // An even share of what is left: a short file hands its unused room to the ones after it.
            int share = Math.Max(400, remaining / (entries.Count - i));
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var part = ImageExtensions.Contains(ext) && images != null ? ReadImageInto(path, ext, images)
                : ImageExtensions.Contains(ext) || ext is ".pdf" or ".ipynb" ? "[not a text file: read this one on its own with file_path]"
                : ReadOne(path, offset, limit, share, header: true);
            if (!part.StartsWith("=== ", StringComparison.Ordinal)) part = $"=== {path} ===\n{part}";   // an error or a note still says whose it is
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(part);
            remaining -= part.Length;
        }
        if (skipped > 0) sb.Append($"\n\n[{skipped} more entr{(skipped == 1 ? "y" : "ies")} not read: one call takes up to {MaxListEntries}]");
        return sb.ToString();
    }

    /// <summary>One picture of a list read: added to <paramref name="images"/>, and named by its place among them so
    /// the text says which picture is which.</summary>
    private string ReadImageInto(string relPath, string ext, List<string> images)
    {
        string fullPath;
        try { fullPath = VanityPathHelper.NormalizeAndResolveStrict(relPath, _baseDir); }
        catch (Exception ex) { return "Error: " + ex.Message; }
        if (!File.Exists(fullPath)) return $"Error: File '{relPath}' not found.";
        try
        {
            var mime = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/png" };
            images.Add($"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(fullPath))}");
            return $"[image {images.Count} shown below]";
        }
        catch (Exception ex) { return $"Error reading file '{relPath}': {ex.Message}"; }
    }

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root      = doc.RootElement;

        if (ReadList(root) is { } listed) return Task.FromResult(listed);

        var relPath = root.TryGetProperty("file_path", out var fp) ? fp.GetString() ?? "" : "";
        if (relPath.Length == 0) return Task.FromResult("Error: file_path (or files) is required.");

        var ext = Path.GetExtension(relPath).ToLowerInvariant();

        if (ext == ".pdf")
        {
            var pages = root.TryGetProperty("pages", out var pg) ? pg.GetString()?.Trim() ?? "" : "";
            return Task.FromResult(ReadPdf(relPath, pages));
        }

        if (ext == ".ipynb")
            return Task.FromResult(ReadNotebook(relPath));

        int offset = root.TryGetProperty("offset", out var off) ? Math.Max(1, off.GetInt32()) : 1;
        int limit  = root.TryGetProperty("limit",  out var lim) ? Math.Clamp(lim.GetInt32(), 1, 8000) : 2000;

        return Task.FromResult(ReadOne(relPath, offset, limit));
    }

    public Task<ToolResultRecord> ExecuteVisualAsync(string toolCallId, string argsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root      = doc.RootElement;

        var images = new List<string>();
        if (ReadList(root, images) is { } listed)
            return Task.FromResult(listed.StartsWith("Error:", StringComparison.Ordinal)
                ? Err(toolCallId, listed)
                : new ToolResultRecord
                {
                    ToolCallId = toolCallId, ToolName = "read_file", Output = listed,
                    // Both fields, as page_view does for several pages: the request to the model sends the list, and the
                    // parts of the harness that know one picture per result still see one.
                    ScreenshotDataUrl = images.Count > 0 ? images[0] : null,
                    ImageDataUrls     = images.Count > 1 ? images : null,
                });

        var relPath = root.TryGetProperty("file_path", out var fp) ? fp.GetString() ?? "" : "";
        if (relPath.Length == 0)
            return Task.FromResult(Err(toolCallId, "Error: file_path (or files) is required."));

        string fullPath;
        try { fullPath = VanityPathHelper.NormalizeAndResolveStrict(relPath, _baseDir); }
        catch (Exception ex) { return Task.FromResult(Err(toolCallId, "Error: " + ex.Message)); }

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();

        if (ImageExtensions.Contains(ext))
        {
            if (!File.Exists(fullPath))
                return Task.FromResult(Err(toolCallId, $"Error: File '{relPath}' not found."));
            try
            {
                var bytes   = File.ReadAllBytes(fullPath);
                var mime    = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/png" };
                var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                return Task.FromResult(new ToolResultRecord
                {
                    ToolCallId        = toolCallId,
                    ToolName          = "read_file",
                    Output            = $"[image '{relPath}' shown below]",
                    ScreenshotDataUrl = dataUrl,
                });
            }
            catch (Exception ex) { return Task.FromResult(Err(toolCallId, $"Error reading file '{relPath}': {ex.Message}")); }
        }

        if (ext == ".pdf")
        {
            var pagesSpec = root.TryGetProperty("pages", out var pg) ? pg.GetString()?.Trim() ?? "" : "";
            var pdfText = ReadPdf(relPath, pagesSpec);
            return Task.FromResult(pdfText.StartsWith("Error", StringComparison.Ordinal)
                ? Err(toolCallId, pdfText)
                : new ToolResultRecord { ToolCallId = toolCallId, ToolName = "read_file", Output = pdfText });
        }

        if (ext == ".ipynb")
            return Task.FromResult(new ToolResultRecord { ToolCallId = toolCallId, ToolName = "read_file", Output = ReadNotebook(relPath) });

        int offset = root.TryGetProperty("offset", out var off) ? Math.Max(1, off.GetInt32()) : 1;
        int limit  = root.TryGetProperty("limit",  out var lim) ? Math.Clamp(lim.GetInt32(), 1, 8000) : 2000;
        return Task.FromResult(new ToolResultRecord { ToolCallId = toolCallId, ToolName = "read_file", Output = ReadOne(relPath, offset, limit) });
    }

    private ToolResultRecord Err(string toolCallId, string msg) =>
        new() { ToolCallId = toolCallId, ToolName = "read_file", Output = msg, IsError = true };

    // ── PDF ───────────────────────────────────────────────────────────────────

    private string ReadPdf(string relPath, string pagesSpec)
    {
        string fullPath;
        try { fullPath = VanityPathHelper.NormalizeAndResolveStrict(relPath, _baseDir); }
        catch (Exception ex) { return "Error: " + ex.Message; }

        if (!AgentToolContext.AllowScopeReads && VanityPathHelper.IsDeniedForAgent(fullPath, _baseDir, out var scopeMsg))
            return scopeMsg;

        if (!File.Exists(fullPath))
            return $"Error: File '{relPath}' not found.";

        try
        {
            using var pdf      = PdfDocument.Open(fullPath);
            int totalPages     = pdf.NumberOfPages;

            var (from, to)     = ParsePageRange(pagesSpec, totalPages);
            if (from < 0)
                return $"Error: {to} pages — use pages parameter e.g. \"1-10\"."; // to holds the error text in this branch

            int span = to - from + 1;
            if (span > 20)
                return $"Error: pages range covers {span} pages; maximum 20 pages per request. Narrow the range (e.g. pages: \"1-20\").";

            if (string.IsNullOrEmpty(pagesSpec) && totalPages > 10)
                return $"Error: This PDF has {totalPages} pages. You MUST provide the pages parameter to read specific page ranges (e.g., pages: \"1-5\"). Reading a large PDF without the pages parameter will fail. Maximum 20 pages per request.";

            var sb = new StringBuilder();
            sb.AppendLine($"[PDF: {relPath}, pages {from}-{to} of {totalPages}]");
            sb.AppendLine();
            for (int p = from; p <= to; p++)
            {
                var page = pdf.GetPage(p);
                string text;
                try { text = ContentOrderTextExtractor.GetText(page); }
                catch { text = page.Text ?? ""; }
                sb.AppendLine($"--- Page {p} ---");
                sb.AppendLine(text.Trim());
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error reading PDF '{relPath}': {ex.Message}";
        }
    }

    private static (int from, int to) ParsePageRange(string spec, int total)
    {
        if (string.IsNullOrEmpty(spec)) return (1, Math.Min(total, 10));

        var parts = spec.Trim().Split('-');
        if (parts.Length == 1 && int.TryParse(parts[0].Trim(), out int single) && single >= 1 && single <= total)
            return (single, single);
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int f) && int.TryParse(parts[1].Trim(), out int t)
            && f >= 1 && t >= f && f <= total)
            return (f, Math.Min(t, total));

        return (-1, 0);
    }

    // ── Jupyter notebook (.ipynb) ─────────────────────────────────────────────

    private string ReadNotebook(string relPath)
    {
        string fullPath;
        try { fullPath = VanityPathHelper.NormalizeAndResolveStrict(relPath, _baseDir); }
        catch (Exception ex) { return "Error: " + ex.Message; }

        if (!AgentToolContext.AllowScopeReads && VanityPathHelper.IsDeniedForAgent(fullPath, _baseDir, out var scopeMsg))
            return scopeMsg;

        if (!File.Exists(fullPath))
            return $"Error: File '{relPath}' not found.";

        try
        {
            var nb   = JsonNode.Parse(File.ReadAllText(fullPath));
            var cells = nb?["cells"]?.AsArray();
            if (cells is null) return $"Error: '{relPath}' does not appear to be a valid Jupyter notebook (no 'cells' array).";

            var sb = new StringBuilder();
            sb.AppendLine($"[Jupyter notebook: {relPath}, {cells.Count} cell(s)]");
            int idx = 0;
            foreach (var cell in cells)
            {
                idx++;
                var type    = cell?["cell_type"]?.GetValue<string>() ?? "unknown";
                var source  = JoinSource(cell?["source"]);
                sb.AppendLine();
                sb.AppendLine($"--- Cell {idx} [{type}] ---");
                if (!string.IsNullOrWhiteSpace(source)) sb.AppendLine(source);

                if (type == "code")
                {
                    var outputs = cell?["outputs"]?.AsArray();
                    if (outputs is { Count: > 0 })
                    {
                        sb.AppendLine("[outputs]");
                        foreach (var o in outputs)
                        {
                            var otype = o?["output_type"]?.GetValue<string>() ?? "";
                            if (otype is "stream")
                            {
                                var text = JoinSource(o?["text"]);
                                if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
                            }
                            else if (otype is "execute_result" or "display_data")
                            {
                                var textData = JoinSource(o?["data"]?["text/plain"]);
                                if (!string.IsNullOrWhiteSpace(textData)) sb.AppendLine(textData);
                                else sb.AppendLine($"[{otype}: non-text output]");
                            }
                            else if (!string.IsNullOrEmpty(otype))
                                sb.AppendLine($"[{otype}]");
                        }
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error reading notebook '{relPath}': {ex.Message}";
        }
    }

    private static string JoinSource(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonArray arr)
            return string.Concat(arr.Select(x => x?.GetValue<string>() ?? ""));
        try { return node.GetValue<string>(); } catch { return ""; }
    }

    // ── Text file ─────────────────────────────────────────────────────────────

    /// <param name="maxChars">A list read's share for this entry: the body stops at the last whole line that fits
    /// and says where to continue. A single read passes no limit and is returned exactly as before.</param>
    /// <param name="header">A list read puts the file and the lines actually returned on the first line.</param>
    private string ReadOne(string relPath, int startLine, int lineCount, int maxChars = int.MaxValue, bool header = false)
    {
        string fullPath;
        try { fullPath = VanityPathHelper.NormalizeAndResolveStrict(relPath, _baseDir); }
        catch (Exception ex) { return "Error: " + ex.Message; }

        if (!AgentToolContext.AllowScopeReads && VanityPathHelper.IsDeniedForAgent(fullPath, _baseDir, out var scopeMsg))
            return scopeMsg;

        if (!File.Exists(fullPath))
        {
            Log.Warn($"[read_file] file not found: {fullPath}");
            return $"Error: File '{relPath}' not found.";
        }

        try
        {
            var allLines   = File.ReadAllLines(fullPath);
            int totalLines = allLines.Length;

            if (totalLines == 0)
                return $"File '{relPath}' is empty.";

            int startIndex = Math.Min(startLine - 1, totalLines - 1);
            int count      = Math.Min(lineCount, totalLines - startIndex);

            var sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < count; i++)
            {
                int lineNum = startIndex + i + 1;
                var line    = allLines[startIndex + i];
                if (line.Length > MaxLineChars) line = line[..MaxLineChars] + $" … [line cut at {MaxLineChars} chars; {line.Length} in the file]";
                if (shown > 0 && (long)sb.Length + line.Length + 12 > maxChars) break;   // a list entry's share is used up
                sb.Append(lineNum).Append('\t').AppendLine(line);
                shown++;
            }

            if (shown < count)
                sb.Append($"… [stopped after line {startIndex + shown} to fit this result; continue this file with offset {startIndex + shown + 1}]");
            else if (startIndex + count < totalLines)
                sb.Append($"… [{totalLines - (startIndex + count)} more line(s) not shown; raise head_limit or use offset]");

            var body = sb.ToString().TrimEnd();
            return header ? $"=== {relPath} (lines {startIndex + 1}-{startIndex + shown} of {totalLines}) ===\n{body}" : body;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            var msg = ex.Message.Replace(fullPath, relPath, StringComparison.OrdinalIgnoreCase)
                                 .Replace(_baseDir, "", StringComparison.OrdinalIgnoreCase);
            return $"Error reading file '{relPath}': {msg}";
        }
    }
}
