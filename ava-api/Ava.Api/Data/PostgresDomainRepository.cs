using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using Dapper;

namespace Ava.Api.Data;

/// <summary>
/// Domains, their children (e.g. individual studies under "Clinical Studies"), and the
/// AVA-native grants that gate children — folded into one repository since children are
/// full <c>domains</c> rows (self-referencing <c>parent_id</c>), not a separate table.
/// </summary>
public class PostgresDomainRepository : IDomainRepository
{
    private readonly DbConnectionFactory _db;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public PostgresDomainRepository(DbConnectionFactory db) => _db = db;

    public async Task<List<DomainDefinition>> GetAllAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<DomainRow>(
            "SELECT * FROM domains WHERE enabled = true AND parent_id IS NULL");
        return rows.Select(MapRow).ToList();
    }

    public async Task<List<DomainDefinition>> GetAllIncludingArchivedAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<DomainRow>("SELECT * FROM domains WHERE parent_id IS NULL");
        return rows.Select(MapRow).ToList();
    }

    public async Task<DomainDefinition?> GetByIdAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var row = await conn.QueryFirstOrDefaultAsync<DomainRow>(
            "SELECT * FROM domains WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
        return row != null ? MapRow(row) : null;
    }

    public async Task<List<DomainDefinition>> GetChildrenAsync(string parentId, bool includeArchived = false)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var sql = includeArchived
            ? "SELECT * FROM domains WHERE LOWER(parent_id) = LOWER(@ParentId) ORDER BY name"
            : "SELECT * FROM domains WHERE LOWER(parent_id) = LOWER(@ParentId) AND enabled = true ORDER BY name";
        var rows = await conn.QueryAsync<DomainRow>(sql, new { ParentId = parentId });
        return rows.Select(MapRow).ToList();
    }

    public async Task AddAsync(DomainDefinition domain)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO domains (id, name, description, owner, enabled, kb_ids, suggestions, welcome_message,
                                  agentic_instruction, kb_prompt_mode, module_key, parent_id, external_ref)
            VALUES (@Id, @Name, @Description, @Owner, @Enabled, @KbIds::jsonb, @Suggestions::jsonb, @WelcomeMessage,
                    @AgenticInstruction, @KbPromptMode, @ModuleKey, @ParentId, @ExternalRef)",
            ToParams(domain));
    }

    public async Task<bool> UpdateAsync(DomainDefinition domain)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var affected = await conn.ExecuteAsync(@"
            UPDATE domains SET name = @Name, description = @Description, owner = @Owner,
                enabled = @Enabled, kb_ids = @KbIds::jsonb, suggestions = @Suggestions::jsonb,
                welcome_message = @WelcomeMessage, agentic_instruction = @AgenticInstruction,
                kb_prompt_mode = @KbPromptMode, module_key = @ModuleKey, parent_id = @ParentId,
                external_ref = @ExternalRef
            WHERE LOWER(id) = LOWER(@Id)",
            ToParams(domain));
        return affected > 0;
    }

    public async Task<bool> ArchiveAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var affected = await conn.ExecuteAsync(
            "UPDATE domains SET enabled = false WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
        return affected > 0;
    }

    public async Task<bool> RestoreAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var affected = await conn.ExecuteAsync(
            "UPDATE domains SET enabled = true WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        // Grants reference this row via domain_id/parent_id FKs with ON DELETE CASCADE — no
        // need to clear them explicitly first.
        var affected = await conn.ExecuteAsync(
            "DELETE FROM domains WHERE LOWER(id) = LOWER(@Id)", new { Id = id });
        return affected > 0;
    }

    // ── Grants ────────────────────────────────────────────────────────────────

    private const string SelectGrantSql = @"
        SELECT id, parent_id AS ParentId, domain_id AS DomainId,
               principal_type AS PrincipalType, principal_id AS PrincipalId,
               browse, update_access AS ""Update"", insert_access AS ""Insert"", delete_access AS ""Delete""
        FROM domain_grants";

    public async Task<List<DomainGrant>> GetGrantsForPrincipalAsync(string principalId, IEnumerable<string> groupIds)
    {
        var groups = groupIds?.ToArray() ?? Array.Empty<string>();
        await using var conn = await _db.OpenConnectionAsync();
        var sql = $@"{SelectGrantSql}
            WHERE (principal_type = 'user'  AND LOWER(principal_id) = LOWER(@PrincipalId))
               OR (principal_type = 'group' AND LOWER(principal_id) = ANY(@Groups))";
        var rows = await conn.QueryAsync<DomainGrant>(sql,
            new { PrincipalId = principalId, Groups = groups.Select(g => g.ToLowerInvariant()).ToArray() });
        return rows.ToList();
    }

    public async Task<List<DomainGrant>> GetGrantsByParentAsync(string parentId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var sql = $@"{SelectGrantSql} WHERE LOWER(parent_id) = LOWER(@ParentId) ORDER BY principal_id";
        var rows = await conn.QueryAsync<DomainGrant>(sql, new { ParentId = parentId });
        return rows.ToList();
    }

    public async Task UpsertGrantAsync(DomainGrant grant)
    {
        if (string.IsNullOrWhiteSpace(grant.Id))
            grant.Id = Guid.NewGuid().ToString("n");

        await using var conn = await _db.OpenConnectionAsync();

        // The UNIQUE constraint can't ON CONFLICT-match a NULL domain_id (wildcard grants),
        // since Postgres treats NULLs as distinct — upsert manually instead.
        if (grant.DomainId == null)
        {
            var existingId = await conn.ExecuteScalarAsync<string?>(
                @"SELECT id FROM domain_grants
                  WHERE LOWER(parent_id) = LOWER(@ParentId) AND domain_id IS NULL
                    AND principal_type = @PrincipalType AND LOWER(principal_id) = LOWER(@PrincipalId)",
                new { grant.ParentId, grant.PrincipalType, grant.PrincipalId });
            if (existingId != null)
                grant.Id = existingId;
        }

        await conn.ExecuteAsync(@"
            INSERT INTO domain_grants (id, parent_id, domain_id, principal_type, principal_id,
                                        browse, update_access, insert_access, delete_access)
            VALUES (@Id, @ParentId, @DomainId, @PrincipalType, @PrincipalId,
                    @Browse, @Update, @Insert, @Delete)
            ON CONFLICT (id) DO UPDATE
                SET browse = EXCLUDED.browse, update_access = EXCLUDED.update_access,
                    insert_access = EXCLUDED.insert_access, delete_access = EXCLUDED.delete_access",
            new
            {
                grant.Id,
                grant.ParentId,
                grant.DomainId,
                grant.PrincipalType,
                grant.PrincipalId,
                grant.Browse,
                grant.Update,
                grant.Insert,
                grant.Delete
            });
    }

    public async Task<bool> RemoveGrantAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var affected = await conn.ExecuteAsync("DELETE FROM domain_grants WHERE id = @Id", new { Id = id });
        return affected > 0;
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static object ToParams(DomainDefinition domain) => new
    {
        domain.Id,
        domain.Name,
        domain.Description,
        domain.Owner,
        domain.Enabled,
        KbIds = JsonSerializer.Serialize(domain.KnowledgeBaseIds, _jsonOpts),
        Suggestions = JsonSerializer.Serialize(domain.Suggestions, _jsonOpts),
        domain.WelcomeMessage,
        domain.AgenticInstruction,
        domain.KbPromptMode,
        domain.ModuleKey,
        domain.ParentId,
        domain.ExternalRef
    };

    private static DomainDefinition MapRow(DomainRow row) => new()
    {
        Id = row.id,
        Name = row.name,
        Description = row.description,
        Owner = row.owner,
        Enabled = row.enabled,
        KnowledgeBaseIds = !string.IsNullOrEmpty(row.kb_ids)
            ? JsonSerializer.Deserialize<List<string>>(row.kb_ids, _jsonOpts) ?? new()
            : new(),
        Suggestions = !string.IsNullOrEmpty(row.suggestions)
            ? JsonSerializer.Deserialize<List<string>>(row.suggestions, _jsonOpts) ?? new()
            : new(),
        WelcomeMessage = row.welcome_message,
        AgenticInstruction = row.agentic_instruction,
        KbPromptMode = row.kb_prompt_mode,
        ModuleKey = row.module_key,
        ParentId = row.parent_id,
        ExternalRef = row.external_ref
    };

    private class DomainRow
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string description { get; set; } = "";
        public string owner { get; set; } = "";
        public bool enabled { get; set; }
        public string kb_ids { get; set; } = "[]";
        public string suggestions { get; set; } = "[]";
        public string? welcome_message { get; set; }
        public string? agentic_instruction { get; set; }
        public string? kb_prompt_mode { get; set; }
        public string? module_key { get; set; }
        public string? parent_id { get; set; }
        public string? external_ref { get; set; }
    }
}
