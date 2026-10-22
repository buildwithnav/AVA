using Ava.Api.Config;
using Ava.Api.Models;
using Ava.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Ava.Api.Controllers;

// ── Health ────────────────────────────────────────────────────────────────────

[ApiController]
public class HealthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly AuthSettings _authSettings;

    public HealthController(IAuthService authService, IOptions<AuthSettings> authSettings)
    {
        _authService = authService;
        _authSettings = authSettings.Value;
    }

    [HttpGet("health")]
    public IActionResult Health([FromServices] ISessionStore sessionStore, [FromServices] IConnectionMultiplexer? redis = null)
    {
        var headers = Request.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToString());

        var cookies = new Dictionary<string, string>
        {
            ["cds-at"] = Request.Cookies["cds-at"] ?? "",
            ["cds-rt"] = Request.Cookies["cds-rt"] ?? ""
        };

        foreach (var cookie in Request.Cookies)
        {
            if (!cookies.ContainsKey(cookie.Key))
                cookies[cookie.Key] = cookie.Value;
        }

        var redisStatus = redis != null ? (redis.IsConnected ? "connected" : "disconnected") : "not_configured";
        var storeType = sessionStore is RedisSessionStore ? "redis" : "in_memory";

        return Ok(new
        {
            status = "healthy",
            service = "ava-api",
            timestamp = DateTime.UtcNow,
            authMode = _authSettings.UseCookieForwarding ? "cookie-forwarding" : "password-auth",
            sessionStore = storeType,
            redis = redisStatus,
            headers,
            cookies
        });
    }

    /// <summary>
    /// Tests cookie forwarding by calling a CDS endpoint and returning diagnostic info.
    /// Use this to verify cookie forwarding is working on server deployment.
    /// </summary>
    [HttpGet("health/test-auth")]
    public async Task<IActionResult> TestAuth()
    {
        var incomingCookies = new Dictionary<string, string>
        {
            ["cds-at"] = Request.Cookies["cds-at"] ?? "(not present)",
            ["cds-rt"] = Request.Cookies["cds-rt"] ?? "(not present)"
        };

        var result = new
        {
            timestamp = DateTime.UtcNow,
            authMode = _authSettings.UseCookieForwarding ? "cookie-forwarding" : "password-auth",
            useCookieForwarding = _authSettings.UseCookieForwarding,
            incomingCookies,
            testEndpoint = "https://cdstest.abbott.com/core/api/filter_mgmt/so2/global",
            testResult = (object?)null,
            error = (string?)null,
            forwardedCookies = (object?)null
        };

        try
        {
            // Create authenticated client (will use cookie forwarding or password auth based on config)
            var client = await _authService.CreateAuthenticatedClientAsync("https://cdstest.abbott.com");

            // Get the cookies that will be sent
            var handler = (HttpClientHandler?)typeof(HttpMessageInvoker)
                .GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .GetValue(client);

            var cookieContainer = handler?.CookieContainer;
            var forwardedCookies = new Dictionary<string, string>();
            if (cookieContainer != null)
            {
                var cookies = cookieContainer.GetCookies(new Uri("https://cdstest.abbott.com"));
                foreach (System.Net.Cookie cookie in cookies)
                {
                    forwardedCookies[cookie.Name] = cookie.Value.Length > 20 
                        ? cookie.Value[..20] + "..." 
                        : cookie.Value;
                }
            }

            // Make a test call to the study endpoint
            var response = await client.GetAsync("/core/api/filter_mgmt/so2/global");
            var content = await response.Content.ReadAsStringAsync();

            return Ok(new
            {
                timestamp = DateTime.UtcNow,
                authMode = _authSettings.UseCookieForwarding ? "cookie-forwarding" : "password-auth",
                useCookieForwarding = _authSettings.UseCookieForwarding,
                incomingCookies,
                testEndpoint = "https://cdstest.abbott.com/core/api/filter_mgmt/so2/global",
                forwardedCookies,
                testResult = new
                {
                    statusCode = (int)response.StatusCode,
                    status = response.StatusCode.ToString(),
                    contentLength = content.Length,
                    contentPreview = content.Length > 500 ? content[..500] + "..." : content
                }
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                timestamp = DateTime.UtcNow,
                authMode = _authSettings.UseCookieForwarding ? "cookie-forwarding" : "password-auth",
                useCookieForwarding = _authSettings.UseCookieForwarding,
                incomingCookies,
                testEndpoint = "https://cdstest.abbott.com/core/api/filter_mgmt/so2/global",
                error = ex.Message,
                errorType = ex.GetType().Name,
                innerError = ex.InnerException?.Message
            });
        }
    }
}

// ── Tools ─────────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/tools")]
public class ToolsController : ControllerBase
{
    private readonly ToolRegistryService _registry;
    private readonly McpClientService _mcp;

    public ToolsController(ToolRegistryService registry, McpClientService mcp)
    {
        _registry = registry;
        _mcp = mcp;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool includeMcp = true)
    {
        var dbTools = _registry.GetAll();

        if (!includeMcp)
            return Ok(new { count = dbTools.Count, tools = dbTools });

        // Merge DB tools + live MCP tools from all enabled MCP services
        var dbToolRoutes = new HashSet<string>(
            dbTools.Where(t => t.HttpMethod == "MCP")
                   .Select(t => t.Route.StartsWith("mcp:") ? t.Route[4..] : t.Route),
            StringComparer.OrdinalIgnoreCase);

        var merged = new List<object>(dbTools.Select(t => (object)t));

        foreach (var serviceKey in _mcp.GetEnabledMcpServiceKeys())
        {
            var mcpTools = await _mcp.ListToolsAsync(serviceKey, null);
            foreach (var tool in mcpTools)
            {
                if (dbToolRoutes.Contains(tool.Name)) continue;
                merged.Add(new
                {
                    id = $"{serviceKey}-{tool.Name.Replace('_', '-')}",
                    service = serviceKey,
                    route = $"mcp:{tool.Name}",
                    httpMethod = "MCP",
                    description = tool.Description,
                    parameters = McpClientService.ParseSchemaToParameters(tool.InputSchema),
                    requiresConfirmation = false,
                    @internal = false,
                });
            }
        }

        return Ok(new { count = merged.Count, tools = merged });
    }

    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        var tool = _registry.GetById(id);
        return tool == null ? NotFound(new { error = $"Tool '{id}' not found" }) : Ok(tool);
    }

    /// <summary>
    /// Returns the raw MCP tool names available across all enabled MCP services.
    /// Used by the workflow builder UI to populate the agentic stage tool/domain selector.
    /// </summary>
    [HttpGet("mcp-names")]
    public async Task<IActionResult> GetMcpNames()
    {
        var names = new List<string>();
        foreach (var serviceKey in _mcp.GetEnabledMcpServiceKeys())
        {
            var tools = await _mcp.ListToolsAsync(serviceKey, null);
            names.AddRange(tools.Select(t => t.Name));
        }
        return Ok(names.Distinct().OrderBy(n => n).ToList());
    }

    [HttpGet("archived")]
    public async Task<IActionResult> GetArchived()
    {
        var tools = await _registry.GetArchivedAsync();
        return Ok(new { count = tools.Count, tools });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateToolRequest request)
    {
        var tool = new ToolDefinition
        {
            Id = request.Id,
            Service = request.Service,
            Route = request.Route,
            HttpMethod = request.HttpMethod,
            Description = request.Description,
            Parameters = request.Parameters ?? new(),
            RequiresConfirmation = request.RequiresConfirmation,
            Internal = false,
            ContentType = "json"
        };

        var created = await _registry.CreateAsync(tool);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateToolRequest request)
    {
        var existing = _registry.GetById(id);
        if (existing == null)
            return NotFound(new { error = $"Tool '{id}' not found" });

        existing.Service = request.Service ?? existing.Service;
        existing.Route = request.Route ?? existing.Route;
        existing.HttpMethod = request.HttpMethod ?? existing.HttpMethod;
        existing.Description = request.Description ?? existing.Description;
        existing.Parameters = request.Parameters ?? existing.Parameters;
        existing.RequiresConfirmation = request.RequiresConfirmation ?? existing.RequiresConfirmation;

        var updated = await _registry.UpdateAsync(existing);
        return Ok(updated);
    }

    [HttpPut("{id}/archive")]
    public async Task<IActionResult> Archive(string id)
    {
        var existing = _registry.GetById(id);
        if (existing == null)
            return NotFound(new { error = $"Tool '{id}' not found" });

        await _registry.ArchiveAsync(id);
        return Ok(new { archived = true, id });
    }

    [HttpPut("{id}/restore")]
    public async Task<IActionResult> Restore(string id)
    {
        await _registry.RestoreAsync(id);
        return Ok(new { restored = true, id });
    }
}

public class CreateToolRequest
{
    public string Id { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = "GET";
    public string? Description { get; set; }
    public List<ToolParameter>? Parameters { get; set; }
    public bool RequiresConfirmation { get; set; }
}

public class UpdateToolRequest
{
    public string? Service { get; set; }
    public string? Route { get; set; }
    public string? HttpMethod { get; set; }
    public string? Description { get; set; }
    public List<ToolParameter>? Parameters { get; set; }
    public bool? RequiresConfirmation { get; set; }
}
