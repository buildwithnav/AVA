using Ava.Api.Data;
using Ava.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

/// <summary>
/// Usage analytics endpoints.
/// GET /api/usage/summary          — aggregate totals (all users, or filtered by userId/date range)
/// GET /api/usage/by-user/{userId} — per-run events for a specific user
///
/// Note: No auth gate currently (internal deployment behind VPN).
/// Auth scoping should be added when the user/role system is implemented.
/// </summary>
[ApiController]
[Route("api/usage")]
public class UsageController : ControllerBase
{
    private readonly PostgresUsageRepository _repo;
    private readonly ILogger<UsageController> _logger;

    public UsageController(PostgresUsageRepository repo, ILogger<UsageController> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/usage/summary
    /// Returns aggregated usage totals broken down by model and domain.
    /// Optional query parameters: userId, from (ISO date), to (ISO date).
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        var summary = await _repo.GetSummaryAsync(userId, from, to);
        return Ok(summary);
    }

    /// <summary>
    /// GET /api/usage/by-user/{userId}
    /// Returns individual usage events for the given userId, newest first.
    /// Optional query parameters: from (ISO date), to (ISO date), limit (default 100).
    /// </summary>
    [HttpGet("by-user/{userId}")]
    public async Task<IActionResult> GetByUser(
        string userId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { error = "userId is required" });

        var events = await _repo.GetByUserAsync(userId, from, to, Math.Clamp(limit, 1, 1000));
        return Ok(new
        {
            userId,
            count = events.Count,
            events
        });
    }
}
