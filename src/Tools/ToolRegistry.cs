using VanityStudio.Llm;

namespace VanityStudio.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool) => _tools[tool.Definition.Name] = tool;

    public bool TryGet(string name, out ITool? tool) => _tools.TryGetValue(name, out tool);

    public IReadOnlyList<ToolDefinition> GetDefinitions(IEnumerable<string> toolNames)
        => toolNames
            .Where(n => _tools.ContainsKey(n))
            .Select(n => _tools[n].Definition)
            .ToList();

    public IReadOnlyList<ToolDefinition> All
        => _tools.Values.Select(t => t.Definition).ToList();

    /// <summary>The registered tool instances, for a host that builds a restricted registry out of this one.</summary>
    public IEnumerable<ITool> Tools => _tools.Values;
}
