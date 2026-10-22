using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ava.Api.Config;
using Ava.Api.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Ava.Api.Services;

/// <summary>
/// Generic MCP protocol client (Streamable HTTP / JSON-RPC 2025-03-26).
/// Supports any number of MCP services configured under Ava:Services with IsMcp=true.
/// Manages per-service per-user sessions and provides tool listing + execution.
/// </summary>
public class McpClientService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AvaSettings _settings;
    private readonly UserSessionService _userSession;
    private readonly ILogger<McpClientService> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Session cache: "serviceKey:patHash" → MCP session ID.
    // Scoped per service so multiple MCP servers don't share sessions.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessionCache = new();

    // Sentinel used when the MCP endpoint is stateless (no mcp-session-id header issued,
    // e.g. Context Forge gateway). Treated as "connected" but no session header is sent.
    private const string StatelessSession = "__stateless__";

    // Per-service resolver state: serviceKey → (toolNameMap, serverToolNames, toolSchemas).
    // Populated lazily on the first tools/list call per service.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServiceToolCache> _serviceCache = new();
    private readonly SemaphoreSlim _toolMapLock = new(1, 1);

    public McpClientService(
        IHttpClientFactory httpClientFactory,
        IOptions<AvaSettings> settings,
        UserSessionService userSession,
        ILogger<McpClientService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _userSession = userSession;
        _logger = logger;
    }

    // ── Service config helpers ─────────────────────────────────────────────────

    private ServiceEndpointSettings? GetMcpService(string serviceKey)
        => _settings.Services.TryGetValue(serviceKey, out var svc) && svc.Enabled && svc.IsMcp ? svc : null;

    private string GetMcpUrl(string serviceKey)
    {
        var baseUrl = GetMcpService(serviceKey)?.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "http://localhost:5020";

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            // Already targets an MCP endpoint (e.g. Context Forge URL with query string)
            if (uri.AbsolutePath.Contains("/mcp", StringComparison.OrdinalIgnoreCase))
                return baseUrl;
            var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/mcp" };
            return builder.Uri.ToString();
        }

        return baseUrl.Contains("/mcp", StringComparison.OrdinalIgnoreCase) || baseUrl.Contains('?')
            ? baseUrl
            : baseUrl.TrimEnd('/') + "/mcp";
    }

    private string? GetMcpBearerToken(string serviceKey)
    {
        var token = GetMcpService(serviceKey)?.BearerToken;
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    private string? GetToolPrefix(string serviceKey)
    {
        var prefix = GetMcpService(serviceKey)?.ToolPrefix;
        return string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim();
    }

    private static bool IsRealSession(string? sessionId)
        => !string.IsNullOrEmpty(sessionId) && sessionId != StatelessSession;

    // ── HTTP headers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Apply standard MCP headers. A configured bearer token (gateway auth) and the
    /// X-ADO-PAT header (upstream ADO auth forwarded by the gateway) are additive —
    /// Context Forge requires both: the bearer to authenticate to the gateway and the
    /// PAT to reach Azure DevOps behind it.
    /// </summary>
    private void ConfigureMcpHeaders(HttpClient client, string serviceKey, string? adoPat, string? sessionId)
    {
        var bearer = GetMcpBearerToken(serviceKey);
        if (bearer != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        if (!string.IsNullOrEmpty(adoPat))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-ADO-PAT", adoPat);

        if (IsRealSession(sessionId))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        // MCP Streamable HTTP spec requires Accept to include text/event-stream
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    }

    // ── Session management ─────────────────────────────────────────────────────

    private async Task<string?> InitializeSessionAsync(string serviceKey, string? adoPat)
    {
        var client = _httpClientFactory.CreateClient();
        var url = GetMcpUrl(serviceKey);

        var request = new JsonRpcRequest
        {
            Method = "initialize",
            Id = 1,
            Params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "ava-api", version = "1.0.0" }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(request, _jsonOpts), Encoding.UTF8, "application/json");
        ConfigureMcpHeaders(client, serviceKey, adoPat, null);

        try
        {
            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    _logger.LogWarning("MCP [{Service}] initialize unauthorized ({Status}) at {Url} — check the gateway bearer token",
                        serviceKey, response.StatusCode, url);
                else
                    _logger.LogWarning("MCP [{Service}] initialize failed: {Status} {Error} (URL: {Url})",
                        serviceKey, response.StatusCode, error, url);
                return null;
            }

            var sessionId = response.Headers.Contains("mcp-session-id")
                ? response.Headers.GetValues("mcp-session-id").FirstOrDefault()
                : null;

            if (IsRealSession(sessionId))
            {
                var notification = new JsonRpcRequest { Method = "notifications/initialized", Params = new { } };
                var notifContent = new StringContent(JsonSerializer.Serialize(notification, _jsonOpts), Encoding.UTF8, "application/json");
                var notifClient = _httpClientFactory.CreateClient();
                ConfigureMcpHeaders(notifClient, serviceKey, adoPat, sessionId);
                await notifClient.PostAsync(url, notifContent);

                _logger.LogInformation("MCP [{Service}] session initialized: {SessionId}", serviceKey, sessionId);
                return sessionId;
            }

            _logger.LogInformation("MCP [{Service}] initialized in stateless mode at {Url}", serviceKey, url);
            return StatelessSession;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MCP [{Service}] server unreachable at {Url}", serviceKey, url);
            return null;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("MCP [{Service}] initialize timed out at {Url}", serviceKey, url);
            return null;
        }
    }

    private async Task<(string? SessionId, string? AdoPat)> GetOrCreateSessionAsync(string serviceKey, string? userSessionId)
    {
        var adoPat = await _userSession.GetAdoPatAsync(userSessionId);
        var cacheKey = $"{serviceKey}:{HashPat(adoPat)}";

        if (_sessionCache.TryGetValue(cacheKey, out var existingSession))
            return (existingSession, adoPat);

        var sessionId = await InitializeSessionAsync(serviceKey, adoPat);
        if (sessionId != null)
            _sessionCache[cacheKey] = sessionId;

        return (sessionId, adoPat);
    }

    // ── Tool listing ───────────────────────────────────────────────────────────

    /// <summary>
    /// List available MCP tools from the given service, optionally filtered by domain.
    /// Domain filtering uses the tool name prefix conventions (e.g. "git_" for git tools).
    /// </summary>
    public async Task<List<McpToolDefinition>> ListToolsAsync(
        string serviceKey, string? userSessionId, IEnumerable<string>? domains = null)
    {
        var svc = GetMcpService(serviceKey);
        if (svc == null)
        {
            _logger.LogWarning("MCP [{Service}] not configured or disabled — skipping tool listing", serviceKey);
            return new();
        }

        var (sessionId, adoPat) = await GetOrCreateSessionAsync(serviceKey, userSessionId);
        if (sessionId == null)
        {
            _logger.LogWarning("MCP [{Service}] cannot list tools — no session", serviceKey);
            return new();
        }

        var request = new JsonRpcRequest { Method = "tools/list", Id = 2, Params = new { } };
        var responseJson = await SendMcpRequestAsync(serviceKey, sessionId, adoPat, request);
        if (responseJson == null)
        {
            // Session may have expired after a long agentic run — retry with a fresh session once.
            _logger.LogWarning("MCP [{Service}] tools/list got no response, retrying with new session...", serviceKey);
            var cacheKey = $"{serviceKey}:{HashPat(adoPat)}";
            _sessionCache.TryRemove(cacheKey, out _);
            (sessionId, adoPat) = await GetOrCreateSessionAsync(serviceKey, userSessionId);
            if (sessionId == null) return new();
            responseJson = await SendMcpRequestAsync(serviceKey, sessionId, adoPat, request);
            if (responseJson == null) return new();
        }

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return new();
            if (!result.TryGetProperty("tools", out var toolsArray)) return new();

            var tools = new List<McpToolDefinition>();
            foreach (var toolEl in toolsArray.EnumerateArray())
            {
                tools.Add(new McpToolDefinition
                {
                    Name = toolEl.GetProperty("name").GetString() ?? "",
                    Description = toolEl.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                    InputSchema = toolEl.TryGetProperty("inputSchema", out var schema) ? schema.GetRawText() : "{}"
                });
            }

            _logger.LogInformation("MCP [{Service}] listed {Count} tools", serviceKey, tools.Count);

            // Warm the per-service tool-name resolver cache.
            // IMPORTANT: must be built from the FULL list before any domain filtering,
            // because CallToolAsync uses these maps for name resolution and schema coercion.
            if (tools.Count > 0)
            {
                var toolPrefix = GetToolPrefix(serviceKey);
                var cache = new ServiceToolCache
                {
                    ToolNameMap = BuildToolMap(tools.Select(t => t.Name), toolPrefix),
                    ServerToolNames = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal),
                    ToolSchemas = BuildSchemaMap(tools, toolPrefix),
                    ToolDefinitions = tools.ToDictionary(
                        t => $"{serviceKey}-{t.Name.Replace('_', '-')}",
                        t => t,
                        StringComparer.OrdinalIgnoreCase)
                };
                _serviceCache[serviceKey] = cache;
            }

            // Apply domain filtering to the returned subset (maps stay full for call resolution).
            if (domains?.Any() == true)
            {
                var domainList = domains.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
                if (domainList.Count > 0)
                {
                    var toolPrefix = GetToolPrefix(serviceKey);
                    var filtered = tools.Where(t => ToolMatchesDomain(t.Name, domainList, toolPrefix)).ToList();
                    if (filtered.Count > 0)
                    {
                        tools = filtered;
                        _logger.LogInformation(
                            "MCP [{Service}] domain filter [{Domains}] → {Count} tools",
                            serviceKey, string.Join(", ", domainList), tools.Count);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "MCP [{Service}] domain filter [{Domains}] matched 0 tools — returning all {Count}",
                            serviceKey, string.Join(", ", domainList), tools.Count);
                    }
                }
            }

            return tools;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP [{Service}] failed to parse tools/list response", serviceKey);
            return new();
        }
    }

    // ── Tool calling ───────────────────────────────────────────────────────────

    /// <summary>
    /// Call an MCP tool by name on the specified service with the given parameters.
    /// Handles session re-initialization on failure (one retry).
    /// </summary>
    public async Task<(bool Success, string Result)> CallToolAsync(
        string serviceKey, string toolName, Dictionary<string, object?> parameters, string? userSessionId)
    {
        var svc = GetMcpService(serviceKey);
        if (svc == null)
            return (false, $"MCP service '{serviceKey}' is not configured or disabled.");

        var (sessionId, adoPat) = await GetOrCreateSessionAsync(serviceKey, userSessionId);
        if (sessionId == null)
        {
            _logger.LogWarning("MCP [{Service}] session unavailable for tool {Tool}, retrying...", serviceKey, toolName);
            var cacheKey = $"{serviceKey}:{HashPat(adoPat)}";
            _sessionCache.TryRemove(cacheKey, out _);
            await Task.Delay(500);
            (sessionId, adoPat) = await GetOrCreateSessionAsync(serviceKey, userSessionId);
            if (sessionId == null)
                return (false, $"MCP [{serviceKey}] session not available — unable to connect to {GetMcpUrl(serviceKey)}");
        }

        var resolvedToolName = await ResolveToolNameAsync(serviceKey, toolName, userSessionId);

        var (coercedParams, coerceError) = CoerceArgumentsToSchema(serviceKey, toolName, resolvedToolName, parameters);
        if (coerceError != null)
        {
            _logger.LogWarning("MCP [{Service}] arg coercion failed for {Tool}: {Error}", serviceKey, toolName, coerceError);
            return (false, coerceError);
        }
        parameters = coercedParams;

        var request = new JsonRpcRequest
        {
            Method = "tools/call",
            Id = Random.Shared.Next(100, 99999),
            Params = new { name = resolvedToolName, arguments = parameters }
        };

        var responseJson = await SendMcpRequestAsync(serviceKey, sessionId, adoPat, request);
        if (responseJson == null)
        {
            _logger.LogWarning("MCP [{Service}] call for {Tool} got no response, retrying with new session...", serviceKey, toolName);
            var cacheKey = $"{serviceKey}:{HashPat(adoPat)}";
            _sessionCache.TryRemove(cacheKey, out _);
            (sessionId, adoPat) = await GetOrCreateSessionAsync(serviceKey, userSessionId);
            if (sessionId == null)
                return (false, $"MCP [{serviceKey}] session expired and re-initialization failed");
            responseJson = await SendMcpRequestAsync(serviceKey, sessionId, adoPat, request);
            if (responseJson == null)
                return (false, $"MCP [{serviceKey}] call failed — no response after retry");
        }

        try
        {
            using var doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                var errorMsg = errorEl.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";
                return (false, $"MCP error: {errorMsg}");
            }

            if (!doc.RootElement.TryGetProperty("result", out var result))
                return (false, "MCP response missing 'result'");

            if (result.TryGetProperty("content", out var contentArray))
            {
                var sb = new StringBuilder();
                foreach (var item in contentArray.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : "text";
                    if (type == "text" && item.TryGetProperty("text", out var text))
                        sb.AppendLine(text.GetString());
                }
                var textResult = sb.ToString().TrimEnd();
                var isError = result.TryGetProperty("isError", out var isErr) && isErr.GetBoolean();
                return (!isError, textResult);
            }

            return (true, result.GetRawText());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP [{Service}] failed to parse tools/call response for {Tool}", serviceKey, toolName);
            return (false, $"Parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Convert MCP tool definitions to OpenAI ChatTool format for use in LLM tool loops.
    /// </summary>
    public static List<ChatTool> ConvertToChatTools(IEnumerable<McpToolDefinition> mcpTools)
        => mcpTools.Select(t => ChatTool.CreateFunctionTool(
            t.Name, t.Description, BinaryData.FromString(t.InputSchema))).ToList();

    // ── HTTP transport ─────────────────────────────────────────────────────────

    private async Task<string?> SendMcpRequestAsync(
        string serviceKey, string sessionId, string? adoPat, JsonRpcRequest request)
    {
        var client = _httpClientFactory.CreateClient();
        var url = GetMcpUrl(serviceKey);
        var content = new StringContent(JsonSerializer.Serialize(request, _jsonOpts), Encoding.UTF8, "application/json");

        ConfigureMcpHeaders(client, serviceKey, adoPat, sessionId);

        try
        {
            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    _logger.LogWarning("MCP [{Service}] {Method} unauthorized ({Status}) — check the gateway bearer token",
                        serviceKey, request.Method, response.StatusCode);
                else
                    _logger.LogWarning("MCP [{Service}] {Method} failed: {Status} {Body}",
                        serviceKey, request.Method, response.StatusCode, errorBody);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound && IsRealSession(sessionId))
                {
                    var cacheKey = $"{serviceKey}:{HashPat(adoPat)}";
                    _sessionCache.TryRemove(cacheKey, out _);
                }
                return null;
            }

            var rawBody = await response.Content.ReadAsStringAsync();
            return ParseSseResponse(rawBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP [{Service}] {Method} threw exception", serviceKey, request.Method);
            return null;
        }
    }

    // ── Tool-name resolution ───────────────────────────────────────────────────

    private async Task<string> ResolveToolNameAsync(string serviceKey, string requested, string? userSessionId)
    {
        await EnsureToolMapAsync(serviceKey, userSessionId);

        if (!_serviceCache.TryGetValue(serviceKey, out var cache) || cache.ServerToolNames.Count == 0)
            return requested;

        if (cache.ServerToolNames.Contains(requested))
            return requested;

        if (cache.ToolNameMap.TryGetValue(CanonicalKey(requested), out var actual) && !string.IsNullOrEmpty(actual))
        {
            if (!string.Equals(actual, requested, StringComparison.Ordinal))
                _logger.LogInformation("MCP [{Service}] resolved tool name {Requested} → {Actual}", serviceKey, requested, actual);
            return actual;
        }

        _logger.LogWarning("MCP [{Service}] could not resolve tool name {Requested}; using as-is", serviceKey, requested);
        return requested;
    }

    private async Task EnsureToolMapAsync(string serviceKey, string? userSessionId)
    {
        if (_serviceCache.ContainsKey(serviceKey)) return;

        await _toolMapLock.WaitAsync();
        try
        {
            if (_serviceCache.ContainsKey(serviceKey)) return;
            await ListToolsAsync(serviceKey, userSessionId);
        }
        finally
        {
            _toolMapLock.Release();
        }
    }

    private static Dictionary<string, string> BuildToolMap(IEnumerable<string> serverNames, string? toolPrefix)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in serverNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            AddToolKey(map, ambiguous, CanonicalKey(name), name);

            // Also index the prefix-stripped canonical key so callers can use bare names
            if (!string.IsNullOrEmpty(toolPrefix) &&
                name.StartsWith(toolPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = name[toolPrefix.Length..];
                if (!string.IsNullOrWhiteSpace(stripped))
                    AddToolKey(map, ambiguous, CanonicalKey(stripped), name);
            }
        }

        foreach (var key in ambiguous)
            map.Remove(key);

        return map;
    }

    private static void AddToolKey(Dictionary<string, string> map, HashSet<string> ambiguous, string key, string serverName)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (map.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing, serverName, StringComparison.Ordinal))
                ambiguous.Add(key);
        }
        else
        {
            map[key] = serverName;
        }
    }

    private static string CanonicalKey(string name)
        => name.Trim().ToLowerInvariant().Replace('-', '_');

    // ── Argument coercion ──────────────────────────────────────────────────────

    private static Dictionary<string, Dictionary<string, string>> BuildSchemaMap(
        IEnumerable<McpToolDefinition> tools, string? toolPrefix)
    {
        var schemas = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name)) continue;
            var typeMap = ParseSchemaTypes(tool.InputSchema);
            if (typeMap.Count == 0) continue;

            AddSchemaKey(schemas, CanonicalKey(tool.Name), typeMap);
            if (!string.IsNullOrEmpty(toolPrefix) &&
                tool.Name.StartsWith(toolPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = tool.Name[toolPrefix.Length..];
                if (!string.IsNullOrWhiteSpace(stripped))
                    AddSchemaKey(schemas, CanonicalKey(stripped), typeMap);
            }
        }
        return schemas;
    }

    private static void AddSchemaKey(
        Dictionary<string, Dictionary<string, string>> schemas, string key, Dictionary<string, string> typeMap)
    {
        if (!string.IsNullOrEmpty(key) && !schemas.ContainsKey(key))
            schemas[key] = typeMap;
    }

    /// <summary>
    /// Converts a JSON-Schema inputSchema string (from an MCP tool) to a list of
    /// <see cref="ToolParameter"/> objects suitable for display in the workflow builder.
    /// Maps JSON Schema types to AVA parameter types (string/number/boolean).
    /// All MCP parameters are treated as "body" parameters.
    /// </summary>
    public static List<ToolParameter> ParseSchemaToParameters(string inputSchema)
    {
        var result = new List<ToolParameter>();
        if (string.IsNullOrWhiteSpace(inputSchema)) return result;
        try
        {
            using var doc = JsonDocument.Parse(inputSchema);
            var root = doc.RootElement;

            if (!root.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Object)
                return result;

            var requiredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("required", out var reqArray)
                && reqArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in reqArray.EnumerateArray())
                    if (r.GetString() is { } s) requiredSet.Add(s);
            }

            foreach (var prop in props.EnumerateObject())
            {
                var jsonType = ExtractSchemaType(prop.Value);
                var avaType = jsonType switch
                {
                    "integer" or "number" => "number",
                    "boolean"             => "boolean",
                    _                     => "string",
                };

                string? description = null;
                if (prop.Value.TryGetProperty("description", out var descEl)
                    && descEl.ValueKind == JsonValueKind.String)
                    description = descEl.GetString();

                result.Add(new ToolParameter
                {
                    Name        = prop.Name,
                    Type        = avaType,
                    Required    = requiredSet.Contains(prop.Name),
                    Location    = "body",
                    Description = description,
                });
            }
        }
        catch (JsonException) { /* malformed schema — return whatever was built so far */ }
        return result;
    }

    /// <summary>Extract paramName → JSON type from a JSON-Schema "properties" object.</summary>
    private static Dictionary<string, string> ParseSchemaTypes(string inputSchema)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(inputSchema)) return map;
        try
        {
            using var doc = JsonDocument.Parse(inputSchema);
            if (!doc.RootElement.TryGetProperty("properties", out var props) ||
                props.ValueKind != JsonValueKind.Object)
                return map;

            foreach (var prop in props.EnumerateObject())
            {
                var type = ExtractSchemaType(prop.Value);
                if (type != null) map[prop.Name] = type;
            }
        }
        catch (JsonException) { }
        return map;
    }

    private static string? ExtractSchemaType(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        if (el.TryGetProperty("type", out var typeEl))
        {
            if (typeEl.ValueKind == JsonValueKind.String)
                return typeEl.GetString();
            if (typeEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in typeEl.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String && t.GetString() is { } ts && ts != "null")
                        return ts;
            }
        }

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (el.TryGetProperty(keyword, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in arr.EnumerateArray())
                {
                    var t = ExtractSchemaType(opt);
                    if (t != null && t != "null") return t;
                }
            }
        }
        return null;
    }

    private (Dictionary<string, object?> Args, string? Error) CoerceArgumentsToSchema(
        string serviceKey, string requested, string resolved, Dictionary<string, object?> parameters)
    {
        if (parameters.Count == 0) return (parameters, null);

        if (!_serviceCache.TryGetValue(serviceKey, out var cache) || cache.ToolSchemas == null)
            return (parameters, null);

        if (!cache.ToolSchemas.TryGetValue(CanonicalKey(resolved), out var schema) &&
            !cache.ToolSchemas.TryGetValue(CanonicalKey(requested), out schema))
            return (parameters, null);

        foreach (var key in parameters.Keys.ToList())
        {
            if (!schema.TryGetValue(key, out var type)) continue;
            var (value, error) = CoerceValue(parameters[key], type, key);
            if (error != null) return (parameters, error);
            parameters[key] = value;
        }
        return (parameters, null);
    }

    private static (object? Value, string? Error) CoerceValue(object? raw, string type, string key)
    {
        string? s = raw as string;
        if (s == null && raw is JsonElement je && je.ValueKind == JsonValueKind.String)
            s = je.GetString();
        if (string.IsNullOrEmpty(s)) return (raw, null);

        switch (type)
        {
            case "integer":
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return (l, null);
                return (raw, $"Invalid parameter '{key}': expected integer, got '{s}'.");

            case "number":
                if (decimal.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out var d))
                    return (d, null);
                return (raw, $"Invalid parameter '{key}': expected number, got '{s}'.");

            case "boolean":
                if (bool.TryParse(s, out var b)) return (b, null);
                return (raw, $"Invalid parameter '{key}': expected boolean, got '{s}'.");

            case "array":
            case "object":
                var trimmed = s.TrimStart();
                var matches = (type == "array" && trimmed.StartsWith('['))
                           || (type == "object" && trimmed.StartsWith('{'));
                if (matches)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(s);
                        return (doc.RootElement.Clone(), null);
                    }
                    catch (JsonException) { }
                }
                return (raw, null);

            default:
                return (raw, null);
        }
    }

    // ── SSE response parsing ───────────────────────────────────────────────────

    /// <summary>
    /// MCP Streamable HTTP returns responses in SSE format: "event: message\ndata: {...}\n\n".
    /// If the body is already plain JSON (starts with '{'), returns it directly.
    /// Only the last data line is used — progress events before the final result are discarded.
    /// </summary>
    private static string? ParseSseResponse(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return null;

        var trimmed = rawBody.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return trimmed;

        string? lastData = null;
        foreach (var line in rawBody.Split('\n'))
        {
            var l = line.TrimStart();
            if (l.StartsWith("data:"))
                lastData = l[5..].Trim();
        }
        return lastData;
    }

    // ── Utilities ──────────────────────────────────────────────────────────────

    private static string HashPat(string? pat)
    {
        if (string.IsNullOrEmpty(pat)) return "__no_pat__";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pat));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// Returns all service keys that are configured as MCP services and currently enabled.
    /// Used by ToolsController to aggregate live tools across all MCP servers.
    /// </summary>
    public IEnumerable<string> GetEnabledMcpServiceKeys()
        => _settings.Services
            .Where(kvp => kvp.Value.IsMcp && kvp.Value.Enabled)
            .Select(kvp => kvp.Key);

    /// <summary>
    /// Returns true if a tool name matches any of the given domain filters.
    /// Normalizes both the tool name and domain string, optionally stripping a service-specific
    /// gateway tool prefix before matching.
    /// Supports exact tool name matches (e.g. "get_work_item") and domain prefix matches
    /// (e.g. "git" matches "git_status", "git_commit", etc.).
    /// </summary>
    private static bool ToolMatchesDomain(string toolName, IEnumerable<string> domains, string? toolPrefix = null)
    {
        var normalized = toolName.Replace("-", "_").ToLowerInvariant();

        // Strip optional gateway prefix (e.g. "ado-mcp-server-") for prefix-based matching
        if (!string.IsNullOrEmpty(toolPrefix))
        {
            var prefixNorm = toolPrefix.Replace("-", "_").ToLowerInvariant();
            if (normalized.StartsWith(prefixNorm))
                normalized = normalized[prefixNorm.Length..];
        }

        return domains.Any(d =>
        {
            var dn = d.Replace("-", "_").Replace(" ", "_").ToLowerInvariant();
            return normalized == dn                  // exact tool name match
                || normalized.StartsWith(dn + "_"); // domain prefix match (e.g. "git_*")
        });
    }

    // ── Inner types ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns cached tool definitions across all services, keyed by the AVA tool ID
    /// (e.g. "ado-mcp-create-work-item"). Populated lazily on the first tools/list call
    /// per service. Returns an empty dictionary if no tools have been listed yet.
    /// </summary>
    public IReadOnlyDictionary<string, McpToolDefinition> GetCachedToolDefinitions()
    {
        var result = new Dictionary<string, McpToolDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (serviceKey, cache) in _serviceCache)
        {
            foreach (var (avaId, def) in cache.ToolDefinitions)
                result.TryAdd(avaId, def);
        }
        return result;
    }

    private class ServiceToolCache
    {
        public Dictionary<string, string> ToolNameMap { get; init; } = new();
        public HashSet<string> ServerToolNames { get; init; } = new();
        public Dictionary<string, Dictionary<string, string>> ToolSchemas { get; init; } = new();
        /// <summary>Keyed by AVA tool ID (e.g. "ado-mcp-create-work-item").</summary>
        public Dictionary<string, McpToolDefinition> ToolDefinitions { get; init; } = new();
    }

    // ── JSON-RPC models ───────────────────────────────────────────────────────

    private class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Id { get; set; }

        [JsonPropertyName("params")]
        public object? Params { get; set; }
    }
}

/// <summary>
/// Represents a tool definition returned by an MCP server's tools/list endpoint.
/// </summary>
public class McpToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string InputSchema { get; set; } = "{}";
}
