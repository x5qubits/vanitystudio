namespace VanityStudio.Memory;

/// <summary>A single persisted memory record — text content + optional embedding vector.</summary>
public sealed class MemoryEntry
{
    public string    Id        { get; init; } = Guid.NewGuid().ToString("N");
    public string    AgentId   { get; init; } = "";
    public string    TraceId   { get; init; } = "";
    public string    Content   { get; init; } = "";
    /// <summary>Embedding vector for semantic search. Null when the entry was stored without embedding.</summary>
    public float[]?  Embedding { get; init; }
    /// <summary>Arbitrary key-value metadata (e.g. persona, skill, task snippet).</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A semantic search result: the matching entry plus its cosine similarity score.</summary>
public sealed class MemorySearchResult
{
    public MemoryEntry Entry     { get; init; } = null!;
    public float       Similarity { get; init; }
}
