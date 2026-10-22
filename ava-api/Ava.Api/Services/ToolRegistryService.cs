using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using OpenAI.Chat;

namespace Ava.Api.Services;

/// <summary>
/// Provides tool lookup and OpenAI ChatTool definitions for function calling.
/// Loads tools from PostgreSQL via IToolRepository.
/// </summary>
public class ToolRegistryService
{
    private List<ToolDefinition> _tools;
    private readonly IToolRepository _toolRepo;
    private readonly ILogger<ToolRegistryService> _logger;

    public ToolRegistryService(
        IToolRepository toolRepo,
        ILogger<ToolRegistryService> logger)
    {
        _toolRepo = toolRepo;
        _logger = logger;
        // Load synchronously on construction to maintain singleton initialization pattern
        _tools = _toolRepo.GetAllAsync().GetAwaiter().GetResult();
        _logger.LogInformation("Loaded {Count} tools from database", _tools.Count);
    }

    public IReadOnlyList<ToolDefinition> GetAll() => _tools.AsReadOnly();

    public async Task<List<ToolDefinition>> GetArchivedAsync() => await _toolRepo.GetArchivedAsync();

    public ToolDefinition? GetById(string id) =>
        _tools.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reloads tools from the database. Call after tool definitions change.</summary>
    public async Task ReloadAsync()
    {
        _tools = await _toolRepo.GetAllAsync();
        _logger.LogInformation("Reloaded {Count} tools from database", _tools.Count);
    }

    public async Task<ToolDefinition> CreateAsync(ToolDefinition tool)
    {
        var created = await _toolRepo.CreateAsync(tool);
        await ReloadAsync();
        return created;
    }

    public async Task<ToolDefinition> UpdateAsync(ToolDefinition tool)
    {
        var updated = await _toolRepo.UpdateAsync(tool);
        await ReloadAsync();
        return updated;
    }

    public async Task ArchiveAsync(string id)
    {
        await _toolRepo.ArchiveAsync(id);
        await ReloadAsync();
    }

    public async Task RestoreAsync(string id)
    {
        await _toolRepo.RestoreAsync(id);
        await ReloadAsync();
    }

    /// <summary>
    /// Get internal tools (kb_schema, kb_query, kb_search, start_workflow) as OpenAI ChatTool definitions.
    /// </summary>
    public List<ChatTool> GetInternalChatTools()
    {
        return _tools
            .Where(t => t.Internal)
            .Select(t => ChatTool.CreateFunctionTool(
                t.Id,
                t.Description,
                BinaryData.FromString(BuildJsonSchema(t))))
            .ToList();
    }

    /// <summary>
    /// Get only KB-related tools (kb_schema, kb_query, kb_search) — used by ai-analyze stage
    /// when it needs tool access but shouldn't start new workflows.
    /// </summary>
    public List<ChatTool> GetKBChatTools()
    {
        var kbToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "kb_schema", "kb_query", "kb_search" };
        return _tools
            .Where(t => t.Internal && kbToolIds.Contains(t.Id))
            .Select(t => ChatTool.CreateFunctionTool(
                t.Id,
                t.Description,
                BinaryData.FromString(BuildJsonSchema(t))))
            .ToList();
    }

    /// <summary>
    /// Get tools for the main agent loop. When no KBs exist, only start_workflow is returned.
    /// When KBs exist, all internal tools are returned.
    /// </summary>
    public List<ChatTool> GetAgentChatTools(bool hasActiveKBs)
    {
        return _tools
            .Where(t => t.Internal &&
                (hasActiveKBs || t.Id.Equals("start_workflow", StringComparison.OrdinalIgnoreCase)))
            .Select(t => ChatTool.CreateFunctionTool(
                t.Id,
                t.Description,
                BinaryData.FromString(BuildJsonSchema(t))))
            .ToList();
    }

    private static string BuildJsonSchema(ToolDefinition tool)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in tool.Parameters)
        {
            var prop = new Dictionary<string, object> { ["description"] = p.Description ?? p.Name };

            prop["type"] = p.Type switch
            {
                "number" or "integer" => p.Type,
                "array" => "array",
                "object" => "object",
                _ => "string"
            };

            if (p.Type == "array")
                prop["items"] = new Dictionary<string, string> { ["type"] = "string" };

            properties[p.Name] = prop;
            if (p.Required) required.Add(p.Name);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };

        return JsonSerializer.Serialize(schema);
    }
}
