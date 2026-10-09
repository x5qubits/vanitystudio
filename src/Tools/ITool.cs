using VanityStudio.Llm;

namespace VanityStudio.Tools;

public interface ITool
{
    ToolDefinition Definition { get; }
    Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default);
}

/// <summary>Visual-capable tool that can return rich multimodal artifacts such as screenshots.</summary>
public interface IVisualTool : ITool
{
    Task<ToolResultRecord> ExecuteVisualAsync(string toolCallId, string argsJson, CancellationToken ct = default);
}
