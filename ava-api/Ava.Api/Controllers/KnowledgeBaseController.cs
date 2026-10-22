using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using Ava.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

/// <summary>
/// Manages knowledge bases: proxy to Python ava-kb for upload/ingest,
/// and domain-scoped KB CRUD with Postgres persistence.
/// </summary>
[ApiController]
[Route("api/kb")]
public class KnowledgeBaseController : ControllerBase
{
    private readonly KnowledgeBaseService _kbService;
    private readonly ToolExecutorService _toolExecutor;
    private readonly IKnowledgeBaseRepository _kbRepo;
    private readonly KbExportCacheService _exportCache;
    private readonly ILogger<KnowledgeBaseController> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public KnowledgeBaseController(
        KnowledgeBaseService kbService,
        ToolExecutorService toolExecutor,
        IKnowledgeBaseRepository kbRepo,
        KbExportCacheService exportCache,
        ILogger<KnowledgeBaseController> logger)
    {
        _kbService    = kbService;
        _toolExecutor = toolExecutor;
        _kbRepo       = kbRepo;
        _exportCache  = exportCache;
        _logger       = logger;
    }

    /// <summary>
    /// Upload a file (CSV/Excel/JSON) and create a session KB.
    /// </summary>
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] string? sessionId,
        [FromForm] string? domainId,
        [FromForm] string? kbName)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided" });

        var kbId = $"upload-{Guid.NewGuid():N}"[..20];
        var scope = string.IsNullOrEmpty(sessionId) ? "domain" : "session";
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var type = (ext == ".md" || ext == ".txt") ? "document" : "dataset";
        var name = kbName ?? Path.GetFileNameWithoutExtension(file.FileName);

        try
        {
            using var stream = file.OpenReadStream();
            var result = await _kbService.CallUploadAsync(
                kbId, scope, sessionId, domainId, type, stream, file.FileName);

            // Register KB metadata
            var kb = new KnowledgeBase
            {
                Id = kbId,
                Name = name,
                Type = type,
                Scope = scope,
                SessionId = sessionId,
                DomainId = domainId,
                Source = new KBSource { Kind = "upload" },
                RowCount = type == "dataset" && result?.RootElement.TryGetProperty("rowCount", out var rc) == true ? rc.GetInt32() : null,
                ChunkCount = type == "document" && result?.RootElement.TryGetProperty("chunkCount", out var cc) == true ? cc.GetInt32() : null,
            };
            _kbService.Register(kb);

            return Ok(new { kbId, name, type, scope, rowCount = kb.RowCount, chunkCount = kb.ChunkCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KB upload failed");
            return StatusCode(500, new { error = "Upload failed", message = ex.Message });
        }
    }

    /// <summary>
    /// List KBs available for a domain + session combination.
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> List(
        [FromQuery] string domainId,
        [FromQuery] string? sessionId)
    {
        var kbs = new List<object>();

        var domainKbs = await _kbService.GetForDomainAsync(domainId);
        foreach (var kb in domainKbs)
            kbs.Add(new { kb.Id, kb.Name, kb.Type, kb.Scope, kb.RowCount, kb.ChunkCount });

        if (!string.IsNullOrEmpty(sessionId))
        {
            var sessionKbs = await _kbService.GetForSessionAsync(sessionId);
            foreach (var kb in sessionKbs)
                kbs.Add(new { kb.Id, kb.Name, kb.Type, kb.Scope, kb.RowCount, kb.ChunkCount });
        }

        return Ok(kbs);
    }

    /// <summary>
    /// Delete a KB (both metadata and Python-side data).
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            await _kbService.DeleteKBAsync(id);
            return Ok(new { deleted = true, kbId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KB delete failed for {KbId}", id);
            return StatusCode(500, new { error = "Delete failed", message = ex.Message });
        }
    }

    // ── CSV export (Download CSV link) ───────────────────────────────────────

    /// <summary>
    /// Download the full CSV for a previously executed kb_query, identified by its queryId.
    /// Re-runs the server-trusted SQL (never client-supplied) against ava-kb's /kb/export,
    /// which streams every row with no 1000-row cap. Returns 410 if the export has expired,
    /// or 409 if any underlying dataset was refreshed/replaced since the query ran (so we never
    /// silently return data that differs from what the user originally saw).
    /// </summary>
    [HttpGet("export/{queryId}")]
    public async Task<IActionResult> ExportCsv(string queryId)
    {
        var entry = await _exportCache.GetAsync(queryId);
        if (entry == null)
            return StatusCode(410, new { error = "export_expired", message = "This export is no longer available. Please re-run the query." });

        // Resolve dataset parquet paths the same way the kb_query tool does, and verify each
        // dataset's version marker still matches what it was when the query originally ran.
        var datasetPaths = new Dictionary<string, string>();
        foreach (var id in entry.DatasetIds)
        {
            var kb = await _kbService.GetByIdAsync(id);
            if (kb == null)
                return NotFound(new { error = "kb_not_found", message = $"Dataset '{id}' not found." });

            var currentVersion = KbExportCacheService.VersionMarker(kb);
            if (!entry.DatasetVersions.TryGetValue(id, out var recordedVersion) || recordedVersion != currentVersion)
                return Conflict(new { error = "dataset_changed", message = $"Dataset '{id}' has changed since this query ran. Please re-run the query to download current results." });

            datasetPaths[id] = kb.DataPath ?? "";
        }

        var upstream = await _kbService.CallExportAsync(entry.DatasetIds, entry.Sql, datasetPaths);
        if (!upstream.IsSuccessStatusCode)
        {
            var err = await upstream.Content.ReadAsStringAsync();
            _logger.LogWarning("KB export failed ({Status}): {Body}", upstream.StatusCode, err);
            return StatusCode((int)upstream.StatusCode, new { error = "export_failed", message = err });
        }

        var fileName = $"ava-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        var stream = await upstream.Content.ReadAsStreamAsync();
        // Clinical data — prevent browser/proxy caching of the exported CSV.
        Response.Headers["Cache-Control"] = "no-store";
        return File(stream, "text/csv; charset=utf-8", fileName);
    }

    // ── Domain KB management ─────────────────────────────────────────────────

    /// <summary>
    /// List all domain-scoped KBs for a domain (used by orchestration UI and chat KB bar).
    /// </summary>
    [HttpGet("domain/{domainId}")]
    public async Task<IActionResult> GetDomainKbs(string domainId)
    {
        var kbs = await _kbRepo.GetByDomainAsync(domainId);
        return Ok(kbs.Select(kb => new
        {
            kb.Id,
            kb.Name,
            kb.Description,
            kb.Type,
            kb.Scope,
            kb.DomainId,
            kb.Source,
            kb.RowCount,
            kb.ChunkCount,
            kb.Schema,
            kb.ColumnDefinitions,
            kb.LastRefreshedAt,
            kb.CreatedAt,
        }));
    }

    /// <summary>
    /// Create a domain KB definition (no data yet — use refresh to populate).
    /// </summary>
    [HttpPost("domain")]
    public IActionResult CreateDomainKb([FromBody] CreateDomainKbRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.DomainId))
            return BadRequest(new { error = "name and domainId are required" });

        var kb = new KnowledgeBase
        {
            Id          = request.Id ?? $"kb-{request.DomainId}-{Guid.NewGuid():N}"[..28],
            Name        = request.Name,
            Description = request.Description,
            Type        = "dataset",
            Scope       = "domain",
            DomainId    = request.DomainId,
            Source      = new KBSource { Kind = "api", ToolId = request.ToolId },
            ColumnDefinitions = request.ColumnDefinitions,
        };

        _kbService.Register(kb);
        _logger.LogInformation("Created domain KB definition '{Id}' for domain '{Domain}'", kb.Id, kb.DomainId);
        return Ok(new { kb.Id, kb.Name, kb.DomainId, kb.Source, kb.ColumnDefinitions });
    }

    /// <summary>
    /// Update the description of a domain KB.
    /// </summary>
    [HttpPut("{id}/description")]
    public async Task<IActionResult> UpdateDescription(string id,
        [FromBody] UpdateKbDescriptionRequest request)
    {
        var kb = await _kbRepo.GetByIdAsync(id);
        if (kb == null) return NotFound(new { error = $"KB '{id}' not found" });
        if (kb.Scope != "domain") return BadRequest(new { error = "Description can only be updated on domain KBs" });

        kb.Description = request.Description?.Trim();
        await _kbRepo.UpdateAsync(kb);

        _logger.LogInformation("Updated description for KB '{Id}'", id);
        return Ok(new { kb.Id, kb.Description });
    }

    /// <summary>
    /// Update the type of a domain KB. Allowed values: "dataset" or "document".
    /// </summary>
    [HttpPut("{id}/type")]
    public async Task<IActionResult> UpdateType(string id,
        [FromBody] UpdateKbTypeRequest request)
    {
        var kb = await _kbRepo.GetByIdAsync(id);
        if (kb == null) return NotFound(new { error = $"KB '{id}' not found" });
        if (kb.Scope != "domain") return BadRequest(new { error = "Type can only be updated on domain KBs" });

        var type = request.Type?.Trim().ToLowerInvariant();
        if (type != "dataset" && type != "document")
            return BadRequest(new { error = "Type must be 'dataset' or 'document'" });

        kb.Type = type;
        await _kbRepo.UpdateAsync(kb);

        _logger.LogInformation("Updated type for KB '{Id}' to '{Type}'", id, type);
        return Ok(new { kb.Id, kb.Type });
    }

    /// <summary>
    /// Update column definitions for a domain KB.
    /// Column labels and descriptions help the LLM understand domain semantics.
    /// Optionally overrides column types on the stored schema (types feed the
    /// system prompt and the kb_schema tool). Column names are matched, never renamed.
    /// </summary>
    [HttpPut("{id}/columns")]
    public async Task<IActionResult> UpdateColumns(string id,
        [FromBody] UpdateColumnsRequest request)
    {
        var kb = await _kbRepo.GetByIdAsync(id);
        if (kb == null) return NotFound(new { error = $"KB '{id}' not found" });
        if (kb.Scope != "domain") return BadRequest(new { error = "Column definitions can only be set on domain KBs" });

        kb.ColumnDefinitions = request.ColumnDefinitions ?? new();

        // Apply type overrides to the stored schema. Match existing columns by name
        // (names are read-only); blank/unknown entries are ignored so a bad payload
        // can never drop columns or wipe a type.
        if (request.Schema is { Count: > 0 } && kb.Schema is { Count: > 0 })
        {
            var typeByName = request.Schema
                .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Type))
                .GroupBy(c => c.Name)
                .ToDictionary(g => g.Key, g => g.Last().Type.Trim());

            foreach (var col in kb.Schema)
                if (typeByName.TryGetValue(col.Name, out var newType))
                    col.Type = newType;
        }

        await _kbRepo.UpdateAsync(kb);

        _logger.LogInformation("Updated column definitions for KB '{Id}' ({Count} columns defined)", id, kb.ColumnDefinitions.Count);
        return Ok(new { kb.Id, kb.ColumnDefinitions, kb.Schema });
    }

    /// <summary>
    /// Refresh a domain KB by re-running its source tool endpoint,
    /// replacing the stored data and updating metadata.
    /// </summary>
    [HttpPost("{id}/refresh")]
    public async Task<IActionResult> Refresh(string id)
    {
        var kb = await _kbRepo.GetByIdAsync(id);
        if (kb == null) return NotFound(new { error = $"KB '{id}' not found" });
        if (kb.Scope != "domain") return BadRequest(new { error = "Only domain KBs can be refreshed" });
        if (string.IsNullOrEmpty(kb.Source.ToolId))
            return BadRequest(new { error = "KB has no source toolId configured — cannot refresh" });

        try
        {
            var updated = await _toolExecutor.RefreshDomainKbAsync(kb);
            if (updated == null)
                return StatusCode(500, new { error = "Refresh failed — see server logs" });

            return Ok(new
            {
                updated.Id,
                updated.Name,
                updated.Type,
                updated.Source,
                updated.RowCount,
                updated.Schema,
                updated.ColumnDefinitions,
                updated.LastRefreshedAt,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KB refresh failed for '{Id}'", id);
            return StatusCode(500, new { error = "Refresh failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Upload a data file (Excel/CSV) for a domain KB and (re)populate it, creating the KB if it
    /// does not exist. The file is parsed by ava-kb; the resulting schema plus the supplied column
    /// definitions are stored in Postgres so kb_schema can serve rich, labelled columns on demand.
    /// Sample rows are intentionally NOT persisted — the column definitions carry the semantics and
    /// this avoids storing row-level (potentially PII) data. Reusable across studies/domains.
    /// </summary>
    [HttpPost("{id}/upload-file")]
    [RequestSizeLimit(104_857_600)] // 100 MB — CRF datasets are well under the ava-kb 12 MB parse cap
    public async Task<IActionResult> UploadDataFile(
        string id,
        IFormFile file,
        [FromForm] string domainId,
        [FromForm] string? name,
        [FromForm] string? description,
        [FromForm] string? columnDefinitions)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided" });
        if (string.IsNullOrWhiteSpace(domainId))
            return BadRequest(new { error = "domainId is required" });

        // Optional column definitions: JSON map of columnName -> { label, description, dataType }
        Dictionary<string, KBColumnDefinition>? colDefs = null;
        if (!string.IsNullOrWhiteSpace(columnDefinitions))
        {
            try
            {
                colDefs = JsonSerializer.Deserialize<Dictionary<string, KBColumnDefinition>>(columnDefinitions, _jsonOpts);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = "Invalid columnDefinitions JSON", message = ex.Message });
            }
        }

        try
        {
            var existing = await _kbRepo.GetByIdAsync(id);

            // Clear any prior ava-kb data so the re-upload is clean (404 is fine for a new KB)
            try { await _kbService.DeleteKbDataOnlyAsync(id); } catch { /* best-effort */ }

            using var stream = file.OpenReadStream();
            var result = await _kbService.CallUploadAsync(
                id, "domain", null, domainId, "dataset", stream, file.FileName);
            if (result == null)
                return StatusCode(500, new { error = "Upload failed", message = "ava-kb returned no result" });

            var kb = existing ?? new KnowledgeBase { Id = id, CreatedAt = DateTime.UtcNow };
            kb.DomainId = domainId;
            kb.Type     = "dataset";
            kb.Scope    = "domain";
            kb.Source   = new KBSource { Kind = "upload" };
            kb.Name     = !string.IsNullOrWhiteSpace(name) ? name!
                          : (string.IsNullOrWhiteSpace(kb.Name) ? Path.GetFileNameWithoutExtension(file.FileName) : kb.Name);
            if (!string.IsNullOrWhiteSpace(description)) kb.Description = description;
            if (colDefs is { Count: > 0 }) kb.ColumnDefinitions = colDefs;

            // Metadata from the ava-kb upload response. Sample rows are deliberately NOT stored.
            var root = result.RootElement;
            if (root.TryGetProperty("rowCount", out var rc)) kb.RowCount = rc.GetInt32();
            if (root.TryGetProperty("dataPath", out var dp)) kb.DataPath = dp.GetString();
            if (root.TryGetProperty("schema", out var sc))
                kb.Schema = sc.EnumerateArray().Select(e => new KBColumnInfo
                {
                    Name = e.GetProperty("name").GetString() ?? "",
                    Type = e.GetProperty("type").GetString() ?? "",
                }).ToList();
            kb.SampleRows      = null;
            kb.LastRefreshedAt = DateTime.UtcNow;

            await _kbRepo.UpsertAsync(kb);

            // Load-and-warn: report parquet columns that have no definition (never fails the upload)
            var undefinedColumns = kb.Schema?
                .Where(c => kb.ColumnDefinitions == null || !kb.ColumnDefinitions.ContainsKey(c.Name))
                .Select(c => c.Name).ToList() ?? new List<string>();
            if (undefinedColumns.Count > 0)
                _logger.LogWarning("KB '{Id}': {Count} column(s) have no definition: {Cols}",
                    id, undefinedColumns.Count, string.Join(", ", undefinedColumns));

            _logger.LogInformation("Uploaded data file for KB '{Id}' ({Rows} rows, {Cols} columns) in domain '{Domain}'",
                id, kb.RowCount, kb.Schema?.Count ?? 0, domainId);

            return Ok(new
            {
                kb.Id,
                kb.Name,
                kb.DomainId,
                kb.RowCount,
                columnCount    = kb.Schema?.Count ?? 0,
                definedColumns = kb.ColumnDefinitions?.Count ?? 0,
                undefinedColumns,
                created        = existing == null,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KB data-file upload failed for '{Id}'", id);
            return StatusCode(500, new { error = "Upload failed", message = ex.Message });
        }
    }
}

// ── Request DTOs ─────────────────────────────────────────────────────────────

public class CreateDomainKbRequest
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DomainId { get; set; } = string.Empty;
    public string? ToolId { get; set; }
    public Dictionary<string, KBColumnDefinition>? ColumnDefinitions { get; set; }
}

public class UpdateKbDescriptionRequest
{
    public string? Description { get; set; }
}

public class UpdateKbTypeRequest
{
    public string? Type { get; set; }
}

public class UpdateColumnsRequest
{
    public Dictionary<string, KBColumnDefinition>? ColumnDefinitions { get; set; }
    /// <summary>Optional column type overrides, matched to the stored schema by name.</summary>
    public List<KBColumnInfo>? Schema { get; set; }
}
