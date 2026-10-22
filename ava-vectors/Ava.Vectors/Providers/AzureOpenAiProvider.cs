using Azure;
using Azure.AI.OpenAI;
using OpenAI.Embeddings;

namespace Ava.Vectors.Providers;

/// <summary>
/// Embedding provider using Azure OpenAI's text-embedding models.
/// </summary>
public class AzureOpenAiProvider : IEmbeddingProvider
{
    private readonly EmbeddingClient _client;
    private readonly int _dimensions;

    public int Dimensions => _dimensions;

    public AzureOpenAiProvider(string endpoint, string apiKey, string deploymentName, int dimensions = 1536)
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(endpoint),
            new AzureKeyCredential(apiKey));
        _client = azureClient.GetEmbeddingClient(deploymentName);
        _dimensions = dimensions;
    }

    public async Task<float[]> EmbedAsync(string text)
    {
        var result = await _client.GenerateEmbeddingAsync(text);
        return result.Value.ToFloats().ToArray();
    }

    public async Task<List<float[]>> EmbedBatchAsync(IEnumerable<string> texts)
    {
        var textList = texts.ToList();
        var results = new List<float[]>(textList.Count);

        // Azure OpenAI supports batch embedding
        var response = await _client.GenerateEmbeddingsAsync(textList);
        foreach (var embedding in response.Value)
        {
            results.Add(embedding.ToFloats().ToArray());
        }

        return results;
    }
}
