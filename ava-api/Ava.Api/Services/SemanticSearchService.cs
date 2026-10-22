using Ava.Api.Config;
using Ava.Api.Data;
using Ava.Api.Models;
using Ava.Vectors;
using Ava.Vectors.Providers;
using Ava.Vectors.Stores;
using Microsoft.Extensions.Options;

namespace Ava.Api.Services;

/// <summary>
/// Embeds prompt descriptions and context tags on startup.
/// Performs cosine similarity search to match user intent.
/// Delegates vector operations to the shared Ava.Vectors library.
/// Falls back to keyword matching when the embedding deployment is unavailable.
/// </summary>
public class SemanticSearchService
{
    private readonly IVectorIndex<PromptDefinition> _vectorIndex;
    private readonly ILogger<SemanticSearchService> _logger;

    private List<PromptDefinition> _allPrompts = new();

    /// <summary>True when the embedding index is built and semantic search is available.</summary>
    public bool IsReady => _vectorIndex.IsReady;

    /// <summary>True when embedding calls have been permanently disabled due to a deployment error.</summary>
    public bool EmbeddingsDisabled => _vectorIndex.EmbeddingsDisabled;

    public SemanticSearchService(
        IVectorIndex<PromptDefinition> vectorIndex,
        ILogger<SemanticSearchService> logger)
    {
        _vectorIndex = vectorIndex;
        _logger = logger;
    }

    /// <summary>Builds the embedding index from loaded prompt definitions.</summary>
    public async Task BuildIndexAsync(IEnumerable<PromptDefinition> prompts)
    {
        var promptList = prompts.ToList();
        _allPrompts = promptList;

        await _vectorIndex.BuildAsync(
            promptList,
            textExtractor: p => $"{p.Description}. {string.Join(". ", p.ContextTags)}",
            idExtractor: p => p.Id);
    }

    /// <summary>
    /// Returns the top matching prompts for a user message using semantic (cosine) similarity.
    /// Score > 0.75 → clear match; multiple in [0.65–0.75] → ambiguous.
    /// Returns empty list when embeddings are disabled — caller should use KeywordSearch instead.
    /// </summary>
    public async Task<List<(PromptDefinition Prompt, float Score)>> SearchAsync(
        string userMessage, string? domain = null, int topK = 3)
    {
        Func<PromptDefinition, bool>? filter = string.IsNullOrEmpty(domain)
            ? null
            : p => p.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase);

        var results = await _vectorIndex.SearchAsync(userMessage, topK, filter);
        return results.Select(r => (r.Item, r.Score)).ToList();
    }

    /// <summary>Direct lookup by prompt ID.</summary>
    public PromptDefinition? GetPromptById(string promptId)
        => _allPrompts.FirstOrDefault(p => p.Id.Equals(promptId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Keyword-based intent matching. Used when embedding deployment is unavailable.
    /// Scores using TF-IDF-style weighting.
    /// </summary>
    public List<(PromptDefinition Prompt, float Score)> KeywordSearch(
        string userMessage, string? domain = null, int topK = 3)
    {
        Func<PromptDefinition, bool>? filter = string.IsNullOrEmpty(domain)
            ? null
            : p => p.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase);

        var results = _vectorIndex.KeywordSearch(userMessage, topK, filter);
        return results.Select(r => (r.Item, r.Score)).ToList();
    }

    /// <summary>Returns cache health statistics.</summary>
    public async Task<Models.VectorCacheStats> GetCacheStatsAsync()
    {
        var stats = await _vectorIndex.GetStatsAsync();
        return new Models.VectorCacheStats
        {
            Ready = stats.Ready,
            EmbeddingsDisabled = stats.EmbeddingsDisabled,
            IndexSize = stats.IndexSize,
            CachedEmbeddings = stats.CachedEmbeddings,
            OrphanedEmbeddings = stats.OrphanedEmbeddings,
            PromptCount = stats.ItemCount
        };
    }

    /// <summary>Truncates the embedding cache.</summary>
    public async Task InvalidateCacheAsync()
    {
        // Rebuild with empty to trigger truncation
        await _vectorIndex.RebuildAsync(
            _allPrompts,
            textExtractor: p => $"{p.Description}. {string.Join(". ", p.ContextTags)}",
            idExtractor: p => p.Id);
    }

    /// <summary>Invalidates cache and rebuilds the index from scratch.</summary>
    public async Task<Models.VectorRebuildResult> RebuildIndexAsync(IEnumerable<PromptDefinition> prompts)
    {
        var promptList = prompts.ToList();
        _allPrompts = promptList;

        var result = await _vectorIndex.RebuildAsync(
            promptList,
            textExtractor: p => $"{p.Description}. {string.Join(". ", p.ContextTags)}",
            idExtractor: p => p.Id);

        return new Models.VectorRebuildResult
        {
            Status = result.Status,
            PreviousCount = result.PreviousCount,
            NewCount = result.NewCount,
            DurationMs = result.DurationMs
        };
    }
}
