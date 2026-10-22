using System.Collections.Concurrent;

namespace Ava.Vectors.Stores;

/// <summary>
/// In-memory vector store for testing and lightweight scenarios.
/// Data is lost when the process exits.
/// </summary>
public class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<string, Dictionary<string, float[]>> _collections = new();

    public Task<Dictionary<string, float[]>> LoadCacheAsync(string collection)
    {
        var cache = _collections.GetOrAdd(collection, _ => new Dictionary<string, float[]>());
        return Task.FromResult(new Dictionary<string, float[]>(cache));
    }

    public Task SaveEmbeddingsAsync(string collection, List<VectorEntry> entries)
    {
        var cache = _collections.GetOrAdd(collection, _ => new Dictionary<string, float[]>());

        foreach (var entry in entries)
        {
            var key = $"{entry.Id}:{entry.ContentHash}";
            cache[key] = entry.Vector;
        }

        return Task.CompletedTask;
    }

    public Task TruncateAsync(string collection)
    {
        _collections.TryRemove(collection, out _);
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(string collection)
    {
        var count = _collections.TryGetValue(collection, out var cache) ? cache.Count : 0;
        return Task.FromResult(count);
    }
}
