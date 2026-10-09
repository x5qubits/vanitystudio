using System.Text.Json;
using VanityStudio.Llm;

namespace VanityStudio.Agent;

/// <summary>Tools whose schemas are not sent on every call. The heavy ones (desktop control, page rendering, image
/// generation) cost thousands of prompt tokens per turn and are rarely needed; they appear in the system prompt as
/// one line each under "More tools" and enter the schema through <c>load_tools</c> for the rest of the session.
/// Ported from the original agent's DeferredTools (schemas on demand).</summary>
public sealed class DeferredTools
{
    public const string LoadToolsName = "load_tools";

    private readonly IReadOnlyList<ToolDefinition> _all;
    private readonly HashSet<string> _deferred;
    private readonly HashSet<string> _revealed = new(StringComparer.OrdinalIgnoreCase);

    public DeferredTools(IReadOnlyList<ToolDefinition> all, IEnumerable<string> deferred)
    {
        _all = all;
        _deferred = new HashSet<string>(deferred.Where(n => all.Any(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))), StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<ToolDefinition> Hidden => _all.Where(t => _deferred.Contains(t.Name) && !_revealed.Contains(t.Name));

    public bool Enabled => _deferred.Count > 0;

    public bool IsLoadCall(string toolName) => Enabled && toolName.Equals(LoadToolsName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The schemas for this call: everything not deferred, the deferred ones already revealed, and
    /// load_tools while something is still hidden.</summary>
    public IReadOnlyList<ToolDefinition> Active()
    {
        if (!Enabled) return _all;
        var active = _all.Where(t => !_deferred.Contains(t.Name) || _revealed.Contains(t.Name)).ToList();
        if (Hidden.Any()) active.Add(LoadToolsDef);
        return active;
    }

    /// <summary>The one-line catalog for the system prompt, or "" when nothing is hidden.</summary>
    public string CatalogBlock()
    {
        var hidden = Hidden.ToList();
        if (hidden.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# More tools, loaded on demand");
        sb.AppendLine("Not in your schema until you call `load_tools` with their names (one call, several names); from the next turn they work like any other tool.");
        foreach (var t in hidden) sb.Append("- ").Append(t.Name).Append(": ").AppendLine(FirstLine(t.Description));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Executes a load_tools call: reveals the named tools for the rest of the session.</summary>
    public ToolResultRecord HandleLoad(LlmToolCall tc)
    {
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(tc.ArgsJson) ? "{}" : tc.ArgsJson);
            if (doc.RootElement.TryGetProperty("names", out var arr))
            {
                if (arr.ValueKind == JsonValueKind.Array) names.AddRange(arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()));
                else if (arr.ValueKind == JsonValueKind.String) names.AddRange(arr.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        catch (JsonException) { }
        var loaded = new List<string>(); var unknown = new List<string>();
        foreach (var n in names.Where(n => n.Length > 0))
        {
            var def = _all.FirstOrDefault(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (def is null) { unknown.Add(n); continue; }
            _revealed.Add(def.Name);
            loaded.Add(def.Name);
        }
        var text = loaded.Count > 0 ? "Loaded: " + string.Join(", ", loaded) + ". Available from your next turn." : "Nothing loaded.";
        if (unknown.Count > 0) text += " Unknown: " + string.Join(", ", unknown) + " (only the names under \"More tools\" can be loaded).";
        return new ToolResultRecord { ToolCallId = tc.Id, ToolName = tc.Name, Output = text, IsError = loaded.Count == 0 };
    }

    private static readonly ToolDefinition LoadToolsDef = new()
    {
        Name        = LoadToolsName,
        Description = "Load tools from the \"More tools\" list into this conversation. Pass their names; they are usable from the next turn.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["names"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" }, ["description"] = "Tool names exactly as listed under \"More tools\"." },
            },
            ["required"] = new[] { "names" },
        },
    };

    private static string FirstLine(string? description)
    {
        var s = (description ?? "").Replace("\r", "");
        var nl = s.IndexOf('\n');
        if (nl >= 0) s = s[..nl];
        var dot = s.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 40) s = s[..(dot + 1)];
        return s.Trim();
    }
}
