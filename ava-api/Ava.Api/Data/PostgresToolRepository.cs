using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using Dapper;

namespace Ava.Api.Data;

public class PostgresToolRepository : IToolRepository
{
    private readonly DbConnectionFactory _db;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public PostgresToolRepository(DbConnectionFactory db) => _db = db;

    public async Task<List<ToolDefinition>> GetAllAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<ToolRow>(
            "SELECT id, service, route, http_method, description, parameters, requires_confirmation, internal, response_format, store_as_kb, content_type, archived FROM tools WHERE archived = false");
        return rows.Select(MapRow).ToList();
    }

    public async Task<List<ToolDefinition>> GetArchivedAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<ToolRow>(
            "SELECT id, service, route, http_method, description, parameters, requires_confirmation, internal, response_format, store_as_kb, content_type, archived FROM tools WHERE archived = true");
        return rows.Select(MapRow).ToList();
    }

    public async Task<ToolDefinition?> GetByIdAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var row = await conn.QueryFirstOrDefaultAsync<ToolRow>(
            "SELECT id, service, route, http_method, description, parameters, requires_confirmation, internal, response_format, store_as_kb, content_type, archived FROM tools WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
        return row != null ? MapRow(row) : null;
    }

    public async Task<ToolDefinition> CreateAsync(ToolDefinition tool)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO tools (id, service, route, http_method, description, parameters, requires_confirmation, internal, response_format, store_as_kb, content_type, archived)
            VALUES (@Id, @Service, @Route, @HttpMethod, @Description, @Parameters::jsonb, @RequiresConfirmation, @Internal, @ResponseFormat, @StoreAsKb::jsonb, @ContentType, false)",
            new
            {
                tool.Id,
                tool.Service,
                tool.Route,
                tool.HttpMethod,
                tool.Description,
                Parameters = JsonSerializer.Serialize(tool.Parameters, _jsonOpts),
                tool.RequiresConfirmation,
                Internal = tool.Internal,
                tool.ResponseFormat,
                StoreAsKb = tool.StoreAsKB != null ? JsonSerializer.Serialize(tool.StoreAsKB, _jsonOpts) : null,
                ContentType = tool.ContentType ?? "json"
            });
        return tool;
    }

    public async Task<ToolDefinition> UpdateAsync(ToolDefinition tool)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync(@"
            UPDATE tools SET service = @Service, route = @Route, http_method = @HttpMethod,
                description = @Description, parameters = @Parameters::jsonb,
                requires_confirmation = @RequiresConfirmation, internal = @Internal,
                response_format = @ResponseFormat, store_as_kb = @StoreAsKb::jsonb,
                content_type = @ContentType
            WHERE LOWER(id) = LOWER(@Id)",
            new
            {
                tool.Id,
                tool.Service,
                tool.Route,
                tool.HttpMethod,
                tool.Description,
                Parameters = JsonSerializer.Serialize(tool.Parameters, _jsonOpts),
                tool.RequiresConfirmation,
                Internal = tool.Internal,
                tool.ResponseFormat,
                StoreAsKb = tool.StoreAsKB != null ? JsonSerializer.Serialize(tool.StoreAsKB, _jsonOpts) : null,
                ContentType = tool.ContentType ?? "json"
            });
        return tool;
    }

    public async Task ArchiveAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync("UPDATE tools SET archived = true WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
    }

    public async Task RestoreAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync("UPDATE tools SET archived = false WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
    }

    private static ToolDefinition MapRow(ToolRow row) => new()
    {
        Id = row.id,
        Service = row.service,
        Route = row.route,
        HttpMethod = row.http_method,
        Description = row.description,
        Parameters = !string.IsNullOrEmpty(row.parameters)
            ? JsonSerializer.Deserialize<List<ToolParameter>>(row.parameters, _jsonOpts) ?? new()
            : new(),
        RequiresConfirmation = row.requires_confirmation,
        Internal = row.@internal,
        ResponseFormat = row.response_format,
        StoreAsKB = !string.IsNullOrEmpty(row.store_as_kb)
            ? JsonSerializer.Deserialize<StoreAsKBConfig>(row.store_as_kb, _jsonOpts)
            : null,
        ContentType = row.content_type ?? "json"
    };

    private class ToolRow
    {
        public string id { get; set; } = "";
        public string service { get; set; } = "";
        public string route { get; set; } = "";
        public string http_method { get; set; } = "GET";
        public string? description { get; set; }
        public string parameters { get; set; } = "[]";
        public bool requires_confirmation { get; set; }
        public bool @internal { get; set; }
        public string? response_format { get; set; }
        public string? store_as_kb { get; set; }
        public string? content_type { get; set; }
        public bool archived { get; set; }
    }
}
