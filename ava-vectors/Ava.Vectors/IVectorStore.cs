namespace Ava.Vectors;

/// <summary>
/// Abstraction over vector persistence. Implementations may use PostgreSQL, files, or in-memory storage.
/// </summary>
public interface IVectorStore
{
    /// <summary>Load all cached embeddings for a given collection.</summary>
    Task<Dictionary<string, float[]>> LoadCacheAsync(string collection);

    /// <summary>Persist a batch of embedding entries to the store.</summary>
    Task SaveEmbeddingsAsync(string collection, List<VectorEntry> entries);

    /// <summary>Remove all embeddings for a given collection.</summary>
    Task TruncateAsync(string collection);

    /// <summary>Count the number of stored embeddings for a given collection.</summary>
    Task<int> CountAsync(string collection);
}
