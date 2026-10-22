namespace Ava.Vectors;

/// <summary>Cache health statistics for a vector index.</summary>
public class VectorCacheStats
{
    public bool Ready { get; set; }
    public bool EmbeddingsDisabled { get; set; }
    public int IndexSize { get; set; }
    public int CachedEmbeddings { get; set; }
    public int OrphanedEmbeddings { get; set; }
    public int ItemCount { get; set; }
}
