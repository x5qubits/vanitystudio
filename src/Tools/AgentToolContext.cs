namespace VanityStudio.Tools;

/// <summary>Ambient context flowing through the async call chain so stateless tool singletons can tell which agent
/// (the main loop or a sub-agent) is calling them.</summary>
public static class AgentToolContext
{
    private static readonly AsyncLocal<string?> _agentId = new();
    public static string? AgentId { get => _agentId.Value; set => _agentId.Value = value; }

    private static readonly AsyncLocal<string?> _runId = new();
    public static string? RunId { get => _runId.Value; set => _runId.Value = value; }

    private static readonly AsyncLocal<int> _depth = new();
    /// <summary>0 for the operator's own conversation, 1 for a sub-agent spawned from it.</summary>
    public static int Depth { get => _depth.Value; set => _depth.Value = value; }

    /// <summary>Kept for the file tools: diagnostic reads are always allowed here.</summary>
    public static bool AllowScopeReads => true;
}
