using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using Dapper;

namespace Ava.Api.Data;

public class PostgresDomainFileRepository : IDomainFileRepository
{
    private readonly DbConnectionFactory _db;

    public PostgresDomainFileRepository(DbConnectionFactory db) => _db = db;

    public async Task<List<DomainFile>> GetByDomainAsync(string domainId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<DomainFileRow>(
            "SELECT * FROM domain_files WHERE LOWER(domain_id) = LOWER(@DomainId) AND enabled = true ORDER BY uploaded_at DESC",
            new { DomainId = domainId });
        return rows.Select(MapRow).ToList();
    }

    public async Task<DomainFile?> GetByIdAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var row = await conn.QueryFirstOrDefaultAsync<DomainFileRow>(
            "SELECT * FROM domain_files WHERE id = @Id", new { Id = id });
        return row != null ? MapRow(row) : null;
    }

    public async Task AddAsync(DomainFile file)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO domain_files (id, domain_id, filename, title, description, tags, content_type, file_size_bytes, chunk_count, uploaded_by, uploaded_at, enabled)
            VALUES (@Id, @DomainId, @Filename, @Title, @Description, @Tags, @ContentType, @FileSizeBytes, @ChunkCount, @UploadedBy, @UploadedAt, @Enabled)",
            new
            {
                file.Id,
                DomainId = file.DomainId,
                file.Filename,
                file.Title,
                file.Description,
                Tags = file.Tags.ToArray(),
                file.ContentType,
                FileSizeBytes = file.FileSizeBytes,
                ChunkCount = file.ChunkCount,
                UploadedBy = file.UploadedBy,
                UploadedAt = file.UploadedAt,
                file.Enabled
            });
    }

    public async Task UpdateAsync(DomainFile file)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync(@"
            UPDATE domain_files SET title = @Title, description = @Description, tags = @Tags,
                content_type = @ContentType, chunk_count = @ChunkCount, enabled = @Enabled
            WHERE id = @Id",
            new
            {
                file.Id,
                file.Title,
                file.Description,
                Tags = file.Tags.ToArray(),
                file.ContentType,
                ChunkCount = file.ChunkCount,
                file.Enabled
            });
    }

    public async Task RemoveAsync(string id)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM domain_files WHERE id = @Id", new { Id = id });
    }

    private static DomainFile MapRow(DomainFileRow row) => new()
    {
        Id = row.id,
        DomainId = row.domain_id,
        Filename = row.filename,
        Title = row.title,
        Description = row.description,
        Tags = row.tags?.ToList() ?? new(),
        ContentType = row.content_type,
        FileSizeBytes = row.file_size_bytes,
        ChunkCount = row.chunk_count,
        UploadedBy = row.uploaded_by,
        UploadedAt = row.uploaded_at,
        Enabled = row.enabled
    };

    private class DomainFileRow
    {
        public string id { get; set; } = "";
        public string domain_id { get; set; } = "";
        public string filename { get; set; } = "";
        public string title { get; set; } = "";
        public string description { get; set; } = "";
        public string[]? tags { get; set; }
        public string content_type { get; set; } = "general";
        public long file_size_bytes { get; set; }
        public int chunk_count { get; set; }
        public string uploaded_by { get; set; } = "";
        public DateTime uploaded_at { get; set; }
        public bool enabled { get; set; }
    }
}
