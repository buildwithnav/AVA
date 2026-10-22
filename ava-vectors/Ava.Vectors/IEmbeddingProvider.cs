namespace Ava.Vectors;

/// <summary>
/// Abstraction over an embedding provider (e.g., Azure OpenAI, Ollama, etc.).
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Generate an embedding vector for a single text input.</summary>
    Task<float[]> EmbedAsync(string text);

    /// <summary>Generate embedding vectors for multiple text inputs in batch.</summary>
    Task<List<float[]>> EmbedBatchAsync(IEnumerable<string> texts);

    /// <summary>The dimensionality of the embedding vectors produced.</summary>
    int Dimensions { get; }
}
