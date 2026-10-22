using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using Dapper;

namespace Ava.Api.Data;

public class PostgresTemplateRepository : ITemplateRepository
{
    private readonly DbConnectionFactory _db;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public PostgresTemplateRepository(DbConnectionFactory db) => _db = db;

    public async Task<List<TemplateDefinition>> GetAllAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<TemplateRow>("SELECT * FROM templates WHERE archived = false");
        return rows.Select(MapRow).ToList();
    }

    public async Task<List<TemplateDefinition>> GetArchivedAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<TemplateRow>("SELECT * FROM templates WHERE archived = true");
        return rows.Select(MapRow).ToList();
    }

    public async Task<TemplateDefinition?> GetByIdAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var row = await conn.QueryFirstOrDefaultAsync<TemplateRow>(
            "SELECT * FROM templates WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
        return row != null ? MapRow(row) : null;
    }

    public void Add(TemplateDefinition template)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        conn.Execute(@"
            INSERT INTO templates (id, description, fields)
            VALUES (@Id, @Description, @Fields::jsonb)
            ON CONFLICT (id) DO UPDATE SET
                description = EXCLUDED.description, fields = EXCLUDED.fields",
            new
            {
                template.Id,
                template.Description,
                Fields = JsonSerializer.Serialize(template.Fields, _jsonOpts)
            });
    }

    public bool Update(TemplateDefinition template)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        var affected = conn.Execute(@"
            UPDATE templates SET description = @Description, fields = @Fields::jsonb
            WHERE LOWER(id) = LOWER(@Id)",
            new
            {
                template.Id,
                template.Description,
                Fields = JsonSerializer.Serialize(template.Fields, _jsonOpts)
            });
        return affected > 0;
    }

    public bool Remove(string id)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        var affected = conn.Execute("DELETE FROM templates WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
        return affected > 0;
    }

    public IReadOnlyList<TemplateDefinition> Snapshot()
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        var rows = conn.Query<TemplateRow>("SELECT * FROM templates");
        return rows.Select(MapRow).ToList().AsReadOnly();
    }

    public void ReplaceAll(List<TemplateDefinition> templates)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM templates", transaction: tx);
        foreach (var template in templates)
        {
            conn.Execute(@"
                INSERT INTO templates (id, description, fields)
                VALUES (@Id, @Description, @Fields::jsonb)",
                new
                {
                    template.Id,
                    template.Description,
                    Fields = JsonSerializer.Serialize(template.Fields, _jsonOpts)
                }, transaction: tx);
        }
        tx.Commit();
    }

    public async Task ArchiveAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync("UPDATE templates SET archived = true WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
    }

    public async Task RestoreAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync("UPDATE templates SET archived = false WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
    }

    private static TemplateDefinition MapRow(TemplateRow row) => new()
    {
        Id = row.id,
        Description = row.description,
        Fields = !string.IsNullOrEmpty(row.fields)
            ? JsonSerializer.Deserialize<List<TemplateField>>(row.fields, _jsonOpts) ?? new()
            : new()
    };

    private class TemplateRow
    {
        public string id { get; set; } = "";
        public string description { get; set; } = "";
        public string fields { get; set; } = "[]";
    }
}
