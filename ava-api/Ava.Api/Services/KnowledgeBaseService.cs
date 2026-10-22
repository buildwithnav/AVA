using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using Microsoft.Extensions.Options;

namespace Ava.Api.Services;

/// <summary>
/// Manages KB metadata, proxies calls to the Python ava-kb service,
/// tracks LastAccessedAt for idle cleanup, and provides KB summaries for system prompts.
/// </summary>
public class KnowledgeBaseService : IDisposable
{
    private readonly IKnowledgeBaseRepository _repo;
    private readonly IDomainRepository _domains;
    private readonly AvaSettings _settings;
    private readonly HttpClient _http;
    private readonly ILogger<KnowledgeBaseService> _logger;
    private readonly Timer _cleanupTimer;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public KnowledgeBaseService(
        IKnowledgeBaseRepository repo,
        IDomainRepository domains,
        IOptions<AvaSettings> settings,
        HttpClient http,
        ILogger<KnowledgeBaseService> logger)
    {
        _repo     = repo;
        _domains  = domains;
        _settings = settings.Value;
        _http     = http;
        _logger   = logger;

        _http.BaseAddress = new Uri(_settings.KnowledgeBase.KBServiceBaseUrl);
        _http.DefaultRequestHeaders.Add("X-KB-Api-Key", _settings.KnowledgeBase.ApiKey);
        _logger.LogInformation("KnowledgeBaseService initialized: BaseAddress={BaseAddress}", _http.BaseAddress);

        // Cleanup idle session KBs every 5 minutes
        _cleanupTimer = new Timer(CleanupIdleKBs, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    // ── Metadata ─────────────────────────────────────────────────────────────

    public async Task<List<KnowledgeBase>> GetForDomainAsync(string domainId)
        => await _repo.GetByDomainAsync(domainId);

    public async Task<List<KnowledgeBase>> GetForSessionAsync(string sessionId)
    {
        var all = await _repo.GetAllAsync();
        return all.Where(k => k.Scope == "session" && k.SessionId == sessionId).ToList();
    }

    public async Task<List<KnowledgeBase>> GetForConversationAsync(string conversationId)
    {
        var all = await _repo.GetAllAsync();
        return all.Where(k => k.ConversationId == conversationId).ToList();
    }

    public async Task<KnowledgeBase?> GetByIdAsync(string id)
        => await _repo.GetByIdAsync(id);

    public void TouchLastAccessed(string kbId)
    {
        var kb = _repo.GetByIdAsync(kbId).Result;
        if (kb != null) kb.LastAccessedAt = DateTime.UtcNow;
    }

    public void Register(KnowledgeBase kb)
        => _repo.Add(kb);

    public void Remove(string id)
        => _repo.Remove(id);

    // ── System prompt injection ──────────────────────────────────────────────

    /// <summary>
    /// Build a concise KB summary for the system prompt.
    /// Includes type, name, and row/chunk count — NOT schemas (those come via kb_schema tool).
    /// </summary>
    public async Task<string> BuildKBSummaryAsync(string domainId, string? sessionId, string? conversationId = null, string? promptMode = null)
    {
        var kbs = new List<KnowledgeBase>();
        kbs.AddRange(await GetForDomainAsync(domainId));
        if (!string.IsNullOrEmpty(sessionId))
            kbs.AddRange(await GetForSessionAsync(sessionId));
        if (!string.IsNullOrEmpty(conversationId))
        {
            var convKBs = await GetForConversationAsync(conversationId);
            foreach (var kb in convKBs)
                if (!kbs.Any(k => k.Id == kb.Id))
                    kbs.Add(kb);
        }

        if (kbs.Count == 0) return string.Empty;

        // "catalog" mode lists only table names/row counts; the model fetches columns on demand
        // via kb_schema. Default ("inline") dumps each dataset's columns into the prompt.
        var catalog = string.Equals(promptMode, "catalog", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine("Available knowledge bases (use kb_query with DuckDB SQL for datasets, kb_search for documents).");
        sb.AppendLine("Always double-quote table and column names in SQL (e.g. SELECT \"col\" FROM \"table\"):");
        if (catalog)
            sb.AppendLine("Only table names are listed below — call kb_schema with a KB id to see its columns (with clinical definitions) before writing SQL.");
        sb.AppendLine();

        foreach (var kb in kbs)
        {
            if (kb.Type == "dataset")
            {
                sb.AppendLine($"KB \"{kb.Id}\": \"{kb.Name}\" (tabular dataset, {kb.RowCount ?? 0} rows, SQL table: \"{kb.Id}\")");

                // Catalog mode: list the table (+ short description) only; the model calls
                // kb_schema on demand to fetch columns. Keeps the prompt small for wide/many datasets.
                if (catalog)
                {
                    if (!string.IsNullOrWhiteSpace(kb.Description))
                        sb.AppendLine($"  {kb.Description!.Trim()}");
                    sb.AppendLine();
                    continue;
                }

                // Include column names with rich semantic metadata when available
                var hasDefs = kb.ColumnDefinitions is { Count: > 0 };
                if (kb.Schema is { Count: > 0 })
                {
                    if (hasDefs)
                    {
                        sb.AppendLine("  Columns (with domain definitions):");
                        foreach (var col in kb.Schema)
                        {
                            if (kb.ColumnDefinitions!.TryGetValue(col.Name, out var def) &&
                                (!string.IsNullOrEmpty(def.Label) || !string.IsNullOrEmpty(def.Description)))
                            {
                                var label = string.IsNullOrEmpty(def.Label) ? col.Name : def.Label;
                                var desc  = string.IsNullOrEmpty(def.Description) ? "" : $" — {def.Description}";
                                sb.AppendLine($"    \"{col.Name}\" ({col.Type}): {label}{desc}");
                            }
                            else
                            {
                                sb.AppendLine($"    \"{col.Name}\" ({col.Type})");
                            }
                        }
                    }
                    else
                    {
                        sb.Append("  Columns: ");
                        sb.AppendLine(string.Join(", ", kb.Schema.Select(c => $"\"{c.Name}\" ({c.Type})")));
                    }
                }

                // Include sample rows only when column definitions are absent
                if (!hasDefs && kb.SampleRows is { Count: > 0 })
                {
                    sb.AppendLine("  Sample rows:");
                    foreach (var row in kb.SampleRows.Take(2))
                    {
                        var fields = row
                            .Where(kv => kv.Value != null)
                            .Take(8) // limit fields shown per row to keep it compact
                            .Select(kv =>
                            {
                                var val = kv.Value?.ToString() ?? "";
                                if (val.Length > 40) val = val[..40] + "...";
                                return $"\"{kv.Key}\": \"{val}\"";
                            });
                        sb.AppendLine($"    {{{string.Join(", ", fields)}}}");
                    }
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"KB \"{kb.Id}\": \"{kb.Name}\" (document collection, {kb.ChunkCount ?? 0} chunks)");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    // ── Proxy calls to Python service ────────────────────────────────────────

    public async Task<JsonDocument?> CallSchemaAsync(string kbId, string path, string type)
    {
        var body = JsonSerializer.Serialize(new { knowledgeBaseId = kbId, path, type }, _jsonOpts);
        _logger.LogInformation("KB CallSchema: POST {Base}kb/schema body={Body}", _http.BaseAddress, body);
        var response = await _http.PostAsync("kb/schema",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("KB CallSchema response: {Status} len={Len}", response.StatusCode, json.Length);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument?> CallQueryAsync(string[] datasetIds, string sql, Dictionary<string, string> datasetPaths)
    {
        var body = JsonSerializer.Serialize(new { datasetIds, sql, datasetPaths }, _jsonOpts);
        _logger.LogInformation("KB CallQuery: POST {Base}kb/query sql={Sql}", _http.BaseAddress, sql);
        var response = await _http.PostAsync("kb/query",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("KB CallQuery response: {Status} len={Len}", response.StatusCode, json.Length);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Streams a full CSV export from ava-kb's /kb/export endpoint (no row cap).
    /// The caller is responsible for resolving dataset paths and forwarding the stream.
    /// Returns the raw HttpResponseMessage so the controller can proxy the body directly.
    /// </summary>
    public async Task<HttpResponseMessage> CallExportAsync(string[] datasetIds, string sql, Dictionary<string, string> datasetPaths)
    {
        var body = JsonSerializer.Serialize(new { datasetIds, sql, datasetPaths }, _jsonOpts);
        _logger.LogInformation("KB CallExport: POST {Base}kb/export sql={Sql}", _http.BaseAddress, sql);
        var request = new HttpRequestMessage(HttpMethod.Post, "kb/export")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    public async Task<JsonDocument?> CallSearchAsync(string knowledgeBaseId, string query, int topK = 5, string? source = null)
    {
        var payload = new Dictionary<string, object?> { ["knowledgeBaseId"] = knowledgeBaseId, ["query"] = query, ["topK"] = topK };
        if (!string.IsNullOrEmpty(source))
            payload["source"] = source;
        var body = JsonSerializer.Serialize(payload, _jsonOpts);
        _logger.LogInformation("KB CallSearch: POST {Base}kb/search query={Query} source={Source}", _http.BaseAddress, query, source ?? "(none)");
        var response = await _http.PostAsync("kb/search",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("KB CallSearch response: {Status} len={Len}", response.StatusCode, json.Length);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument?> CallGetChunksBySourceAsync(string kbId, string source)
    {
        _logger.LogInformation("KB GetChunksBySource: GET {Base}kb/chunks-by-source kbId={KbId} source={Source}", _http.BaseAddress, kbId, source);
        var response = await _http.GetAsync($"kb/chunks-by-source?kbId={Uri.EscapeDataString(kbId)}&source={Uri.EscapeDataString(source)}");
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("KB GetChunksBySource failed ({Status}): {Body}", response.StatusCode, json);
            return null;
        }
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Section-aware lookup via ava-kb: finds chunks whose heading matches the query,
    /// then returns the full section (heading chunk + subsequent chunks until next heading).
    /// </summary>
    public async Task<JsonDocument?> CallSectionLookupAsync(string kbId, string sectionQuery, string? source = null, int maxFollowChunks = 5)
    {
        var payload = new Dictionary<string, object?>
        {
            ["knowledge_base_id"] = kbId,
            ["section_query"] = sectionQuery,
            ["max_follow_chunks"] = maxFollowChunks,
        };
        if (source != null) payload["source"] = source;

        var body = JsonSerializer.Serialize(payload, _jsonOpts);
        _logger.LogInformation("KB SectionLookup: POST {Base}kb/section-lookup query=\"{Query}\" source={Source}", _http.BaseAddress, sectionQuery, source ?? "(all)");
        var response = await _http.PostAsync("kb/section-lookup",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("KB SectionLookup failed ({Status}): {Body}", response.StatusCode, json);
            return null;
        }
        return JsonDocument.Parse(json);
    }

    /// <summary>Get a single chunk by its exact ID from ava-kb.</summary>
    public async Task<JsonDocument?> CallGetChunkByIdAsync(string kbId, string chunkId)
    {
        var url = $"kb/chunk-by-id?kbId={Uri.EscapeDataString(kbId)}&chunkId={Uri.EscapeDataString(chunkId)}";
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Search with parent expansion: searches children for precision,
    /// then returns full parent section content for LLM context.
    /// </summary>
    public async Task<JsonDocument?> CallSearchExpandedAsync(string knowledgeBaseId, string query, int topK = 10, string? source = null)
    {
        var payload = new Dictionary<string, object?> { ["knowledgeBaseId"] = knowledgeBaseId, ["query"] = query, ["topK"] = topK };
        if (!string.IsNullOrEmpty(source))
            payload["source"] = source;
        var body = JsonSerializer.Serialize(payload, _jsonOpts);
        _logger.LogInformation("KB SearchExpanded: POST {Base}kb/search-expanded query={Query} topK={TopK} source={Source}", _http.BaseAddress, query, topK, source ?? "(none)");
        var response = await _http.PostAsync("kb/search-expanded",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("KB SearchExpanded failed ({Status}): {Body}", response.StatusCode, json);
            return null;
        }
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Get a parent chunk by its ID — returns the full semantic section content.
    /// </summary>
    public async Task<JsonDocument?> CallGetParentAsync(string kbId, string parentId)
    {
        var url = $"kb/parent?kbId={Uri.EscapeDataString(kbId)}&parentId={Uri.EscapeDataString(parentId)}";
        _logger.LogDebug("KB GetParent: GET {Url}", url);
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument?> CallGetAdjacentChunksAsync(string kbId, string chunkId, int before = 0, int after = 3)
    {
        var payload = new Dictionary<string, object?>
        {
            ["knowledge_base_id"] = kbId,
            ["chunk_id"] = chunkId,
            ["before"] = before,
            ["after"] = after
        };
        var body = JsonSerializer.Serialize(payload, _jsonOpts);
        _logger.LogInformation("KB GetAdjacentChunks: POST {Base}kb/adjacent-chunks kbId={KbId} chunkId={ChunkId} before={Before} after={After}",
            _http.BaseAddress, kbId, chunkId, before, after);
        var response = await _http.PostAsync("kb/adjacent-chunks",
            new StringContent(body, Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("KB GetAdjacentChunks failed ({Status}): {Body}", response.StatusCode, errorBody);
            return null;
        }
        var json = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("KB GetAdjacentChunks response: len={Len}", json.Length);
        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument?> CallIngestAsync(string kbId, string scope, string? sessionId, string? domainId, string name, string format, object? data)
    {
        var payload = new Dictionary<string, object?>
        {
            ["kbId"] = kbId,
            ["scope"] = scope,
            ["name"] = name,
            ["format"] = format,
        };
        if (sessionId != null) payload["sessionId"] = sessionId;
        if (domainId != null) payload["domainId"] = domainId;
        if (data != null) payload["data"] = data;

        var body = JsonSerializer.Serialize(payload, _jsonOpts);
        _logger.LogInformation("KB CallIngest: POST {Base}kb/ingest kbId={KbId} name={Name} format={Format}", _http.BaseAddress, kbId, name, format);
        var response = await _http.PostAsync("kb/ingest",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("KB CallIngest response: {Status} len={Len}", response.StatusCode, json.Length);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument?> CallUploadAsync(string kbId, string scope, string? sessionId, string? domainId, string type, Stream fileStream, string fileName,
        bool appendMode = false, int? chunkSize = null, int? chunkOverlap = null)
    {
        _logger.LogInformation("KB CallUpload: POST {Base}kb/upload kbId={KbId} fileName={FileName} type={Type}", _http.BaseAddress, kbId, fileName, type);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(kbId), "kbId");
        content.Add(new StringContent(scope), "scope");
        content.Add(new StringContent(type), "type");
        if (sessionId != null) content.Add(new StringContent(sessionId), "sessionId");
        if (domainId != null) content.Add(new StringContent(domainId), "domainId");
        if (appendMode) content.Add(new StringContent("true"), "append_mode");
        if (chunkSize.HasValue) content.Add(new StringContent(chunkSize.Value.ToString()), "chunk_size");
        if (chunkOverlap.HasValue) content.Add(new StringContent(chunkOverlap.Value.ToString()), "chunk_overlap");
        content.Add(new StreamContent(fileStream), "file", fileName);

        var response = await _http.PostAsync("kb/upload", content);
        var json = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("KB CallUpload response: {Status} len={Len}", response.StatusCode, json.Length);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("KB upload failed ({Status}): {Body}", response.StatusCode, json);
            throw new HttpRequestException(
                $"ava-kb upload failed ({(int)response.StatusCode} {response.StatusCode}): {json}");
        }
        return JsonDocument.Parse(json);
    }

    public async Task DeleteKBAsync(string kbId)
    {
        var response = await _http.DeleteAsync($"kb/{kbId}");
        response.EnsureSuccessStatusCode();
        _repo.Remove(kbId);
    }

    /// <summary>
    /// Deletes the ava-kb data files for a KB without removing the metadata record.
    /// Used before a refresh so the new upload overwrites cleanly.
    /// </summary>
    public async Task DeleteKbDataOnlyAsync(string kbId)
    {
        var response = await _http.DeleteAsync($"kb/{kbId}");
        // 404 is fine — means there was no data to delete yet (first refresh)
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    // ── Idle cleanup ─────────────────────────────────────────────────────────

    private void CleanupIdleKBs(object? state)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-_settings.KnowledgeBase.IdleTimeoutMinutes);
            var all = _repo.GetAllAsync().Result;
            foreach (var kb in all.Where(k => k.Scope == "session" && k.LastAccessedAt < cutoff))
            {
                _logger.LogInformation("Cleaning up idle session KB: {KbId} (last accessed {LastAccess})", kb.Id, kb.LastAccessedAt);
                try
                {
                    _http.DeleteAsync($"/kb/{kb.Id}").Wait();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete idle KB {KbId} from Python service", kb.Id);
                }
                _repo.Remove(kb.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during KB idle cleanup");
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}
