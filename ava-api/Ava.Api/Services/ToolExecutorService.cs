using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using Ava.Api.Config;
using Ava.Api.Models;
using Microsoft.Extensions.Options;

namespace Ava.Api.Services;

/// <summary>
/// Dispatches tool calls: internal KB tools route to KnowledgeBaseService,
/// external tools route to HTTP MCP/REST servers. Handles storeAsKB for external tool outputs.
/// </summary>
public class ToolExecutorService
{
    private readonly AvaSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthService _authService;
    private readonly UserSessionService _userSession;
    private readonly KnowledgeBaseService _kbService;
    private readonly DomainFileService _domainFileService;
    private readonly McpClientService _mcpClient;
    private readonly IToolRepository _toolRepo;
    private readonly IKnowledgeBaseRepository _kbRepo;
    private readonly ILogger<ToolExecutorService> _logger;

    private const string McpRoutePrefix = "mcp:";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ToolExecutorService(
        IOptions<AvaSettings> settings,
        IHttpClientFactory httpClientFactory,
        IAuthService authService,
        UserSessionService userSession,
        KnowledgeBaseService kbService,
        DomainFileService domainFileService,
        McpClientService mcpClient,
        IToolRepository toolRepo,
        IKnowledgeBaseRepository kbRepo,
        ILogger<ToolExecutorService> logger)
    {
        _settings          = settings.Value;
        _httpClientFactory = httpClientFactory;
        _authService       = authService;
        _userSession       = userSession;
        _kbService         = kbService;
        _domainFileService = domainFileService;
        _mcpClient         = mcpClient;
        _toolRepo          = toolRepo;
        _kbRepo            = kbRepo;
        _logger            = logger;
    }

    /// <summary>
    /// Execute a tool call from the LLM. Routes internal tools to KnowledgeBaseService,
    /// external tools to HTTP endpoints.
    /// </summary>
    public async Task<(bool Success, string Result, int DurationMs)> ExecuteAsync(
        ToolDefinition tool, Dictionary<string, object?> parameters, string? sessionId = null, string? conversationId = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        parameters = NormalizeParameters(parameters);

        try
        {
            // Route internal tools
            if (tool.Internal)
            {
                var result = await ExecuteInternalAsync(tool.Id, parameters, sessionId);
                sw.Stop();
                return (true, result, (int)sw.ElapsedMilliseconds);
            }

            // External tool execution
            return await ExecuteExternalAsync(tool, parameters, sessionId, conversationId, sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Tool {Id} threw exception", tool.Id);
            return (false, $"Tool execution failed: {ex.Message}", (int)sw.ElapsedMilliseconds);
        }
    }

    // ── Internal tool routing ─────────────────────────────────────────────────

    private async Task<string> ExecuteInternalAsync(string toolId, Dictionary<string, object?> parameters, string? sessionId)
    {
        switch (toolId.ToLowerInvariant())
        {
            case "kb_schema":
            {
                var kbId = parameters.GetValueOrDefault("knowledgeBaseId")?.ToString() ?? "";
                var kb = await _kbService.GetByIdAsync(kbId);
                if (kb == null)
                    return JsonSerializer.Serialize(new { error = "kb_not_found", message = $"Knowledge base '{kbId}' not found." }, _jsonOptions);

                _kbService.TouchLastAccessed(kbId);

                // Prefer the schema + business definitions stored in Postgres (populated at upload
                // time). Merge each column's type with its clinical label/description so the model
                // gets meaningful names without a parquet read. Falls back to the ava-kb parquet
                // read only for KBs that have no stored schema (e.g. documents or legacy datasets).
                if (kb.Type == "dataset" && kb.Schema is { Count: > 0 })
                {
                    var columns = kb.Schema.Select(c =>
                    {
                        KBColumnDefinition? def = null;
                        kb.ColumnDefinitions?.TryGetValue(c.Name, out def);
                        return new
                        {
                            name        = c.Name,
                            type        = c.Type,
                            label       = string.IsNullOrWhiteSpace(def?.Label) ? null : def!.Label,
                            description = string.IsNullOrWhiteSpace(def?.Description) ? null : def!.Description
                        };
                    }).ToList();

                    return JsonSerializer.Serialize(new
                    {
                        kbId     = kb.Id,
                        name     = kb.Name,
                        description = kb.Description,
                        type     = kb.Type,
                        rowCount = kb.RowCount ?? 0,
                        columns
                    }, _jsonOptions);
                }

                var doc = await _kbService.CallSchemaAsync(kbId, kb.DataPath ?? "", kb.Type);
                return doc?.RootElement.GetRawText() ?? "{}";
            }

            case "kb_query":
            {
                var datasetIdsRaw = parameters.GetValueOrDefault("datasetIds");
                var sql = parameters.GetValueOrDefault("sql")?.ToString() ?? "";

                var datasetIds = ParseStringArray(datasetIdsRaw);
                var datasetPaths = new Dictionary<string, string>();

                foreach (var id in datasetIds)
                {
                    var kb = await _kbService.GetByIdAsync(id);
                    if (kb == null)
                        return JsonSerializer.Serialize(new { error = "kb_not_found", message = $"Dataset '{id}' not found." }, _jsonOptions);
                    if (kb.Type != "dataset")
                        return JsonSerializer.Serialize(new { error = "wrong_type", message = $"'{id}' is a {kb.Type}, not a dataset. Use kb_search for documents." }, _jsonOptions);
                    _kbService.TouchLastAccessed(id);
                    datasetPaths[id] = kb.DataPath ?? "";
                }

                var doc = await _kbService.CallQueryAsync(datasetIds, sql, datasetPaths);
                return doc?.RootElement.GetRawText() ?? "{}";
            }

            case "kb_search":
            {
                var kbId = parameters.GetValueOrDefault("knowledgeBaseId")?.ToString() ?? "";
                var query = parameters.GetValueOrDefault("query")?.ToString() ?? "";
                var topK = 5;
                if (parameters.TryGetValue("topK", out var topKVal) && topKVal != null)
                    int.TryParse(topKVal.ToString(), out topK);

                var kb = await _kbService.GetByIdAsync(kbId);
                if (kb == null)
                    return JsonSerializer.Serialize(new { error = "kb_not_found", message = $"Knowledge base '{kbId}' not found." }, _jsonOptions);
                if (kb.Type != "document")
                    return JsonSerializer.Serialize(new { error = "wrong_type", message = $"'{kbId}' is a {kb.Type}, not a document. Use kb_query for datasets." }, _jsonOptions);

                _kbService.TouchLastAccessed(kbId);
                var doc = await _kbService.CallSearchAsync(kbId, query, topK);
                return doc?.RootElement.GetRawText() ?? "{}";
            }

            case "start_workflow":
            {
                // Return the parameters — AgentService handles the actual workflow dispatch
                var description = parameters.GetValueOrDefault("description")?.ToString() ?? "";
                var context = parameters.GetValueOrDefault("context");
                return JsonSerializer.Serialize(new { matched = true, description, context }, _jsonOptions);
            }

            case "search_docs":
            {
                var query = parameters.GetValueOrDefault("query")?.ToString() ?? "";
                var domainId = parameters.GetValueOrDefault("domainId")?.ToString() ?? "";
                var topK = 10;
                if (parameters.TryGetValue("topK", out var topKVal2) && topKVal2 != null)
                    int.TryParse(topKVal2.ToString(), out topK);

                if (string.IsNullOrEmpty(query))
                    return JsonSerializer.Serialize(new { error = "query is required" }, _jsonOptions);

                var searchResults = await _domainFileService.ExecuteSearchDocsAsync(domainId, query, topK);
                if (searchResults.Count == 0)
                    return JsonSerializer.Serialize(new { message = "No relevant content found in domain documents.", chunks = Array.Empty<object>() }, _jsonOptions);

                return JsonSerializer.Serialize(new
                {
                    message = $"Found {searchResults.Count} relevant sections from domain documents.",
                    chunks = searchResults.Select(c => new { c.Id, c.Source, c.Title, c.Content, c.Score, c.ParentId })
                }, _jsonOptions);
            }

            case "get_ado_work_item":
            {
                var (success, result) = await _mcpClient.CallToolAsync("context-forge", "ado-mcp-server-get-work-item", parameters, sessionId);
                if (!success)
                    return JsonSerializer.Serialize(new { error = "ado_call_failed", message = result }, _jsonOptions);
                return result;
            }

            case "query_ado_work_items":
            {
                var (success, result) = await _mcpClient.CallToolAsync("context-forge", "ado-mcp-server-run-wiql-query", parameters, sessionId);
                if (!success)
                    return JsonSerializer.Serialize(new { error = "ado_call_failed", message = result }, _jsonOptions);

                // Post-process: if exactly one work item, add parentId at root for easy extraction
                try
                {
                    using var doc = JsonDocument.Parse(result);
                    if (doc.RootElement.TryGetProperty("workItems", out var wiArray) &&
                        wiArray.ValueKind == JsonValueKind.Array)
                    {
                        var count = wiArray.GetArrayLength();
                        if (count == 1)
                        {
                            var item = wiArray[0];
                            var id = item.GetProperty("id").GetInt32();
                            var title = item.TryGetProperty("title", out var t) ? t.GetString() : "";
                            var type = item.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                            return JsonSerializer.Serialize(new { parentId = id, parentTitle = title, parentType = type, count, workItems = JsonSerializer.Deserialize<object>(wiArray.GetRawText()) }, _jsonOptions);
                        }
                        return JsonSerializer.Serialize(new { count, workItems = JsonSerializer.Deserialize<object>(wiArray.GetRawText()) }, _jsonOptions);
                    }
                }
                catch { /* fall through to raw result */ }
                return result;
            }



            default:
                return JsonSerializer.Serialize(new { error = "unknown_internal_tool", message = $"Internal tool '{toolId}' not recognized." }, _jsonOptions);
        }
    }

    private static string[] ParseStringArray(object? value)
    {
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            if (je.ValueKind == JsonValueKind.String)
            {
                var s = je.GetString() ?? "";
                // Handle LLM double-serialization: "datasetIds": "[\"id1\"]" instead of ["id1"]
                if (s.StartsWith("["))
                {
                    try
                    {
                        var inner = JsonSerializer.Deserialize<string[]>(s);
                        if (inner != null && inner.Length > 0) return inner;
                    }
                    catch { }
                }
                return new[] { s };
            }
        }
        if (value is string s2)
        {
            if (s2.StartsWith("["))
            {
                try
                {
                    var inner = JsonSerializer.Deserialize<string[]>(s2);
                    if (inner != null && inner.Length > 0) return inner;
                }
                catch { }
            }
            return new[] { s2 };
        }
        if (value is string[] arr)
            return arr;
        return Array.Empty<string>();
    }

    // ── External tool execution ───────────────────────────────────────────────

    private async Task<(bool Success, string Result, int DurationMs)> ExecuteExternalAsync(
        ToolDefinition tool, Dictionary<string, object?> parameters, string? sessionId, string? conversationId, System.Diagnostics.Stopwatch sw)
    {
        if (!_settings.Services.TryGetValue(tool.Service, out var svc) || !svc.Enabled)
            return (false, $"Service '{tool.Service}' is not configured or disabled.", 0);

        // ── MCP dispatch: route all tools whose service is marked IsMcp ──────────
        if (svc.IsMcp)
        {
            // Tool name is declared explicitly with the "mcp:" route prefix (e.g. "mcp:get_work_item")
            var mcpToolName = tool.Route.StartsWith(McpRoutePrefix, StringComparison.OrdinalIgnoreCase)
                ? tool.Route[McpRoutePrefix.Length..]
                : tool.Id;

            // Enrich parameters for tools that need type injection
            EnrichMcpParameters(tool.Id, mcpToolName, parameters);

            // Special handling: ado-get-sprint-items requires a two-step MCP call
            // (first get current iteration ID, then get work items for that iteration)
            if (tool.Id.Equals("ado-get-sprint-items", StringComparison.OrdinalIgnoreCase))
            {
                var sprintResult = await ExecuteSprintItemsAsync(tool.Service, parameters, sessionId);
                sw.Stop();
                return (sprintResult.Success, sprintResult.Result, (int)sw.ElapsedMilliseconds);
            }

            _logger.LogInformation("MCP dispatch: [{Service}] tool {Id} → {McpTool}", tool.Service, tool.Id, mcpToolName);
            var (success, result) = await _mcpClient.CallToolAsync(tool.Service, mcpToolName, parameters, sessionId);
            sw.Stop();
            return (success, result, (int)sw.ElapsedMilliseconds);
        }

        var url = BuildUrl(svc.BaseUrl, tool, parameters);
        _logger.LogInformation("External tool {Id}: service={Service}, baseUrl={BaseUrl}, builtUrl={Url}, method={Method}, requiresCdsAuth={Auth}",
            tool.Id, tool.Service, svc.BaseUrl, url, tool.HttpMethod, svc.RequiresCdsAuth);
        HttpClient client;

        if (svc.RequiresCdsAuth)
        {
            _logger.LogInformation("Tool {Id} requires CDS auth — using authenticated client for {BaseUrl}", tool.Id, svc.BaseUrl);
            client = await _authService.CreateAuthenticatedClientAsync(svc.BaseUrl);
        }
        else
        {
            client = _httpClientFactory.CreateClient();
        }

        var isBinary = tool.ResponseFormat is "xlsx" or "csv";

        if (isBinary)
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        else
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        if (tool.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            tool.HttpMethod.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
        {
            HttpContent content;
            if (tool.ContentType.Equals("form", StringComparison.OrdinalIgnoreCase))
            {
                content = BuildFormContent(tool, parameters, _logger);
            }
            else
            {
                var body = BuildBody(tool, parameters);
                content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            response = tool.HttpMethod.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
                ? await client.PatchAsync(url, content)
                : await client.PostAsync(url, content);
        }
        else
        {
            response = await client.GetAsync(url);
        }

        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Tool {Id} returned {Status} from {Url}: {Body}", tool.Id, response.StatusCode, url, errorBody);
            return (false, $"Tool returned {(int)response.StatusCode}: {errorBody}", (int)sw.ElapsedMilliseconds);
        }

        _logger.LogInformation("Tool {Id} succeeded in {Ms}ms from {Url}", tool.Id, sw.ElapsedMilliseconds, url);

        // Handle binary responses (xlsx, csv) — store as KB via file upload
        if (isBinary && tool.StoreAsKB is { Enabled: true })
        {
            var resultSummary = await StoreBinaryToolOutputAsKBAsync(tool, response, sessionId, conversationId);
            return (true, resultSummary, (int)sw.ElapsedMilliseconds);
        }

        var resultJson = await response.Content.ReadAsStringAsync();

        // Handle storeAsKB for JSON responses
        if (tool.StoreAsKB is { Enabled: true })
        {
            await StoreToolOutputAsKBAsync(tool, resultJson, sessionId);
        }

        return (true, resultJson, (int)sw.ElapsedMilliseconds);
    }

    private async Task<string> StoreBinaryToolOutputAsKBAsync(
        ToolDefinition tool, HttpResponseMessage response, string? sessionId, string? conversationId)
    {
        try
        {
            var config = tool.StoreAsKB!;
            var kbId = $"{tool.Id}-{Guid.NewGuid():N}"[..24];
            var format = tool.ResponseFormat ?? "xlsx";
            var ext = format == "csv" ? ".csv" : ".xlsx";

            var bytes = await response.Content.ReadAsByteArrayAsync();
            _logger.LogInformation("Tool {ToolId} returned {Size} bytes of {Format} data", tool.Id, bytes.Length, format);

            // If no data returned, skip KB upload and report empty result
            if (bytes.Length == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "empty",
                    message = $"The report returned no data. Try adjusting your filter criteria."
                }, _jsonOptions);
            }

            // Session-scoped KBs need a session ID; fall back to a generated one if the client didn't provide one.
            var effectiveSessionId = sessionId ?? $"auto-{Guid.NewGuid():N}"[..24];

            using var stream = new MemoryStream(bytes);
            var uploadResult = await _kbService.CallUploadAsync(
                kbId, config.Scope, effectiveSessionId, null, "dataset", stream, $"{config.KbName}{ext}");

            var rowCount = uploadResult?.RootElement.TryGetProperty("rowCount", out var rc) == true ? rc.GetInt32() : (int?)null;

            // Parse schema (column definitions) from upload response
            List<KBColumnInfo>? schema = null;
            if (uploadResult?.RootElement.TryGetProperty("schema", out var schemaProp) == true)
            {
                schema = schemaProp.EnumerateArray()
                    .Select(e => new KBColumnInfo
                    {
                        Name = e.GetProperty("name").GetString() ?? "",
                        Type = e.GetProperty("type").GetString() ?? ""
                    })
                    .ToList();
            }

            // Parse sample rows (2-3 rows) from upload response
            List<Dictionary<string, object?>>? sampleRows = null;
            if (uploadResult?.RootElement.TryGetProperty("sampleRows", out var sampleProp) == true)
            {
                sampleRows = sampleProp.EnumerateArray()
                    .Take(3)
                    .Select(row => row.EnumerateObject()
                        .ToDictionary(
                            p => p.Name,
                            p => (object?)(p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString())))
                    .ToList();
            }

            var kb = new KnowledgeBase
            {
                Id = kbId,
                Name = config.KbName,
                Type = "dataset",
                Scope = config.Scope,
                SessionId = effectiveSessionId,
                ConversationId = conversationId,
                Source = new KBSource { Kind = "api", Endpoint = tool.Route },
                RowCount = rowCount,
                Schema = schema,
                SampleRows = sampleRows,
            };
            _kbService.Register(kb);
            _logger.LogInformation("Stored tool {ToolId} binary output as KB {KbId} ({Rows} rows, {Cols} columns, conv={ConvId})",
                tool.Id, kbId, rowCount, schema?.Count ?? 0, conversationId);

            return JsonSerializer.Serialize(new
            {
                status = "loaded",
                kbId,
                name = config.KbName,
                rowCount,
                columns = schema?.Select(c => c.Name).ToList() ?? new List<string>(),
                message = $"Loaded {config.KbName} with {rowCount} rows. The data is now available for analysis."
            }, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store binary tool {ToolId} output as KB", tool.Id);
            return JsonSerializer.Serialize(new
            {
                status = "partial",
                message = $"Report data was fetched successfully from CDS but could not be stored as a knowledge base: {ex.Message}. You may need to re-run the report.",
                error = "kb_ingest_failed"
            }, _jsonOptions);
        }
    }

    private async Task StoreToolOutputAsKBAsync(ToolDefinition tool, string resultJson, string? sessionId)
    {
        try
        {
            var config = tool.StoreAsKB!;
            var kbId = $"{tool.Id}-{Guid.NewGuid():N}"[..24];
            var format = tool.ResponseFormat ?? "json";

            // Parse the JSON result and forward to Python service
            var data = JsonSerializer.Deserialize<object>(resultJson, _jsonOptions);
            var ingestResult = await _kbService.CallIngestAsync(
                kbId, config.Scope, sessionId, null, config.KbName, format, data);

            // Register KB metadata
            var kb = new KnowledgeBase
            {
                Id = kbId,
                Name = config.KbName,
                Type = "dataset",
                Scope = config.Scope,
                SessionId = sessionId,
                Source = new KBSource { Kind = "api", Endpoint = tool.Route },
                RowCount = ingestResult?.RootElement.TryGetProperty("rowCount", out var rc) == true ? rc.GetInt32() : null,
            };
            _kbService.Register(kb);
            _logger.LogInformation("Stored tool {ToolId} output as KB {KbId} ({Rows} rows)", tool.Id, kbId, kb.RowCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store tool {ToolId} output as KB", tool.Id);
            // Non-fatal — tool result is still returned to the user
        }
    }

    // ── Domain KB refresh ────────────────────────────────────────────────────

    /// <summary>
    /// Re-runs a domain KB's source tool, replaces the stored data in ava-kb,
    /// and updates the DB record (rowCount, schema, sampleRows, lastRefreshedAt).
    /// </summary>
    public async Task<KnowledgeBase?> RefreshDomainKbAsync(KnowledgeBase kb)
    {
        var tool = await _toolRepo.GetByIdAsync(kb.Source.ToolId!);
        if (tool == null)
        {
            _logger.LogWarning("RefreshDomainKb: tool '{ToolId}' not found for KB '{KbId}'", kb.Source.ToolId, kb.Id);
            return null;
        }

        if (!_settings.Services.TryGetValue(tool.Service, out var svc) || !svc.Enabled)
        {
            _logger.LogWarning("RefreshDomainKb: service '{Service}' not configured for tool '{ToolId}'", tool.Service, tool.Id);
            return null;
        }

        _logger.LogInformation("Refreshing domain KB '{KbId}' via tool '{ToolId}'", kb.Id, tool.Id);

        try
        {
            // Call the tool endpoint with no user-supplied params (uses fixed route/query params only)
            var url = BuildUrl(svc.BaseUrl, tool, new Dictionary<string, object?>());
            HttpClient client = svc.RequiresCdsAuth
                ? await _authService.CreateAuthenticatedClientAsync(svc.BaseUrl)
                : _httpClientFactory.CreateClient();

            var isBinary = tool.ResponseFormat is "xlsx" or "csv";
            if (isBinary)
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            else
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            if (tool.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var body = BuildBody(tool, new Dictionary<string, object?>());
                var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                response = await client.PostAsync(url, content);
            }
            else
            {
                response = await client.GetAsync(url);
            }

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("RefreshDomainKb: tool '{ToolId}' returned {Status}: {Body}", tool.Id, response.StatusCode, err);
                return null;
            }

            // Delete old ava-kb data so re-upload is clean
            try { await _kbService.DeleteKbDataOnlyAsync(kb.Id); } catch { /* best-effort */ }

            // Re-ingest with same kbId so domain conversations continue working
            JsonDocument? uploadResult;
            if (isBinary)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                var ext   = tool.ResponseFormat == "csv" ? ".csv" : ".xlsx";
                using var stream = new MemoryStream(bytes);
                uploadResult = await _kbService.CallUploadAsync(kb.Id, "domain", null, kb.DomainId, "dataset", stream, $"{kb.Name}{ext}");
            }
            else
            {
                var json = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<object>(json, _jsonOptions);
                uploadResult = await _kbService.CallIngestAsync(kb.Id, "domain", null, kb.DomainId, kb.Name, "json", data);
            }

            // Parse updated metadata from upload response
            if (uploadResult?.RootElement.TryGetProperty("rowCount", out var rcProp) == true)
                kb.RowCount = rcProp.GetInt32();

            if (uploadResult?.RootElement.TryGetProperty("dataPath", out var dpProp) == true)
                kb.DataPath = dpProp.GetString();

            if (uploadResult?.RootElement.TryGetProperty("schema", out var schemaProp) == true)
            {
                kb.Schema = schemaProp.EnumerateArray()
                    .Select(e => new KBColumnInfo
                    {
                        Name = e.GetProperty("name").GetString() ?? "",
                        Type = e.GetProperty("type").GetString() ?? "",
                    }).ToList();
            }

            if (uploadResult?.RootElement.TryGetProperty("sampleRows", out var srProp) == true)
            {
                kb.SampleRows = srProp.EnumerateArray().Take(3)
                    .Select(row => row.EnumerateObject()
                        .ToDictionary(p => p.Name, p => (object?)(
                            p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString())))
                    .ToList();
            }

            kb.LastRefreshedAt = DateTime.UtcNow;
            await _kbRepo.UpdateAsync(kb);

            _logger.LogInformation("Domain KB '{KbId}' refreshed — {Rows} rows", kb.Id, kb.RowCount);
            return kb;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RefreshDomainKb failed for KB '{KbId}'", kb.Id);
            return null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static Dictionary<string, object?> NormalizeParameters(Dictionary<string, object?> parameters)
    {
        var result = new Dictionary<string, object?>(parameters.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in parameters)
        {
            if (val is JsonElement je)
            {
                result[key] = je.ValueKind switch
                {
                    JsonValueKind.Number  => je.TryGetInt64(out var l) ? (object?)l : je.GetDouble(),
                    JsonValueKind.String  => je.GetString(),
                    JsonValueKind.True    => true,
                    JsonValueKind.False   => false,
                    JsonValueKind.Null    => null,
                    _                     => je.GetRawText()
                };
            }
            else
            {
                result[key] = val;
            }
        }
        return result;
    }

    private static string BuildUrl(string baseUrl, ToolDefinition tool, Dictionary<string, object?> parameters)
    {
        var route = tool.Route;
        foreach (var p in tool.Parameters.Where(p => p.Location == "path"))
        {
            if (parameters.TryGetValue(p.Name, out var val) && val != null)
                route = route.Replace($"{{{p.Name}}}", Uri.EscapeDataString(val.ToString()!));
        }

        var uri = new UriBuilder(baseUrl.TrimEnd('/') + route);
        var queryParams = tool.Parameters.Where(p => p.Location == "query").ToList();
        if (queryParams.Any())
        {
            var query = HttpUtility.ParseQueryString(string.Empty);
            foreach (var p in queryParams)
            {
                if (parameters.TryGetValue(p.Name, out var val) && val != null)
                    query[p.Name] = val.ToString();
            }
            uri.Query = query.ToString();
        }
        return uri.ToString();
    }

    private static Dictionary<string, object?> BuildBody(ToolDefinition tool, Dictionary<string, object?> parameters)
    {
        var body = new Dictionary<string, object?>();
        foreach (var p in tool.Parameters.Where(p => p.Location == "body"))
        {
            if (parameters.TryGetValue(p.Name, out var val))
                body[p.Name] = val;
            else if (p.DefaultValue != null)
                body[p.Name] = p.DefaultValue;
        }
        return body;
    }

    private static HttpContent BuildFormContent(ToolDefinition tool, Dictionary<string, object?> parameters, ILogger logger)
    {
        var formFields = new Dictionary<string, string>();

        foreach (var p in tool.Parameters.Where(p => p.Location.Equals("form", StringComparison.OrdinalIgnoreCase)))
        {
            if (parameters.TryGetValue(p.Name, out var val) && val != null)
                formFields[p.Name] = val.ToString()!;
            else if (p.DefaultValue != null)
                formFields[p.Name] = p.DefaultValue;
        }

        var dataDict = new Dictionary<string, object?>();
        foreach (var p in tool.Parameters.Where(p => p.Location.Equals("formData", StringComparison.OrdinalIgnoreCase)))
        {
            if (parameters.TryGetValue(p.Name, out var val))
                dataDict[p.Name] = val;
            else if (p.DefaultValue != null)
                dataDict[p.Name] = p.DefaultValue;
        }
        if (dataDict.Count > 0)
            formFields["data"] = JsonSerializer.Serialize(dataDict);

        foreach (var p in tool.Parameters.Where(p => p.Location.Equals("body", StringComparison.OrdinalIgnoreCase)))
        {
            if (parameters.TryGetValue(p.Name, out var val) && val != null)
                formFields[p.Name] = val.ToString()!;
            else if (p.DefaultValue != null)
                formFields[p.Name] = p.DefaultValue;
        }

        logger.LogDebug("BuildFormContent for {ToolId}: {FieldCount} fields", tool.Id, formFields.Count);

        return new FormUrlEncodedContent(formFields);
    }

    /// <summary>
    /// Composite MCP operation for the legacy ado-get-sprint-items tool.
    /// Step 1: get_team_iterations with timeframe=current to find the iteration ID.
    /// Step 2: get_iteration_work_items with that iteration ID.
    /// </summary>
    private async Task<(bool Success, string Result)> ExecuteSprintItemsAsync(
        string serviceKey, Dictionary<string, object?> parameters, string? sessionId)
    {
        var team = parameters.TryGetValue("team", out var t) && t != null ? t.ToString()! : "Quality Crusaders";

        // Step 1: Get current iteration
        var iterParams = new Dictionary<string, object?>
        {
            ["team"] = team,
            ["timeframe"] = "current"
        };
        var (iterSuccess, iterResult) = await _mcpClient.CallToolAsync(serviceKey, "get_team_iterations", iterParams, sessionId);
        if (!iterSuccess)
            return (false, $"Failed to get current sprint: {iterResult}");

        // Extract iteration ID from response
        string? iterationId = null;
        try
        {
            using var doc = JsonDocument.Parse(iterResult);
            // Response is typically an array of iterations; take the first one
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                if (first.TryGetProperty("id", out var idProp))
                    iterationId = idProp.GetString();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                    iterationId = idProp.GetString();
                else if (doc.RootElement.TryGetProperty("iterations", out var iters)
                         && iters.ValueKind == JsonValueKind.Array && iters.GetArrayLength() > 0)
                {
                    iterationId = iters[0].TryGetProperty("id", out var nestedId) ? nestedId.GetString() : null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse iteration response for sprint items");
            return (false, $"Failed to parse current sprint info: {iterResult}");
        }

        if (string.IsNullOrEmpty(iterationId))
            return (false, $"Could not determine current sprint iteration ID. Response: {iterResult}");

        // Step 2: Get work items for that iteration
        var itemsParams = new Dictionary<string, object?>
        {
            ["team"] = team,
            ["iterationId"] = iterationId
        };
        _logger.LogInformation("MCP composite [{Service}]: ado-get-sprint-items → get_iteration_work_items(team={Team}, iterationId={IterId})",
            serviceKey, team, iterationId);
        return await _mcpClient.CallToolAsync(serviceKey, "get_iteration_work_items", itemsParams, sessionId);
    }

    /// <summary>
    /// Enriches parameters for MCP calls that need implicit values based on the legacy tool ID.
    /// For example, the old "ado-create-epic" REST endpoint was specific to Epics, but the MCP
    /// "create_work_item" tool requires an explicit "type" parameter.
    /// </summary>
    private static void EnrichMcpParameters(string toolId, string mcpToolName, Dictionary<string, object?> parameters)
    {
        // Inject work item type for create_work_item mapped from type-specific legacy tools
        if (mcpToolName == "create_work_item" && !parameters.ContainsKey("type"))
        {
            var inferredType = toolId.ToLowerInvariant() switch
            {
                "ado-create-pbi"     => "Product Backlog Item",
                "ado-create-bug"     => "Bug",
                "ado-create-task"    => "Task",
                "ado-create-epic"    => "Epic",
                "ado-create-feature" => "Feature",
                _                    => null
            };
            if (inferredType != null)
                parameters["type"] = inferredType;
        }

        // For ado-create-feature (legacy tool ID) or create_work_item with type=Feature,
        // inject required custom fields when not already specified by the workflow author.
        bool isFeatureCreate =
            toolId.Equals("ado-create-feature", StringComparison.OrdinalIgnoreCase)
            || (mcpToolName == "create_work_item"
                && parameters.TryGetValue("type", out var typeVal)
                && typeVal?.ToString()?.Equals("Feature", StringComparison.OrdinalIgnoreCase) == true);

        if (isFeatureCreate && !parameters.ContainsKey("additionalFields"))
        {
            parameters["additionalFields"] = new Dictionary<string, object?>
            {
                ["Custom.Rel_Purpose"] = "Enhancement",
                ["Custom.Source"] = "Systems Project Request"
            };
        }

        // For get_team_iterations mapped from ado-get-current-sprint, inject timeframe=current
        if (mcpToolName == "get_team_iterations" && toolId.Equals("ado-get-current-sprint", StringComparison.OrdinalIgnoreCase))
        {
            if (!parameters.ContainsKey("timeframe"))
                parameters["timeframe"] = "current";
        }

        // Ensure "team" is present for sprint/iteration tools (MCP requires it)
        if (mcpToolName is "get_team_iterations" or "get_iteration_work_items" or "get_backlog_items")
        {
            var hasTeam = parameters.TryGetValue("team", out var teamVal)
                          && teamVal != null
                          && !string.IsNullOrWhiteSpace(teamVal.ToString());
            if (!hasTeam)
                parameters["team"] = "Quality Crusaders"; // default team
        }

        // For update_work_item mapped from ado-move-to-sprint, rename "sprint" param to "iterationPath"
        if (mcpToolName == "update_work_item" && toolId.Equals("ado-move-to-sprint", StringComparison.OrdinalIgnoreCase))
        {
            if (parameters.TryGetValue("sprint", out var sprintVal) && !parameters.ContainsKey("iterationPath"))
            {
                parameters["iterationPath"] = sprintVal;
                parameters.Remove("sprint");
            }
        }
    }
}
