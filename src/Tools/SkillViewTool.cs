using System.Text.Json;
using VanityStudio.Agent;
using VanityStudio.Llm;

namespace VanityStudio.Tools;

/// <summary>Loads a skill's playbook into the conversation on demand. The catalog in the system prompt names the
/// skills; this returns the body of one.</summary>
public sealed class SkillViewTool : ITool
{
    private readonly Func<PromptLibrary> _library;

    public SkillViewTool(Func<PromptLibrary> library) => _library = library;

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "skill_view",
        Description = "Load a skill (a playbook of steps and rules for a kind of task) by name, as listed under \"Skills you may load\". " +
                      "Call it when the task matches a skill's description, before starting the work; without a name it lists the skills.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The skill name exactly as listed." },
            },
        },
    };

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        string name = "";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString() ?? "";
        }
        catch (JsonException) { }

        var lib = _library();
        if (name.Trim().Length == 0)
            return Task.FromResult(lib.Skills.Count == 0 ? "No skills are defined." :
                string.Join("\n", lib.Skills.Select(s => $"- {s.Name}: {s.Description}")));
        var skill = lib.Skill(name);
        if (skill is null)
            return Task.FromResult($"No skill named '{name}'. Available: {string.Join(", ", lib.Skills.Select(s => s.Name))}");
        return Task.FromResult($"# Skill: {skill.Name}\n{skill.Playbook}");
    }
}
