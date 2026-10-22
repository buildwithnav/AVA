using Ava.Api.Config;
using Ava.Api.Models;
using Ava.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

[ApiController]
[Route("api/vectors")]
public class VectorCacheController : ControllerBase
{
    private readonly SemanticSearchService _semanticSearch;
    private readonly IPromptRepository _promptRepo;
    private readonly ILogger<VectorCacheController> _logger;

    public VectorCacheController(
        SemanticSearchService semanticSearch,
        IPromptRepository promptRepo,
        ILogger<VectorCacheController> logger)
    {
        _semanticSearch = semanticSearch;
        _promptRepo = promptRepo;
        _logger = logger;
    }

    /// <summary>GET /api/vectors/status — Returns vector cache health and stats.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var stats = await _semanticSearch.GetCacheStatsAsync();
        return Ok(stats);
    }

    /// <summary>POST /api/vectors/rebuild — Invalidates cache and rebuilds the embedding index.</summary>
    [HttpPost("rebuild")]
    public async Task<IActionResult> Rebuild()
    {
        _logger.LogInformation("Vector cache rebuild requested");
        var prompts = await _promptRepo.GetAllAsync();
        var result = await _semanticSearch.RebuildIndexAsync(prompts);
        return Ok(result);
    }
}
