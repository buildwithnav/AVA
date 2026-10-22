using Ava.Api.Models;
using Ava.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

/// <summary>
/// Admin API for managing domain context files.
/// Supports upload (drag-and-drop / file picker), list, metadata update, and delete.
/// </summary>
[ApiController]
[Route("api/domain-files")]
public class DomainFileController : ControllerBase
{
    private readonly DomainFileService _domainFileService;
    private readonly ILogger<DomainFileController> _logger;

    public DomainFileController(
        DomainFileService domainFileService,
        ILogger<DomainFileController> logger)
    {
        _domainFileService = domainFileService;
        _logger = logger;
    }

    /// <summary>
    /// Upload a domain context file. Chunks it, indexes in FTS, and generates embeddings.
    /// </summary>
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] string domainId,
        [FromForm] string? title,
        [FromForm] string? description,
        [FromForm] string? tags,
        [FromForm] string? uploadedBy)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided" });

        if (string.IsNullOrWhiteSpace(domainId))
            return BadRequest(new { error = "domainId is required" });

        var fileName = file.FileName ?? "untitled";
        var fileTitle = title ?? Path.GetFileNameWithoutExtension(fileName);
        var fileDescription = description ?? "";
        var fileTags = string.IsNullOrWhiteSpace(tags)
            ? Array.Empty<string>()
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            using var stream = file.OpenReadStream();
            var result = await _domainFileService.UploadAsync(
                domainId, stream, fileName, file.Length,
                fileTitle, fileDescription, fileTags,
                uploadedBy ?? "admin");

            return Ok(new
            {
                result.Id,
                result.DomainId,
                result.Filename,
                result.Title,
                result.Description,
                result.Tags,
                result.ContentType,
                result.FileSizeBytes,
                result.ChunkCount,
                result.UploadedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain file upload failed for {Filename}", fileName);
            return StatusCode(500, new { error = "Upload failed", message = ex.Message });
        }
    }

    /// <summary>
    /// List all domain context files for a given domain.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string domainId)
    {
        if (string.IsNullOrWhiteSpace(domainId))
            return BadRequest(new { error = "domainId query parameter is required" });

        var files = await _domainFileService.GetByDomainAsync(domainId);
        return Ok(files.Select(f => new
        {
            f.Id,
            f.DomainId,
            f.Filename,
            f.Title,
            f.Description,
            f.Tags,
            f.ContentType,
            f.FileSizeBytes,
            f.ChunkCount,
            f.UploadedBy,
            f.UploadedAt
        }));
    }

    /// <summary>
    /// Update metadata (title, description, tags) for a domain file.
    /// </summary>
    [HttpPut("{id}/metadata")]
    public async Task<IActionResult> UpdateMetadata(string id, [FromBody] UpdateDomainFileMetadataRequest request)
    {
        var updated = await _domainFileService.UpdateMetadataAsync(
            id,
            request.Title ?? "",
            request.Description ?? "",
            request.Tags ?? Array.Empty<string>());

        if (updated == null)
            return NotFound(new { error = "Domain file not found", id });

        return Ok(new
        {
            updated.Id,
            updated.Title,
            updated.Description,
            updated.Tags,
            updated.ContentType
        });
    }

    /// <summary>
    /// Delete a domain file and its associated FTS chunks and embeddings.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var file = await _domainFileService.GetByIdAsync(id);
        if (file == null)
            return NotFound(new { error = "Domain file not found", id });

        try
        {
            await _domainFileService.DeleteAsync(id);
            return Ok(new { deleted = true, id, filename = file.Filename });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain file delete failed for {Id}", id);
            return StatusCode(500, new { error = "Delete failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Search domain files using hybrid search (for testing/debugging).
    /// </summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] DomainFileSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DomainId) || string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { error = "domainId and query are required" });

        var results = await _domainFileService.HybridSearchAsync(
            request.DomainId, request.Query, request.TopK ?? 5);

        return Ok(new { chunks = results });
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public class UpdateDomainFileMetadataRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string[]? Tags { get; set; }
}

public class DomainFileSearchRequest
{
    public string DomainId { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public int? TopK { get; set; }
}
