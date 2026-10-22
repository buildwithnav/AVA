using System.Text;
using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Data;
using Ava.Api.Models;
using Ava.Vectors;
using Dapper;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Ava.Api.Services;

/// <summary>
/// Orchestrates domain context file operations: upload (chunk + FTS + embed),
/// hybrid search (embedding cosine + BM25 via RRF), manifest generation, and deletion.
/// </summary>
public class DomainFileService
{
    private readonly IDomainFileRepository _repo;
    private readonly KnowledgeBaseService _kbService;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly DbConnectionFactory _db;
    private readonly AvaSettings _settings;
    private readonly ILogger<DomainFileService> _logger;

    private const string CollectionPrefix = "domain-files-";

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public DomainFileService(
        IDomainFileRepository repo,
        KnowledgeBaseService kbService,
        IEmbeddingProvider embeddingProvider,
        DbConnectionFactory db,
        IOptions<AvaSettings> settings,
        ILogger<DomainFileService> logger)
    {
        _repo = repo;
        _kbService = kbService;
        _embeddingProvider = embeddingProvider;
        _db = db;
        _settings = settings.Value;
        _logger = logger;
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Upload a domain context file: proxy to ava-kb for chunking/FTS, embed chunks, save metadata.
    /// </summary>
    public async Task<DomainFile> UploadAsync(
        string domainId, Stream fileStream, string filename, long fileSize,
        string title, string description, string[] tags, string uploadedBy,
        int? chunkSize = null, int? chunkOverlap = null)
    {
        var fileId = $"df-{Guid.NewGuid():N}"[..20];
        var kbId = $"{CollectionPrefix}{domainId}";

        _logger.LogInformation("Uploading domain file: {FileId} ({Filename}) to domain {DomainId}",
            fileId, filename, domainId);

        // Use larger chunk size for domain files (better section preservation)
        var effectiveChunkSize = chunkSize ?? _settings.DomainFiles.ChunkSize;
        var effectiveChunkOverlap = chunkOverlap ?? _settings.DomainFiles.ChunkOverlap;

        // 1. Proxy to ava-kb for chunking + FTS indexing (append mode)
        var uploadResult = await _kbService.CallUploadAsync(
            kbId, "domain", sessionId: null, domainId: domainId,
            type: "document", fileStream, filename,
            appendMode: true, chunkSize: effectiveChunkSize, chunkOverlap: effectiveChunkOverlap);

        var chunkCount = 0;
        if (uploadResult?.RootElement.TryGetProperty("chunkCount", out var cc) == true)
            chunkCount = cc.GetInt32();

        // 2. Embed chunks in PostgreSQL for vector search + fallback retrieval.
        //    Prefer chunks directly from the upload response (no round-trip dependency).
        //    Falls back to fetching from ava-kb's chunks-by-source endpoint if response doesn't include them.
        var chunksFromResponse = ExtractChunksFromUploadResponse(uploadResult);
        if (chunksFromResponse.Count > 0)
        {
            await EmbedFileChunksDirectAsync(chunksFromResponse, filename, domainId);
        }
        else
        {
            await EmbedFileChunksAsync(kbId, filename, domainId);
        }

        // 3. Save metadata
        var domainFile = new DomainFile
        {
            Id = fileId,
            DomainId = domainId,
            Filename = filename,
            Title = title,
            Description = description,
            Tags = tags.ToList(),
            ContentType = DetectContentType(filename),
            FileSizeBytes = fileSize,
            ChunkCount = chunkCount,
            UploadedBy = uploadedBy,
            UploadedAt = DateTime.UtcNow,
            Enabled = true
        };

        await _repo.AddAsync(domainFile);
        _logger.LogInformation("Domain file uploaded: {FileId} with {ChunkCount} chunks", fileId, chunkCount);

        return domainFile;
    }

    /// <summary>
    /// Extract chunk data directly from the ava-kb upload response.
    /// Avoids a second round-trip to fetch chunks back from ava-kb.
    /// </summary>
    private static List<(string Id, string Content)> ExtractChunksFromUploadResponse(JsonDocument? uploadResult)
    {
        var chunks = new List<(string Id, string Content)>();
        if (uploadResult == null) return chunks;

        if (uploadResult.RootElement.TryGetProperty("chunks", out var chunksArray)
            && chunksArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var chunk in chunksArray.EnumerateArray())
            {
                var id = chunk.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var content = chunk.TryGetProperty("content", out var cProp) ? cProp.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(id))
                    chunks.Add((id, content));
            }
        }
        return chunks;
    }

    /// <summary>
    /// Embed pre-extracted chunks directly into PostgreSQL.
    /// Used when chunks are available from the upload response (no ava-kb round-trip needed).
    /// </summary>
    private async Task EmbedFileChunksDirectAsync(List<(string Id, string Content)> chunks, string filename, string domainId)
    {
        try
        {
            if (chunks.Count == 0) return;

            var collection = $"{CollectionPrefix}{domainId}";
            var texts = chunks.Select(c => c.Content).ToList();
            var embeddings = await _embeddingProvider.EmbedBatchAsync(texts);

            // Clean up stale PG data from previous uploads of this file
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "DELETE FROM domain_file_chunks WHERE collection = @Collection AND source = @Source",
                new { Collection = collection, Source = filename });
            await conn.ExecuteAsync(
                "DELETE FROM vector_embeddings WHERE collection = @Collection AND item_id LIKE @Pattern",
                new { Collection = collection, Pattern = $"{filename}#%" });

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunkId = chunks[i].Id;
                var contentHash = ComputeHash(chunks[i].Content);
                var vector = new Vector(embeddings[i]);

                await conn.ExecuteAsync(@"
                    INSERT INTO vector_embeddings (collection, item_id, content_hash, embedding)
                    VALUES (@Collection, @ItemId, @ContentHash, @Embedding::vector)
                    ON CONFLICT (collection, item_id, content_hash)
                    DO UPDATE SET embedding = EXCLUDED.embedding",
                    new { Collection = collection, ItemId = chunkId, ContentHash = contentHash, Embedding = vector });

                await conn.ExecuteAsync(@"
                    INSERT INTO domain_file_chunks (collection, chunk_id, source, title, content)
                    VALUES (@Collection, @ChunkId, @Source, @Title, @Content)
                    ON CONFLICT (collection, chunk_id)
                    DO UPDATE SET content = EXCLUDED.content, source = EXCLUDED.source, title = EXCLUDED.title",
                    new { Collection = collection, ChunkId = chunkId, Source = filename, Title = filename, Content = chunks[i].Content });
            }

            _logger.LogInformation("EmbedFileChunksDirect: stored {Count} chunks for {Filename} in collection {Collection}",
                chunks.Count, filename, collection);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EmbedFileChunksDirect failed for {Filename} — PG fallback will not be available", filename);
        }
    }

    /// <summary>
    /// Embed file chunks for a specific source file and store in pgvector.
    /// Fetches chunks directly by source (not via search) for reliability.
    /// </summary>
    private async Task EmbedFileChunksAsync(string kbId, string filename, string domainId)
    {
        try
        {
            // Fetch all chunks for this source file directly (no BM25 matching needed)
            var chunksResult = await _kbService.CallGetChunksBySourceAsync(kbId, filename);
            if (chunksResult == null) return;

            var chunks = new List<(string Id, string Content)>();
            if (chunksResult.RootElement.TryGetProperty("chunks", out var chunksArray))
            {
                foreach (var chunk in chunksArray.EnumerateArray())
                {
                    var content = chunk.GetProperty("content").GetString() ?? "";
                    var id = chunk.GetProperty("id").GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        chunks.Add((id, content));
                    }
                }
            }

            if (chunks.Count == 0)
            {
                _logger.LogWarning("No chunks found to embed for file {Filename} in KB {KbId}", filename, kbId);
                return;
            }

            // Embed in batches
            var collection = $"{CollectionPrefix}{domainId}";
            var texts = chunks.Select(c => c.Content).ToList();
            var embeddings = await _embeddingProvider.EmbedBatchAsync(texts);

            // Clean up stale PG data from previous uploads of this file before re-inserting
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "DELETE FROM domain_file_chunks WHERE collection = @Collection AND source = @Source",
                new { Collection = collection, Source = filename });
            await conn.ExecuteAsync(
                "DELETE FROM vector_embeddings WHERE collection = @Collection AND item_id LIKE @Pattern",
                new { Collection = collection, Pattern = $"{filename}#%" });

            // Store in pgvector + chunk content table
            for (int i = 0; i < chunks.Count; i++)
            {
                // Use original chunk ID from ava-kb to maintain consistency between
                // DuckDB (FTS) and PostgreSQL (embedding + fallback retrieval)
                var chunkId = chunks[i].Id;
                var contentHash = ComputeHash(chunks[i].Content);
                var vector = new Vector(embeddings[i]);

                await conn.ExecuteAsync(@"
                    INSERT INTO vector_embeddings (collection, item_id, content_hash, embedding)
                    VALUES (@Collection, @ItemId, @ContentHash, @Embedding::vector)
                    ON CONFLICT (collection, item_id, content_hash)
                    DO UPDATE SET embedding = EXCLUDED.embedding",
                    new { Collection = collection, ItemId = chunkId, ContentHash = contentHash, Embedding = vector });

                // Store chunk content for retrieval during embedding search
                await conn.ExecuteAsync(@"
                    INSERT INTO domain_file_chunks (collection, chunk_id, source, title, content)
                    VALUES (@Collection, @ChunkId, @Source, @Title, @Content)
                    ON CONFLICT (collection, chunk_id)
                    DO UPDATE SET content = EXCLUDED.content, source = EXCLUDED.source, title = EXCLUDED.title",
                    new { Collection = collection, ChunkId = chunkId, Source = filename, Title = filename, Content = chunks[i].Content });
            }

            _logger.LogInformation("Embedded {Count} chunks for file {Filename} in collection {Collection}",
                chunks.Count, filename, collection);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed chunks for file {Filename} — search will use BM25 only", filename);
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    /// <summary>Delete a domain file: remove metadata, FTS chunks, and embeddings.</summary>
    public async Task DeleteAsync(string fileId)
    {
        var file = await _repo.GetByIdAsync(fileId);
        if (file == null) return;

        var kbId = $"{CollectionPrefix}{file.DomainId}";
        var collection = $"{CollectionPrefix}{file.DomainId}";

        // Delete FTS chunks from ava-kb
        try
        {
            // Call the delete-by-source endpoint on ava-kb
            using var http = new HttpClient();
            http.BaseAddress = new Uri(_settings.KnowledgeBase.KBServiceBaseUrl);
            http.DefaultRequestHeaders.Add("X-KB-Api-Key", _settings.KnowledgeBase.ApiKey);
            await http.DeleteAsync($"kb/source?kbId={kbId}&source={Uri.EscapeDataString(file.Filename)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete FTS chunks for file {FileId}", fileId);
        }

        // Delete embeddings and chunk content from pgvector
        try
        {
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "DELETE FROM vector_embeddings WHERE collection = @Collection AND item_id LIKE @Pattern",
                new { Collection = collection, Pattern = $"{file.Filename}#%" });
            await conn.ExecuteAsync(
                "DELETE FROM domain_file_chunks WHERE collection = @Collection AND chunk_id LIKE @Pattern",
                new { Collection = collection, Pattern = $"{file.Filename}#%" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete embeddings for file {FileId}", fileId);
        }

        // Remove metadata
        await _repo.RemoveAsync(fileId);
        _logger.LogInformation("Domain file deleted: {FileId} ({Filename})", fileId, file.Filename);
    }

    // ── List & Update ─────────────────────────────────────────────────────────

    public Task<List<DomainFile>> GetByDomainAsync(string domainId)
        => _repo.GetByDomainAsync(domainId);

    public Task<DomainFile?> GetByIdAsync(string id)
        => _repo.GetByIdAsync(id);

    public async Task<DomainFile?> UpdateMetadataAsync(string fileId, string title, string description, string[] tags)
    {
        var file = await _repo.GetByIdAsync(fileId);
        if (file == null) return null;

        file.Title = title;
        file.Description = description;
        file.Tags = tags.ToList();
        await _repo.UpdateAsync(file);
        return file;
    }

    // ── Manifest ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a compact manifest string for the system prompt listing available domain files.
    /// </summary>
    public async Task<string> BuildManifestAsync(string domainId)
    {
        var files = await _repo.GetByDomainAsync(domainId);
        _logger.LogInformation("BuildManifest: domain={DomainId} files={FileCount}", domainId, files.Count);
        if (files.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Domain reference files available:");
        sb.AppendLine("  Use the search_docs tool to find specific information in these documents.");
        foreach (var f in files)
        {
            var tags = f.Tags.Count > 0 ? $" [{string.Join(", ", f.Tags)}]" : "";
            sb.AppendLine($"  • \"{f.Title}\" ({f.ContentType}) — {f.Description}{tags}");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    // ── Strategy-Based Retrieval ──────────────────────────────────────────────

    /// <summary>
    /// Execute fast retrieval: single BM25+embedding search, expand to parent sections.
    /// Best for simple factual questions.
    /// Includes adjacent section expansion for sequential queries ("after", "next", "before").
    /// </summary>
    /// <param name="domainId">Domain ID</param>
    /// <param name="query">The search query (may be rewritten for BM25)</param>
    /// <param name="originalMessage">Original user message for detecting sequential intent</param>
    public async Task<List<DomainFileChunk>> ExecuteFastRetrievalAsync(string domainId, string query, string? originalMessage = null)
    {
        var topK = _settings.DomainFiles.Retrieval.FastTopK;
        var kbId = $"{CollectionPrefix}{domainId}";

        _logger.LogInformation("FastRetrieval: domain={DomainId} query=\"{Query}\" topK={TopK}",
            domainId, query[..Math.Min(80, query.Length)], topK);

        List<DomainFileChunk> chunks;

        // Use parent-expanded search from ava-kb
        try
        {
            var result = await _kbService.CallSearchExpandedAsync(kbId, query, topK);
            if (result != null)
            {
                chunks = ParseSearchExpandedResults(result);
                if (chunks.Count > 0)
                {
                    _logger.LogInformation("FastRetrieval: returning {Count} parent-expanded results", chunks.Count);
                }
                else
                {
                    chunks = await HybridSearchAsync(domainId, query, topK);
                }
            }
            else
            {
                chunks = await HybridSearchAsync(domainId, query, topK);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FastRetrieval: parent-expanded search failed, falling back to hybrid");
            chunks = await HybridSearchAsync(domainId, query, topK);
        }

        // Source-scoped secondary search: when the user mentions a specific file,
        // do an additional BM25 search scoped to that file with higher top_k to
        // capture ALL matching rows (bypasses global BM25 length-normalization bias).
        try
        {
            var boostedSources = await DetectMentionedSourcesAsync(domainId, originalMessage ?? query);
            if (boostedSources.Count > 0)
            {
                _logger.LogInformation("FastRetrieval: source-scoped search for {Sources}", string.Join(", ", boostedSources));
                var existingIds = new HashSet<string>(chunks.Select(c => c.Id));

                foreach (var source in boostedSources)
                {
                    var sourceResult = await _kbService.CallSearchExpandedAsync(kbId, query, topK: 20, source: source);
                    if (sourceResult != null)
                    {
                        var sourceChunks = ParseSearchExpandedResults(sourceResult);
                        foreach (var sc in sourceChunks)
                        {
                            if (!existingIds.Contains(sc.Id))
                            {
                                chunks.Add(sc);
                                existingIds.Add(sc.Id);
                            }
                        }
                        _logger.LogInformation("FastRetrieval: source-scoped added {Count} new chunks from {Source}",
                            sourceChunks.Count(c => !existingIds.Contains(c.Id) == false), source);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FastRetrieval: source-scoped search failed (non-fatal)");
        }

        // Sequential expansion: if the ORIGINAL user message asks about "after" or "next" or "before",
        // include the adjacent parent section for the top result
        var sequentialCheckText = originalMessage ?? query;
        if (chunks.Count > 0 && IsSequentialQuery(sequentialCheckText))
        {
            await ExpandWithAdjacentParent(kbId, chunks, sequentialCheckText);
        }

        return chunks;
    }

    private static readonly string[] _sequentialSignals = ["after", "next", "following", "before", "previous", "preceding", "comes after", "comes before"];

    private static bool IsSequentialQuery(string query)
    {
        var lower = query.ToLower();
        return _sequentialSignals.Any(s => lower.Contains(s));
    }

    private async Task ExpandWithAdjacentParent(string kbId, List<DomainFileChunk> chunks, string query)
    {
        // Find the top chunk with a parent_id that we can derive an adjacent parent from
        var topChunk = chunks.FirstOrDefault(c => !string.IsNullOrEmpty(c.ParentId));
        if (topChunk == null) return;

        // Parse parent number: "source#parent-47" → 47
        var parentId = topChunk.ParentId!;
        var match = System.Text.RegularExpressions.Regex.Match(parentId, @"#parent-(\d+)$");
        if (!match.Success) return;

        var parentNum = int.Parse(match.Groups[1].Value);
        var lower = query.ToLower();

        // Determine direction: "after"/"next" = +1, "before"/"previous" = -1
        int offset = lower.Contains("before") || lower.Contains("previous") || lower.Contains("preceding") ? -1 : 1;
        var adjacentParentId = parentId[..parentId.LastIndexOf('-')] + $"-{parentNum + offset}";

        try
        {
            var parentDoc = await _kbService.CallGetParentAsync(kbId, adjacentParentId);
            if (parentDoc != null)
            {
                var root = parentDoc.RootElement;
                var content = root.TryGetProperty("content", out var c) ? c.GetString() : null;
                var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
                var source = root.TryGetProperty("source", out var s) ? s.GetString() : topChunk.Source;

                if (!string.IsNullOrEmpty(content))
                {
                    chunks.Add(new DomainFileChunk
                    {
                        ChunkId = adjacentParentId,
                        ParentId = adjacentParentId,
                        Source = source ?? topChunk.Source,
                        Title = title ?? "Adjacent Section",
                        Content = content,
                        Score = topChunk.Score * 0.9f // Slightly lower priority
                    });
                    _logger.LogInformation("FastRetrieval: added adjacent parent {ParentId} for sequential query", adjacentParentId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FastRetrieval: could not fetch adjacent parent {ParentId}", adjacentParentId);
        }
    }

    /// <summary>
    /// Execute expanded retrieval: decompose query into sub-queries, retrieve many results,
    /// deduplicate by parent, return top-K parent sections.
    /// Best for broad overview questions.
    /// </summary>
    public async Task<List<DomainFileChunk>> ExecuteExpandedRetrievalAsync(string domainId, string query, List<string>? subQueries = null)
    {
        var retrieval = _settings.DomainFiles.Retrieval;
        var kbId = $"{CollectionPrefix}{domainId}";

        // Use rewritten sub-queries when available (they're optimized for BM25).
        // Fall back to original query only when no rewrites exist.
        var queries = new List<string>();
        if (subQueries != null && subQueries.Count > 0)
        {
            queries.AddRange(subQueries);
        }
        else
        {
            queries.Add(query);
        }

        _logger.LogInformation("ExpandedRetrieval: domain={DomainId} queries={QueryCount} retrieveK={RetrieveK}",
            domainId, queries.Count, retrieval.ExpandedRetrieveK);

        // Use Reciprocal Rank Fusion (RRF) to merge results across queries.
        // BM25 scores are NOT comparable across queries of different lengths,
        // so rank-based merging produces better results than score-based sorting.
        const float rrfK = 60f;
        var rrfScores = new Dictionary<string, (float Score, DomainFileChunk Chunk)>();

        foreach (var q in queries)
        {
            try
            {
                var result = await _kbService.CallSearchExpandedAsync(kbId, q, retrieval.ExpandedRetrieveK / queries.Count);
                if (result != null)
                {
                    var chunks = ParseSearchExpandedResults(result);
                    for (int rank = 0; rank < chunks.Count; rank++)
                    {
                        var chunk = chunks[rank];
                        var key = chunk.ParentId ?? chunk.ChunkId;
                        var contribution = 1f / (rrfK + rank + 1);

                        if (rrfScores.TryGetValue(key, out var existing))
                        {
                            // Accumulate RRF score; keep the chunk with more content
                            var bestChunk = (chunk.Content?.Length ?? 0) > (existing.Chunk.Content?.Length ?? 0) ? chunk : existing.Chunk;
                            rrfScores[key] = (existing.Score + contribution, bestChunk);
                        }
                        else
                        {
                            rrfScores[key] = (contribution, chunk);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ExpandedRetrieval: sub-query '{Query}' failed", q);
            }
        }

        var results = rrfScores.Values
            .OrderByDescending(v => v.Score)
            .Take(retrieval.ExpandedReturnK)
            .Select(v => v.Chunk)
            .ToList();

        _logger.LogInformation("ExpandedRetrieval: returning {Count} deduplicated parent sections (RRF merged)", results.Count);
        return results;
    }

    /// <summary>
    /// Get the search_docs tool definition for agentic retrieval mode.
    /// The agent loop will call this tool iteratively.
    /// </summary>
    public SearchDocsToolDefinition GetSearchDocsTool(string domainId)
    {
        return new SearchDocsToolDefinition
        {
            DomainId = domainId,
            MaxIterations = _settings.DomainFiles.Retrieval.AgenticMaxIterations,
            TokenBudget = _settings.DomainFiles.Retrieval.AgenticTokenBudget
        };
    }

    /// <summary>
    /// Execute a single search_docs tool call (used in agentic mode).
    /// </summary>
    public async Task<List<DomainFileChunk>> ExecuteSearchDocsAsync(string domainId, string query, int topK = 10)
    {
        return await ExecuteFastRetrievalAsync(domainId, query);
    }

    private List<DomainFileChunk> ParseSearchExpandedResults(JsonDocument doc)
    {
        var results = new List<DomainFileChunk>();
        if (!doc.RootElement.TryGetProperty("chunks", out var chunksArray))
            return results;

        foreach (var chunk in chunksArray.EnumerateArray())
        {
            var chunkId = chunk.GetProperty("id").GetString() ?? "";
            var content = chunk.GetProperty("content").GetString() ?? "";
            var source = chunk.GetProperty("source").GetString() ?? "";
            var title = chunk.GetProperty("title").GetString() ?? "";
            var score = chunk.TryGetProperty("score", out var s) ? s.GetDouble() : 0.0;
            var parentId = chunk.TryGetProperty("parentId", out var pid) ? pid.GetString() : null;
            var parentContent = chunk.TryGetProperty("parentContent", out var pc) ? pc.GetString() : null;

            // Use parent content if available (provides full section context)
            var displayContent = !string.IsNullOrEmpty(parentContent) ? parentContent : content;

            results.Add(new DomainFileChunk
            {
                ChunkId = chunkId,
                Source = source,
                Title = title,
                Content = displayContent,
                Score = (float)score,
                ParentId = parentId
            });
        }

        return results;
    }

    // ── Hybrid Search ─────────────────────────────────────────────────────────

    /// <summary>
    /// Hybrid search combining embedding cosine similarity + BM25 keyword search,
    /// merged via Reciprocal Rank Fusion (RRF).
    /// Applies source file boosting when the query explicitly mentions a file name.
    /// Includes adjacent chunks for continuity when a chunk boundary splits related content.
    /// </summary>
    public async Task<List<DomainFileChunk>> HybridSearchAsync(string domainId, string query, int topK = 5, float? threshold = null)
    {
        var effectiveThreshold = threshold ?? _settings.DomainFiles.SimilarityThreshold;
        var kbId = $"{CollectionPrefix}{domainId}";
        var collection = $"{CollectionPrefix}{domainId}";

        _logger.LogInformation("HybridSearch: domain={DomainId} kbId={KbId} query=\"{Query}\" topK={TopK} threshold={Threshold}",
            domainId, kbId, query[..Math.Min(80, query.Length)], topK, effectiveThreshold);

        // Determine if the query mentions a specific file (for source boosting)
        var boostedSources = await DetectMentionedSourcesAsync(domainId, query);
        if (boostedSources.Count > 0)
            _logger.LogInformation("HybridSearch: source boost applied for: {Sources}", string.Join(", ", boostedSources));

        // Run both searches in parallel
        var embeddingTask = EmbeddingSearchAsync(collection, query, topK * 2);
        var bm25Task = BM25SearchAsync(kbId, query, topK * 2);

        await Task.WhenAll(embeddingTask, bm25Task);

        var embeddingResults = embeddingTask.Result;
        var bm25Results = bm25Task.Result;

        _logger.LogInformation("HybridSearch results: embedding={EmbeddingCount} bm25={Bm25Count}",
            embeddingResults.Count, bm25Results.Count);

        if (embeddingResults.Count == 0 && bm25Results.Count == 0)
        {
            // Both sources returned nothing — try direct PG text search as last resort
            _logger.LogWarning("HybridSearch: both embedding and BM25 returned empty — trying PostgreSQL text fallback");
            return await PostgresTextFallbackAsync(collection, query, topK);
        }

        // Reciprocal Rank Fusion
        const float k = 60f;
        var rrfScores = new Dictionary<string, (float Score, string Content, string Source, string Title)>();

        for (int i = 0; i < embeddingResults.Count; i++)
        {
            var r = embeddingResults[i];
            if (r.Score < effectiveThreshold) continue;
            rrfScores[r.Id] = (1f / (k + i + 1), r.Content, r.Source, r.Title);
        }

        for (int i = 0; i < bm25Results.Count; i++)
        {
            var r = bm25Results[i];
            if (rrfScores.ContainsKey(r.Id))
                rrfScores[r.Id] = (rrfScores[r.Id].Score + 1f / (k + i + 1), r.Content, r.Source, r.Title);
            else
                rrfScores[r.Id] = (1f / (k + i + 1), r.Content, r.Source, r.Title);
        }

        // Safety net: if threshold filtered ALL embedding results and BM25 also empty,
        // relax threshold and include top embedding results rather than returning nothing
        if (rrfScores.Count == 0 && embeddingResults.Count > 0)
        {
            _logger.LogWarning("HybridSearch: threshold {Threshold} filtered all {Count} embedding results — relaxing to include top results",
                effectiveThreshold, embeddingResults.Count);
            for (int i = 0; i < Math.Min(topK, embeddingResults.Count); i++)
            {
                var r = embeddingResults[i];
                rrfScores[r.Id] = (1f / (k + i + 1), r.Content, r.Source, r.Title);
            }
        }

        // Apply source file boost: when user explicitly mentions a document,
        // chunks from that file get a 3x score multiplier so they rank above
        // incidental keyword matches from other files.
        if (boostedSources.Count > 0)
        {
            var boosted = new Dictionary<string, (float Score, string Content, string Source, string Title)>();
            foreach (var kvp in rrfScores)
            {
                var score = kvp.Value.Score;
                if (boostedSources.Any(bs => kvp.Value.Source.Equals(bs, StringComparison.OrdinalIgnoreCase)))
                    score *= 3.0f;
                boosted[kvp.Key] = (score, kvp.Value.Content, kvp.Value.Source, kvp.Value.Title);
            }
            rrfScores = boosted;
        }

        var topResults = rrfScores
            .OrderByDescending(x => x.Value.Score)
            .Take(topK)
            .Select(x => new DomainFileChunk
            {
                Id = x.Key,
                Content = x.Value.Content,
                Source = x.Value.Source,
                Title = x.Value.Title,
                Score = x.Value.Score
            })
            .ToList();

        // Expand with adjacent chunks for continuity (chunk N-1 and N+1)
        var expanded = await ExpandWithAdjacentChunksAsync(collection, topResults);
        return expanded;
    }

    /// <summary>
    /// For each retrieved chunk, fetch adjacent chunks (N-1 and N+1) to ensure
    /// content that spans chunk boundaries is fully captured.
    /// N-1 provides section headings/context; N+1 provides continuation content.
    /// Chunk IDs follow the pattern "filename#chunk-N".
    /// </summary>
    private async Task<List<DomainFileChunk>> ExpandWithAdjacentChunksAsync(string collection, List<DomainFileChunk> results)
    {
        if (results.Count == 0) return results;

        var existingIds = new HashSet<string>(results.Select(r => r.Id));
        var adjacentIds = new List<string>();

        foreach (var chunk in results)
        {
            var match = System.Text.RegularExpressions.Regex.Match(chunk.Id, @"^(.+)#chunk-(\d+)$");
            if (!match.Success) continue;

            var filePrefix = match.Groups[1].Value;
            var chunkNum = int.Parse(match.Groups[2].Value);

            // Include the previous chunk (section heading / context)
            if (chunkNum > 0)
            {
                var prevId = $"{filePrefix}#chunk-{chunkNum - 1}";
                if (!existingIds.Contains(prevId))
                {
                    adjacentIds.Add(prevId);
                    existingIds.Add(prevId);
                }
            }

            // Include the next chunk (content continues forward)
            var nextId = $"{filePrefix}#chunk-{chunkNum + 1}";
            if (!existingIds.Contains(nextId))
            {
                adjacentIds.Add(nextId);
                existingIds.Add(nextId);
            }
        }

        if (adjacentIds.Count == 0) return results;

        // Fetch adjacent chunks from the database
        try
        {
            await using var conn = await _db.OpenConnectionAsync();
            var adjacentChunks = await conn.QueryAsync<AdjacentChunkRow>(@"
                SELECT chunk_id, source, title, content
                FROM domain_file_chunks
                WHERE collection = @Collection AND chunk_id = ANY(@Ids)",
                new { Collection = collection, Ids = adjacentIds.ToArray() });

            foreach (var adj in adjacentChunks)
            {
                results.Add(new DomainFileChunk
                {
                    Id = adj.chunk_id,
                    Content = adj.content ?? "",
                    Source = adj.source ?? "",
                    Title = adj.title ?? "",
                    Score = 0f // Adjacent chunks don't have their own relevance score
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch adjacent chunks — returning primary results only");
        }

        // Sort by source then chunk number to maintain document order
        return results
            .OrderBy(r => r.Source)
            .ThenBy(r =>
            {
                var m = System.Text.RegularExpressions.Regex.Match(r.Id, @"#chunk-(\d+)$");
                return m.Success ? int.Parse(m.Groups[1].Value) : 0;
            })
            .ToList();
    }

    /// <summary>
    /// Section-aware lookup: delegates to ava-kb's section-lookup endpoint which searches
    /// DuckDB (the source of truth for chunks) for headings matching the query,
    /// then returns the full section (heading chunk + subsequent chunks until next heading).
    /// Falls back to PostgreSQL domain_file_chunks if ava-kb call fails.
    /// </summary>
    public async Task<List<DomainFileChunk>> SectionLookupAsync(string domainId, string sectionQuery, string? fileName = null)
    {
        var kbId = $"{CollectionPrefix}{domainId}";

        _logger.LogInformation("SectionLookup: domain={DomainId} query=\"{Query}\" file={File}",
            domainId, sectionQuery, fileName ?? "(all)");

        try
        {
            // Primary path: call ava-kb's DuckDB-backed section lookup
            var result = await _kbService.CallSectionLookupAsync(kbId, sectionQuery, fileName);
            if (result != null && result.RootElement.TryGetProperty("chunks", out var chunksArray))
            {
                var chunks = new List<DomainFileChunk>();
                foreach (var chunk in chunksArray.EnumerateArray())
                {
                    chunks.Add(new DomainFileChunk
                    {
                        Id = chunk.GetProperty("id").GetString() ?? "",
                        Content = chunk.GetProperty("content").GetString() ?? "",
                        Source = chunk.GetProperty("source").GetString() ?? "",
                        Title = chunk.GetProperty("title").GetString() ?? "",
                        Score = chunk.TryGetProperty("score", out var s) ? (float)s.GetDouble() : 1.0f
                    });
                }

                if (chunks.Count > 0)
                {
                    _logger.LogInformation("SectionLookup via ava-kb: returning {Count} chunks", chunks.Count);
                    return chunks;
                }
            }

            _logger.LogInformation("SectionLookup: ava-kb returned no results, trying PostgreSQL fallback");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SectionLookup: ava-kb call failed, trying PostgreSQL fallback");
        }

        // Fallback: query PostgreSQL domain_file_chunks (populated during EmbedFileChunksAsync)
        try
        {
            var collection = $"{CollectionPrefix}{domainId}";
            await using var conn = await _db.OpenConnectionAsync();

            var searchPattern = $"%{sectionQuery.Replace("'", "''")}%";
            var sql = @"
                SELECT chunk_id, source, title, content
                FROM domain_file_chunks
                WHERE collection = @Collection
                AND LEFT(content, 150) ILIKE @Pattern";

            if (!string.IsNullOrEmpty(fileName))
                sql += " AND source = @FileName";

            sql += " ORDER BY chunk_id LIMIT 5";

            var headingChunks = (await conn.QueryAsync<AdjacentChunkRow>(sql,
                new { Collection = collection, Pattern = searchPattern, FileName = fileName })).ToList();

            if (headingChunks.Count == 0)
            {
                // Fallback: search full content
                sql = @"
                    SELECT chunk_id, source, title, content
                    FROM domain_file_chunks
                    WHERE collection = @Collection
                    AND content ILIKE @Pattern";
                if (!string.IsNullOrEmpty(fileName))
                    sql += " AND source = @FileName";
                sql += " ORDER BY chunk_id LIMIT 5";

                headingChunks = (await conn.QueryAsync<AdjacentChunkRow>(sql,
                    new { Collection = collection, Pattern = searchPattern, FileName = fileName })).ToList();
            }

            if (headingChunks.Count == 0)
                return new();

            // Follow chunk IDs forward for section content
            var results = new List<DomainFileChunk>();
            var seenIds = new HashSet<string>();

            foreach (var heading in headingChunks)
            {
                if (seenIds.Contains(heading.chunk_id)) continue;
                seenIds.Add(heading.chunk_id);

                results.Add(new DomainFileChunk
                {
                    Id = heading.chunk_id,
                    Content = heading.content ?? "",
                    Source = heading.source ?? "",
                    Title = heading.title ?? "",
                    Score = 1.0f
                });

                var match = System.Text.RegularExpressions.Regex.Match(heading.chunk_id, @"^(.+)#chunk-(\d+)$");
                if (!match.Success) continue;

                var filePrefix = match.Groups[1].Value;
                var startIdx = int.Parse(match.Groups[2].Value);

                var nextIds = Enumerable.Range(startIdx + 1, 5)
                    .Select(i => $"{filePrefix}#chunk-{i}")
                    .Where(id => !seenIds.Contains(id))
                    .ToList();

                if (nextIds.Count == 0) continue;

                var nextChunks = await conn.QueryAsync<AdjacentChunkRow>(@"
                    SELECT chunk_id, source, title, content
                    FROM domain_file_chunks
                    WHERE collection = @Collection AND chunk_id = ANY(@Ids)
                    ORDER BY chunk_id",
                    new { Collection = collection, Ids = nextIds.ToArray() });

                foreach (var next in nextChunks)
                {
                    var firstLine = (next.content ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
                    if (firstLine.Length < 80 &&
                        System.Text.RegularExpressions.Regex.IsMatch(firstLine, @"^(#{1,6}\s|\d+(\.\d+)?\s+\S)"))
                    {
                        break;
                    }

                    seenIds.Add(next.chunk_id);
                    results.Add(new DomainFileChunk
                    {
                        Id = next.chunk_id,
                        Content = next.content ?? "",
                        Source = next.source ?? "",
                        Title = next.title ?? "",
                        Score = 0.9f
                    });
                }
            }

            _logger.LogInformation("SectionLookup (PostgreSQL fallback): returning {Count} chunks", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SectionLookup PostgreSQL fallback also failed — returning empty");
            return new();
        }
    }

    /// <summary>
    /// Get adjacent chunks (before/after a given chunk ID) for sequential navigation.
    /// Falls back to PostgreSQL if ava-kb returns no results.
    /// </summary>
    public async Task<List<DomainFileChunk>> GetAdjacentChunksAsync(string domainId, string chunkId, int before = 0, int after = 3)
    {
        var kbId = $"{CollectionPrefix}{domainId}";
        var collection = $"{CollectionPrefix}{domainId}";
        _logger.LogInformation("GetAdjacentChunks: domain={DomainId} chunkId={ChunkId} before={Before} after={After}",
            domainId, chunkId, before, after);

        // Primary path: ava-kb DuckDB
        try
        {
            var response = await _kbService.CallGetAdjacentChunksAsync(kbId, chunkId, before, after);
            if (response != null && response.RootElement.TryGetProperty("chunks", out var chunksArray))
            {
                var results = new List<DomainFileChunk>();
                foreach (var chunk in chunksArray.EnumerateArray())
                {
                    results.Add(new DomainFileChunk
                    {
                        Id = chunk.GetProperty("id").GetString() ?? "",
                        Content = chunk.GetProperty("content").GetString() ?? "",
                        Source = chunk.GetProperty("source").GetString() ?? "",
                        Title = chunk.GetProperty("title").GetString() ?? "",
                        Score = 0.9f
                    });
                }
                if (results.Count > 0)
                {
                    _logger.LogInformation("GetAdjacentChunks via ava-kb: returning {Count} chunks", results.Count);
                    return results;
                }
            }
            _logger.LogInformation("GetAdjacentChunks: ava-kb returned no results, trying PostgreSQL fallback");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetAdjacentChunks: ava-kb call failed, trying PostgreSQL fallback");
        }

        // Fallback: query PostgreSQL domain_file_chunks directly
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(chunkId, @"^(.+)#chunk-(\d+)$");
            if (!match.Success)
            {
                _logger.LogWarning("GetAdjacentChunks: chunk ID '{ChunkId}' does not match expected format", chunkId);
                return new();
            }

            var filePrefix = match.Groups[1].Value;
            var currentIdx = int.Parse(match.Groups[2].Value);
            var startIdx = Math.Max(0, currentIdx - before);
            var endIdx = currentIdx + after;

            var targetIds = Enumerable.Range(startIdx, endIdx - startIdx + 1)
                .Where(i => i != currentIdx)
                .Select(i => $"{filePrefix}#chunk-{i}")
                .ToList();

            if (targetIds.Count == 0)
                return new();

            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<AdjacentChunkRow>(@"
                SELECT chunk_id, source, title, content
                FROM domain_file_chunks
                WHERE collection = @Collection AND chunk_id = ANY(@Ids)
                ORDER BY chunk_id",
                new { Collection = collection, Ids = targetIds.ToArray() });

            var pgResults = rows.Select(r => new DomainFileChunk
            {
                Id = r.chunk_id,
                Content = r.content ?? "",
                Source = r.source ?? "",
                Title = r.title ?? "",
                Score = 0.9f
            }).ToList();

            _logger.LogInformation("GetAdjacentChunks (PostgreSQL fallback): returning {Count} chunks", pgResults.Count);
            return pgResults;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetAdjacentChunks PostgreSQL fallback also failed — returning empty");
            return new();
        }
    }

    /// <summary>Cosine similarity search against pgvector embeddings.</summary>
    private async Task<List<DomainFileChunk>> EmbeddingSearchAsync(string collection, string query, int topK)
    {
        try
        {
            var queryEmbedding = await _embeddingProvider.EmbedAsync(query);
            var queryVector = new Vector(queryEmbedding);

            await using var conn = await _db.OpenConnectionAsync();
            var results = await conn.QueryAsync<EmbeddingSearchRow>(@"
                SELECT ve.item_id, ve.content_hash,
                       1 - (ve.embedding <=> @QueryVector::vector) as score,
                       COALESCE(dfc.content, '') as content,
                       COALESCE(dfc.source, '') as source,
                       COALESCE(dfc.title, '') as title
                FROM vector_embeddings ve
                LEFT JOIN domain_file_chunks dfc ON dfc.collection = ve.collection AND dfc.chunk_id = ve.item_id
                WHERE ve.collection = @Collection
                ORDER BY ve.embedding <=> @QueryVector::vector
                LIMIT @TopK",
                new { Collection = collection, QueryVector = queryVector, TopK = topK });

            return results.Select(r => new DomainFileChunk
            {
                Id = r.item_id,
                Content = r.content ?? "",
                Source = r.source ?? (r.item_id.Contains('#') ? r.item_id.Split('#')[0] : r.item_id),
                Title = r.title ?? (r.item_id.Contains('#') ? r.item_id.Split('#')[0] : r.item_id),
                Score = (float)r.score
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding search failed for collection {Collection} — falling back to BM25 only", collection);
            return new();
        }
    }

    /// <summary>BM25 keyword search via ava-kb.</summary>
    private async Task<List<DomainFileChunk>> BM25SearchAsync(string kbId, string query, int topK)
    {
        try
        {
            var result = await _kbService.CallSearchAsync(kbId, query, topK);
            if (result == null) return new();

            var chunks = new List<DomainFileChunk>();
            if (result.RootElement.TryGetProperty("chunks", out var chunksArray))
            {
                int idx = 0;
                foreach (var chunk in chunksArray.EnumerateArray())
                {
                    // Use chunk id (e.g. "filename#chunk-0") as unique identifier for RRF merge
                    var chunkId = chunk.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    var source = chunk.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(chunkId)) chunkId = $"{source}#bm25-{idx}";

                    chunks.Add(new DomainFileChunk
                    {
                        Id = chunkId,
                        Content = chunk.TryGetProperty("content", out var cnt) ? cnt.GetString() ?? "" : "",
                        Source = source,
                        Title = chunk.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Score = chunk.TryGetProperty("score", out var sc) ? (float)sc.GetDouble() : 0f
                    });
                    idx++;
                }
            }

            return chunks;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BM25 search failed for KB {KbId}", kbId);
            return new();
        }
    }

    /// <summary>
    /// Last-resort PostgreSQL text search when both BM25 and embedding search return empty.
    /// Searches domain_file_chunks content directly using ILIKE for keyword matches.
    /// </summary>
    private async Task<List<DomainFileChunk>> PostgresTextFallbackAsync(string collection, string query, int topK)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync();

            // Extract meaningful keywords (skip short words)
            var keywords = query.Split(new[] { ' ', ',', '.', '?', '!', '"', '\'' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2)
                .Take(5)
                .ToList();

            if (keywords.Count == 0) return new();

            // Build ILIKE conditions — match chunks containing ANY keyword
            var conditions = string.Join(" OR ", keywords.Select((_, i) => $"content ILIKE @Kw{i}"));
            var sql = $@"
                SELECT chunk_id, source, title, content
                FROM domain_file_chunks
                WHERE collection = @Collection AND ({conditions})
                LIMIT @TopK";

            var parameters = new Dapper.DynamicParameters();
            parameters.Add("Collection", collection);
            parameters.Add("TopK", topK);
            for (int i = 0; i < keywords.Count; i++)
                parameters.Add($"Kw{i}", $"%{keywords[i]}%");

            var rows = await conn.QueryAsync<AdjacentChunkRow>(sql, parameters);
            var results = rows.Select(r => new DomainFileChunk
            {
                Id = r.chunk_id,
                Content = r.content ?? "",
                Source = r.source ?? "",
                Title = r.title ?? "",
                Score = 0.1f // Low score to indicate fallback
            }).ToList();

            _logger.LogInformation("PostgresTextFallback: found {Count} chunks for query \"{Query}\"",
                results.Count, query[..Math.Min(50, query.Length)]);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostgresTextFallback failed for collection {Collection}", collection);
            return new();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Detect if the query explicitly mentions any domain file by name or title.
    /// Returns the filenames that should receive a score boost during RRF.
    /// Matching is case-insensitive and works on partial name matches
    /// (e.g., "system spec" matches "SMA System Specification.docx").
    /// </summary>
    private async Task<List<string>> DetectMentionedSourcesAsync(string domainId, string query)
    {
        var files = await _repo.GetByDomainAsync(domainId);
        if (files.Count <= 1) return new(); // No boosting needed with only one file

        var queryLower = query.ToLowerInvariant();
        var queryTokens = queryLower.Split(new[] { ' ', ',', '.', '!', '?', '\'', '"' },
            StringSplitOptions.RemoveEmptyEntries);
        var boosted = new List<string>();

        foreach (var file in files)
        {
            // Build candidate tokens from filename (without extension), title, tags, and description
            var nameWithoutExt = Path.GetFileNameWithoutExtension(file.Filename).ToLowerInvariant();
            var titleLower = (file.Title ?? "").ToLowerInvariant();
            var descLower = (file.Description ?? "").ToLowerInvariant();

            var candidateTokens = nameWithoutExt
                .Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2) // Skip short tokens like "of", "a"
                .ToList();

            if (!string.IsNullOrEmpty(titleLower))
            {
                var titleTokens = titleLower
                    .Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length > 2);
                candidateTokens.AddRange(titleTokens);
            }

            // Include tags (e.g., "requirements", "monitoring", "sma")
            foreach (var tag in file.Tags)
            {
                var tagTokens = tag.ToLowerInvariant()
                    .Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length > 2);
                candidateTokens.AddRange(tagTokens);
            }

            // Include description tokens (significant words only)
            if (!string.IsNullOrEmpty(descLower))
            {
                var descTokens = descLower
                    .Split(new[] { ' ', '_', '-', ',', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length > 3); // Higher threshold for description to avoid noise
                candidateTokens.AddRange(descTokens);
            }

            candidateTokens = candidateTokens.Distinct().ToList();

            // Count how many candidate tokens are matched by the query
            // A match is either exact containment or a query word being a 3+ char prefix of the token
            // e.g., "spec" matches "specification", "manual" matches "manual"
            var matchCount = candidateTokens.Count(ct =>
                queryTokens.Any(qt =>
                    qt == ct || // exact match
                    (qt.Length >= 3 && ct.StartsWith(qt)) || // query word is prefix of candidate
                    (ct.Length >= 3 && qt.StartsWith(ct))    // candidate is prefix of query word
                ));

            // Require 2+ token matches to avoid false positives
            if (matchCount >= 2)
                boosted.Add(file.Filename);
        }

        return boosted;
    }

    private static string DetectContentType(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".md" => "documentation",
            ".txt" => "documentation",
            ".json" => "configuration",
            ".csv" or ".xlsx" => "data",
            ".pdf" or ".docx" => "documentation",
            _ => "general"
        };
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16];
    }

    private class EmbeddingSearchRow
    {
        public string item_id { get; set; } = "";
        public string content_hash { get; set; } = "";
        public double score { get; set; }
        public string? content { get; set; }
        public string? source { get; set; }
        public string? title { get; set; }
    }

    private class AdjacentChunkRow
    {
        public string chunk_id { get; set; } = "";
        public string? source { get; set; }
        public string? title { get; set; }
        public string? content { get; set; }
    }
}
