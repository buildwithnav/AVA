using System.Text.Json;
using Dapper;
using Ava.Api.Config;
using Ava.Api.Models;

namespace Ava.Api.Data;

/// <summary>
/// Hybrid IKnowledgeBaseRepository:
///   - Domain-scoped KBs are persisted to the knowledge_bases PostgreSQL table.
///   - Session-scoped KBs remain in-memory (transient by design).
/// On startup, seeds domain KBs from knowledgebases.json if they are not already in the DB.
/// </summary>
public class PostgresKnowledgeBaseRepository : IKnowledgeBaseRepository
{
    private readonly DbConnectionFactory _db;
    private readonly ILogger<PostgresKnowledgeBaseRepository> _logger;

    // In-memory store for session-scoped (transient) KBs only
    private readonly List<KnowledgeBase> _sessionKbs = new();
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public PostgresKnowledgeBaseRepository(
        DbConnectionFactory db,
        ILogger<PostgresKnowledgeBaseRepository> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── Seed ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Insert domain KBs from knowledgebases.json that are not yet in the database.
    /// Called once at startup to preserve backward compatibility.
    /// </summary>
    public async Task SeedFromConfigAsync(IEnumerable<KnowledgeBase> configKbs)
    {
        foreach (var kb in configKbs.Where(k => k.Scope == "domain"))
        {
            await using var conn = await _db.OpenConnectionAsync();

            // Upsert config fields on every startup so changes to knowledgebases.json
            // (e.g. corrected columnDefinitions) propagate without manual DB edits.
            // Runtime fields (schema, sample_rows, row_count, data_path, last_refreshed_at)
            // are intentionally excluded from the update so a KB refresh is not undone.
            await conn.ExecuteAsync("""
                INSERT INTO knowledge_bases
                    (id, name, description, type, scope, domain_id, source, column_definitions, enabled, created_at)
                VALUES
                    (@Id, @Name, @Description, @Type, @Scope, @DomainId, @Source::jsonb, @ColumnDefinitions::jsonb, @Enabled, @CreatedAt)
                ON CONFLICT (id) DO UPDATE SET
                    name               = EXCLUDED.name,
                    description        = EXCLUDED.description,
                    source             = EXCLUDED.source,
                    column_definitions = EXCLUDED.column_definitions,
                    enabled            = EXCLUDED.enabled
                """, new
            {
                kb.Id,
                kb.Name,
                kb.Description,
                kb.Type,
                kb.Scope,
                DomainId          = kb.DomainId,
                Source            = JsonSerializer.Serialize(kb.Source, _jsonOpts),
                ColumnDefinitions = kb.ColumnDefinitions != null
                    ? JsonSerializer.Serialize(kb.ColumnDefinitions, _jsonOpts) : null,
                kb.Enabled,
                kb.CreatedAt,
            });

            _logger.LogInformation("Seeded/updated domain KB '{Id}' config from knowledgebases.json", kb.Id);
        }
    }

    // ── IKnowledgeBaseRepository ──────────────────────────────────────────────

    public async Task<List<KnowledgeBase>> GetAllAsync()
    {
        var domainKbs = await GetAllDomainKbsFromDbAsync();
        lock (_lock)
        {
            return domainKbs.Concat(_sessionKbs).ToList();
        }
    }

    public async Task<KnowledgeBase?> GetByIdAsync(string id)
    {
        // Check session-scoped in-memory first
        lock (_lock)
        {
            var session = _sessionKbs.FirstOrDefault(k =>
                k.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && k.Enabled);
            if (session != null) return session;
        }

        // Fall back to DB
        await using var conn = await _db.OpenConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync(
            "SELECT * FROM knowledge_bases WHERE id = @Id AND enabled = true", new { Id = id });
        return row == null ? null : MapRow(row);
    }

    public async Task<List<KnowledgeBase>> GetByDomainAsync(string domainId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync(
            "SELECT * FROM knowledge_bases WHERE domain_id = @DomainId AND scope = 'domain' AND enabled = true ORDER BY created_at",
            new { DomainId = domainId });
        return rows.Select<dynamic, KnowledgeBase>(r => MapRow(r)).ToList();
    }

    public void Add(KnowledgeBase kb)
    {
        if (kb.Scope == "domain")
        {
            // Fire-and-forget async upsert — runs on thread pool so caller is not blocked
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var conn = await _db.OpenConnectionAsync();
                    var exists = await conn.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM knowledge_bases WHERE id = @Id)", new { kb.Id });

                    if (exists)
                        await UpdateRowAsync(conn, kb);
                    else
                        await InsertAsync(conn, kb);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist domain KB '{Id}' to database", kb.Id);
                }
            });
        }
        else
        {
            lock (_lock)
            {
                _sessionKbs.RemoveAll(k => k.Id == kb.Id);
                _sessionKbs.Add(kb);
            }
        }
    }

    public void Remove(string id)
    {
        lock (_lock) _sessionKbs.RemoveAll(k => k.Id == id);

        _ = Task.Run(async () =>
        {
            try
            {
                await using var conn = await _db.OpenConnectionAsync();
                await conn.ExecuteAsync(
                    "DELETE FROM knowledge_bases WHERE id = @Id", new { Id = id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove KB '{Id}' from database", id);
            }
        });
    }

    public async Task UpdateAsync(KnowledgeBase kb)
    {
        if (kb.Scope != "domain")
        {
            lock (_lock)
            {
                var idx = _sessionKbs.FindIndex(k => k.Id == kb.Id);
                if (idx >= 0) _sessionKbs[idx] = kb;
            }
            return;
        }

        await using var conn = await _db.OpenConnectionAsync();
        await UpdateRowAsync(conn, kb);
    }

    public async Task UpsertAsync(KnowledgeBase kb)
    {
        if (kb.Scope != "domain")
        {
            lock (_lock)
            {
                _sessionKbs.RemoveAll(k => k.Id == kb.Id);
                _sessionKbs.Add(kb);
            }
            return;
        }

        // InsertAsync uses INSERT ... ON CONFLICT (id) DO UPDATE, so this is a true upsert.
        await using var conn = await _db.OpenConnectionAsync();
        await InsertAsync(conn, kb);
    }

    // ── DB helpers ────────────────────────────────────────────────────────────

    private async Task<List<KnowledgeBase>> GetAllDomainKbsFromDbAsync()
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync(
            "SELECT * FROM knowledge_bases WHERE scope = 'domain' AND enabled = true ORDER BY created_at");
        return rows.Select<dynamic, KnowledgeBase>(r => MapRow(r)).ToList();
    }

    private async Task InsertAsync(Npgsql.NpgsqlConnection conn, KnowledgeBase kb)
    {
        await conn.ExecuteAsync("""
            INSERT INTO knowledge_bases
                (id, name, description, type, scope, domain_id, source,
                 column_definitions, row_count, chunk_count, schema, sample_rows,
                 data_path, enabled, created_at, last_refreshed_at)
            VALUES
                (@Id, @Name, @Description, @Type, @Scope, @DomainId, @Source::jsonb,
                 @ColumnDefinitions::jsonb, @RowCount, @ChunkCount, @Schema::jsonb, @SampleRows::jsonb,
                 @DataPath, @Enabled, @CreatedAt, @LastRefreshedAt)
            ON CONFLICT (id) DO UPDATE SET
                name               = EXCLUDED.name,
                description        = EXCLUDED.description,
                source             = EXCLUDED.source,
                column_definitions = EXCLUDED.column_definitions,
                row_count          = EXCLUDED.row_count,
                chunk_count        = EXCLUDED.chunk_count,
                schema             = EXCLUDED.schema,
                sample_rows        = EXCLUDED.sample_rows,
                data_path          = EXCLUDED.data_path,
                enabled            = EXCLUDED.enabled,
                last_refreshed_at  = EXCLUDED.last_refreshed_at
            """, ToParams(kb));
    }

    private async Task UpdateRowAsync(Npgsql.NpgsqlConnection conn, KnowledgeBase kb)
    {
        await conn.ExecuteAsync("""
            UPDATE knowledge_bases SET
                name               = @Name,
                type               = @Type,
                description        = @Description,
                source             = @Source::jsonb,
                column_definitions = @ColumnDefinitions::jsonb,
                row_count          = @RowCount,
                chunk_count        = @ChunkCount,
                schema             = @Schema::jsonb,
                sample_rows        = @SampleRows::jsonb,
                data_path          = @DataPath,
                enabled            = @Enabled,
                last_refreshed_at  = @LastRefreshedAt
            WHERE id = @Id
            """, ToParams(kb));
    }

    private static object ToParams(KnowledgeBase kb) => new
    {
        kb.Id,
        kb.Name,
        kb.Description,
        kb.Type,
        kb.Scope,
        DomainId        = kb.DomainId,
        Source          = JsonSerializer.Serialize(kb.Source, _jsonOpts),
        ColumnDefinitions = kb.ColumnDefinitions != null
            ? JsonSerializer.Serialize(kb.ColumnDefinitions, _jsonOpts) : null,
        RowCount        = kb.RowCount,
        ChunkCount      = kb.ChunkCount,
        Schema          = kb.Schema != null
            ? JsonSerializer.Serialize(kb.Schema, _jsonOpts) : null,
        SampleRows      = kb.SampleRows != null
            ? JsonSerializer.Serialize(kb.SampleRows, _jsonOpts) : null,
        kb.DataPath,
        kb.Enabled,
        kb.CreatedAt,
        LastRefreshedAt = kb.LastRefreshedAt,
    };

    private static KnowledgeBase MapRow(dynamic row)
    {
        var kb = new KnowledgeBase
        {
            Id              = row.id,
            Name            = row.name,
            Description     = row.description,
            Type            = row.type,
            Scope           = row.scope,
            DomainId        = row.domain_id,
            RowCount        = row.row_count,
            ChunkCount      = row.chunk_count,
            DataPath        = row.data_path,
            Enabled         = row.enabled,
            CreatedAt       = row.created_at,
            LastRefreshedAt = row.last_refreshed_at,
        };

        if (!string.IsNullOrEmpty((string?)row.source))
            kb.Source = JsonSerializer.Deserialize<KBSource>((string)row.source, _jsonOpts) ?? new();

        if (!string.IsNullOrEmpty((string?)row.column_definitions))
            kb.ColumnDefinitions = JsonSerializer.Deserialize<Dictionary<string, KBColumnDefinition>>(
                (string)row.column_definitions, _jsonOpts);

        if (!string.IsNullOrEmpty((string?)row.schema))
            kb.Schema = JsonSerializer.Deserialize<List<KBColumnInfo>>((string)row.schema, _jsonOpts);

        if (!string.IsNullOrEmpty((string?)row.sample_rows))
            kb.SampleRows = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                (string)row.sample_rows, _jsonOpts);

        return kb;
    }
}
