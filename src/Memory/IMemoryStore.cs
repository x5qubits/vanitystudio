namespace VanityStudio.Memory;

/// <summary>Cold memory store: hybrid semantic (vector) + relational access.
/// Implementations: <c>InMemoryStore</c> (volatile) and <c>JsonFileMemoryStore</c> (durable, default).</summary>
public interface IMemoryStore
{
    /// <summary>Persist a memory entry. Upserts by <see cref="MemoryEntry.Id"/>.</summary>
    Task SaveAsync(MemoryEntry entry, CancellationToken ct = default);

    /// <summary>Semantic nearest-neighbour search: returns the top-<paramref name="topK"/> entries whose
    /// embeddings are closest to <paramref name="queryEmbedding"/> by cosine similarity.</summary>
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        float[]          queryEmbedding,
        int              topK       = 5,
        string?          agentId    = null,
        CancellationToken ct        = default);

    /// <summary>Relational lookup: all entries for a given agent, newest first.</summary>
    Task<IReadOnlyList<MemoryEntry>> GetByAgentAsync(
        string           agentId,
        int              limit      = 50,
        CancellationToken ct        = default);

    /// <summary>Delete a single entry by id.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}
