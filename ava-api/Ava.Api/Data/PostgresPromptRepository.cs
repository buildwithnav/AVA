using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using Dapper;

namespace Ava.Api.Data;

public class PostgresPromptRepository : IPromptRepository
{
    private readonly DbConnectionFactory _db;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public PostgresPromptRepository(DbConnectionFactory db) => _db = db;

    public async Task<List<PromptDefinition>> GetAllAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<PromptRow>("SELECT * FROM prompts WHERE archived = false ORDER BY id");
        return rows.Select(MapRow).ToList();
    }

    public async Task<List<PromptDefinition>> GetArchivedAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<PromptRow>("SELECT * FROM prompts WHERE archived = true ORDER BY id");
        return rows.Select(MapRow).ToList();
    }

    public async Task<List<PromptDefinition>> GetByDomainAsync(string domain)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<PromptRow>(
            "SELECT * FROM prompts WHERE LOWER(domain) = LOWER(@Domain) AND archived = false ORDER BY id", new { Domain = domain });
        return rows.Select(MapRow).ToList();
    }

    public async Task<PromptDefinition?> GetByIdAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var row = await conn.QueryFirstOrDefaultAsync<PromptRow>(
            "SELECT * FROM prompts WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
        return row != null ? MapRow(row) : null;
    }

    public async Task<PromptDefinition> CreateAsync(PromptDefinition prompt)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var stagesJson = JsonSerializer.Serialize(prompt.Stages ?? new List<WorkflowStage>(), _jsonOpts);

        await conn.ExecuteAsync("""
            INSERT INTO prompts (id, domain, description, sample_prompt, context_tags, system_prompt, stages, pii_check, preserve_context)
            VALUES (@Id, @Domain, @Description, @SamplePrompt, @ContextTags, @SystemPrompt, @Stages::jsonb, @PiiCheck, @PreserveContext)
            """,
            new
            {
                prompt.Id,
                prompt.Domain,
                prompt.Description,
                prompt.SamplePrompt,
                ContextTags = prompt.ContextTags?.ToArray() ?? Array.Empty<string>(),
                prompt.SystemPrompt,
                Stages = stagesJson,
                prompt.PiiCheck,
                prompt.PreserveContext
            });

        return prompt;
    }

    public async Task<PromptDefinition?> UpdateAsync(string id, PromptDefinition prompt)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var stagesJson = JsonSerializer.Serialize(prompt.Stages ?? new List<WorkflowStage>(), _jsonOpts);

        var affected = await conn.ExecuteAsync("""
            UPDATE prompts
            SET id              = @NewId,
                domain          = @Domain,
                description     = @Description,
                sample_prompt   = @SamplePrompt,
                context_tags    = @ContextTags,
                system_prompt   = @SystemPrompt,
                stages          = @Stages::jsonb,
                pii_check       = @PiiCheck,
                preserve_context= @PreserveContext
            WHERE LOWER(id) = LOWER(@Id)
            """,
            new
            {
                Id = id,
                NewId = prompt.Id,
                prompt.Domain,
                prompt.Description,
                prompt.SamplePrompt,
                ContextTags = prompt.ContextTags?.ToArray() ?? Array.Empty<string>(),
                prompt.SystemPrompt,
                Stages = stagesJson,
                prompt.PiiCheck,
                prompt.PreserveContext
            });

        return affected > 0 ? prompt : null;
    }

    public async Task ArchiveAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync("UPDATE prompts SET archived = true WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
    }

    public async Task RestoreAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync("UPDATE prompts SET archived = false WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
    }

    private static PromptDefinition MapRow(PromptRow row) => new()
    {
        Id = row.id,
        Domain = row.domain,
        Description = row.description,
        SamplePrompt = row.sample_prompt,
        ContextTags = row.context_tags?.ToList() ?? new(),
        SystemPrompt = row.system_prompt,
        Stages = !string.IsNullOrEmpty(row.stages)
            ? JsonSerializer.Deserialize<List<WorkflowStage>>(row.stages, _jsonOpts)
            : new(),
        PiiCheck = row.pii_check,
        PreserveContext = row.preserve_context
    };

    private class PromptRow
    {
        public string id { get; set; } = "";
        public string domain { get; set; } = "";
        public string description { get; set; } = "";
        public string? sample_prompt { get; set; }
        public string[] context_tags { get; set; } = Array.Empty<string>();
        public string system_prompt { get; set; } = "";
        public string stages { get; set; } = "[]";
        public bool pii_check { get; set; }
        public bool preserve_context { get; set; }
    }
}
