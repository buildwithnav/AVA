using Ava.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

/// <summary>
/// Azure DevOps PAT per-user endpoints.
///
/// POST   /api/auth/ado/connect   — stores a user-supplied PAT in their session
/// DELETE /api/auth/ado/session   — removes the ADO PAT from the session
/// GET    /api/auth/ado/status    — returns whether an ADO PAT is set for the session
/// </summary>
[ApiController]
[Route("api/auth/ado")]
public class AdoAuthController : ControllerBase
{
    private readonly UserSessionService _sessions;
    private readonly ILogger<AdoAuthController> _logger;

    public AdoAuthController(UserSessionService sessions, ILogger<AdoAuthController> logger)
    {
        _sessions = sessions;
        _logger   = logger;
    }

    // ── POST /api/auth/ado/connect ────────────────────────────────────────────

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] AdoConnectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest(new { error = "sessionId is required" });
        if (string.IsNullOrWhiteSpace(request.Pat))
            return BadRequest(new { error = "pat is required" });

        await _sessions.SetAdoPatAsync(request.SessionId, request.Pat);
        _logger.LogInformation("Session {Id}: ADO PAT connected", request.SessionId[..8]);
        return NoContent();
    }

    // ── DELETE /api/auth/ado/session ──────────────────────────────────────────

    [HttpDelete("session")]
    public async Task<IActionResult> Revoke([FromQuery] string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "sessionId is required" });

        await _sessions.RevokeAdoPatAsync(sessionId);
        return NoContent();
    }

    // ── GET /api/auth/ado/status ──────────────────────────────────────────────

    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "sessionId is required" });

        var pat = await _sessions.GetAdoPatAsync(sessionId);
        return Ok(new { connected = !string.IsNullOrEmpty(pat) });
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record AdoConnectRequest(string SessionId, string Pat);
