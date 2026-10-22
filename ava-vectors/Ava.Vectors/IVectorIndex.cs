namespace Ava.Vectors;

/// <summary>
/// Generic vector index that supports semantic and keyword search over any item type.
/// </summary>
public interface IVectorIndex<T>
{
    /// <summary>Build the index from a set of items.</summary>
    Task BuildAsync(IEnumerable<T> items, Func<T, string> textExtractor, Func<T, string> idExtractor);

    /// <summary>Search for similar items by text query using cosine similarity.</summary>
    Task<List<VectorMatch<T>>> SearchAsync(string query, int topK = 3, Func<T, bool>? filter = null);

    /// <summary>Keyword-based fallback search using TF-IDF scoring.</summary>
    List<VectorMatch<T>> KeywordSearch(string query, int topK = 3, Func<T, bool>? filter = null);

    /// <summary>Invalidate cache and rebuild from scratch.</summary>
    Task<VectorRebuildResult> RebuildAsync(IEnumerable<T> items, Func<T, string> textExtractor, Func<T, string> idExtractor);

    /// <summary>Get cache health statistics.</summary>
    Task<VectorCacheStats> GetStatsAsync();

    /// <summary>True when the embedding index is built and semantic search is available.</summary>
    bool IsReady { get; }

    /// <summary>True when embedding calls have been permanently disabled due to a deployment error.</summary>
    bool EmbeddingsDisabled { get; }
}
