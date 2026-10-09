using VanityStudio.Llm;

namespace VanityStudio.Tools;

public interface IToolRegistry
{
    void Register(ITool tool);
    bool TryGet(string name, out ITool? tool);
    IReadOnlyList<ToolDefinition> GetDefinitions(IEnumerable<string> toolNames);
    IReadOnlyList<ToolDefinition> All { get; }
}
