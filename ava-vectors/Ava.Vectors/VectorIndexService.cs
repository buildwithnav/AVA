using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Ava.Vectors;

/// <summary>
/// Generic vector index service that orchestrates embedding generation, caching, and search.
/// Uses cosine similarity for semantic search with TF-IDF keyword fallback.
/// </summary>
public class VectorIndexService<T> : IVectorIndex<T>
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _store;
    private readonly string _collection;
    private readonly ILogger<VectorIndexService<T>> _logger;

    private List<(T Item, float[] Embedding)> _index = new();
    private List<T> _allItems = new();
    private Func<T, string> _textExtractor = _ => string.Empty;
    private Func<T, string> _idExtractor = _ => string.Empty;
    private bool _ready;
    private bool _embeddingsDisabled;

    private static readonly SemaphoreSlim _rebuildLock = new(1, 1);

    public bool IsReady => _ready;
    public bool EmbeddingsDisabled => _embeddingsDisabled;

    public VectorIndexService(
        IEmbeddingProvider embeddingProvider,
        IVectorStore store,
        string collection,
        ILogger<VectorIndexService<T>> logger)
    {
        _embeddingProvider = embeddingProvider;
        _store = store;
        _collection = collection;
        _logger = logger;
    }

    public async Task BuildAsync(IEnumerable<T> items, Func<T, string> textExtractor, Func<T, string> idExtractor)
    {
        var itemList = items.ToList();
        _allItems = itemList;
        _textExtractor = textExtractor;
        _idExtractor = idExtractor;

        // Load cached embeddings from store
        var cache = await _store.LoadCacheAsync(_collection);

        var index = new List<(T, float[])>();
        var newEmbeddings = new List<VectorEntry>();

        foreach (var item in itemList)
        {
            var id = idExtractor(item);
            var text = textExtractor(item);
            var hash = ComputeHash(text);
            var cacheKey = $"{id}:{hash}";

            if (cache.TryGetValue(cacheKey, out var cached))
            {
                index.Add((item, cached));
                _logger.LogDebug("Cache hit for item '{Id}'", id);
                continue;
            }

            try
            {
                var vector = await _embeddingProvider.EmbedAsync(text);
                index.Add((item, vector));
                newEmbeddings.Add(new VectorEntry(id, hash, vector));
                _logger.LogDebug("Embedded item '{Id}'", id);
            }
            catch (Exception ex) when (IsDeploymentNotFound(ex))
            {
                _embeddingsDisabled = true;
                _logger.LogWarning("Embedding deployment not found. Semantic search disabled — using keyword fallback.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to embed item '{Id}'", id);
            }
        }

        // Persist new embeddings
        if (newEmbeddings.Count > 0)
            await _store.SaveEmbeddingsAsync(_collection, newEmbeddings);

        if (index.Count > 0)
        {
            _index = index;
            _ready = true;
            _logger.LogInformation("Vector index built — {Count} items indexed in collection '{Collection}'",
                _index.Count, _collection);
        }
        else
        {
            _logger.LogWarning("No items were indexed — semantic search disabled, using keyword fallback");
        }
    }

    public async Task<List<VectorMatch<T>>> SearchAsync(string query, int topK = 3, Func<T, bool>? filter = null)
    {
        if (!_ready || _index.Count == 0)
            return new();

        try
        {
            var queryVec = await _embeddingProvider.EmbedAsync(query);

            var candidates = _index
                .Where(e => filter == null || filter(e.Item))
                .Select(e => new VectorMatch<T>(e.Item, CosineSimilarity(queryVec, e.Embedding)))
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();

            return candidates;
        }
        catch (Exception ex) when (IsDeploymentNotFound(ex))
        {
            _embeddingsDisabled = true;
            _ready = false;
            _logger.LogWarning("Embedding deployment not found during search — falling back to keyword matching");
            return new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Semantic search failed");
            return new();
        }
    }

    public List<VectorMatch<T>> KeywordSearch(string query, int topK = 3, Func<T, bool>? filter = null)
    {
        if (_allItems.Count == 0) return new();

        var pool = filter == null ? _allItems : _allItems.Where(filter).ToList();
        if (pool.Count == 0) return new();

        var queryWords = Tokenize(query);

        // Build IDF: for each word, how many items contain it
        var docFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in pool)
        {
            var text = _textExtractor(item);
            var words = Tokenize(text);
            foreach (var w in words)
                docFreq[w] = docFreq.TryGetValue(w, out var c) ? c + 1 : 1;
        }

        int n = pool.Count;

        var scored = pool
            .Select(item =>
            {
                var text = _textExtractor(item);
                var itemWords = Tokenize(text);
                float score = 0f;
                foreach (var w in queryWords)
                {
                    if (!itemWords.Contains(w)) continue;
                    var df = docFreq.TryGetValue(w, out var d) ? d : 1;
                    score += MathF.Log((float)n / df + 1f);
                }
                return new VectorMatch<T>(item, score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        return scored;
    }

    public async Task<VectorRebuildResult> RebuildAsync(
        IEnumerable<T> items, Func<T, string> textExtractor, Func<T, string> idExtractor)
    {
        if (!await _rebuildLock.WaitAsync(TimeSpan.Zero))
            return new VectorRebuildResult { Status = "already_running" };

        try
        {
            var sw = Stopwatch.StartNew();
            var previousCount = _index.Count;

            await _store.TruncateAsync(_collection);
            _index = new();
            _ready = false;
            _embeddingsDisabled = false;

            _logger.LogInformation("Vector cache invalidated for collection '{Collection}'", _collection);

            await BuildAsync(items, textExtractor, idExtractor);

            sw.Stop();
            return new VectorRebuildResult
            {
                Status = "rebuilt",
                PreviousCount = previousCount,
                NewCount = _index.Count,
                DurationMs = (int)sw.ElapsedMilliseconds
            };
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    public async Task<VectorCacheStats> GetStatsAsync()
    {
        try
        {
            var cachedCount = await _store.CountAsync(_collection);
            var currentIds = _allItems.Select(_idExtractor).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cache = await _store.LoadCacheAsync(_collection);

            // Orphaned = stored in cache but item ID not in current set
            var orphanCount = cache.Keys
                .Select(k => k.Split(':')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(id => !currentIds.Contains(id));

            return new VectorCacheStats
            {
                Ready = _ready,
                EmbeddingsDisabled = _embeddingsDisabled,
                IndexSize = _index.Count,
                CachedEmbeddings = cachedCount,
                OrphanedEmbeddings = orphanCount,
                ItemCount = _allItems.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get cache stats");
            return new VectorCacheStats
            {
                Ready = _ready,
                EmbeddingsDisabled = _embeddingsDisabled,
                IndexSize = _index.Count,
                ItemCount = _allItems.Count
            };
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16];
    }

    private static HashSet<string> Tokenize(string text) =>
        new(text.ToLowerInvariant()
            .Split([' ', ',', '.', '-', '_', '/', '\\', '(', ')', '\n', '\r', '\t'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2),
            StringComparer.OrdinalIgnoreCase);

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    private static bool IsDeploymentNotFound(Exception ex)
    {
        // Azure.RequestFailedException with 404 indicates deployment not found
        return ex is Azure.RequestFailedException { Status: 404 };
    }
}
