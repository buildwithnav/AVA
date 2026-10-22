using Ava.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

/// <summary>
/// General auth endpoint.
///
/// GET /api/auth/me — returns current user auth state (session-based).
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserSessionService _sessions;

    public AuthController(UserSessionService sessions)
    {
        _sessions = sessions;
    }

    /// <summary>
    /// Returns the authenticated user's info for the given session.
    /// If no sessionId is provided or the session has no GitHub token,
    /// returns an empty object (unauthenticated).
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me([FromQuery] string? sessionId)
    {
        var token = await _sessions.GetGitHubTokenAsync(sessionId);
        if (string.IsNullOrEmpty(token))
            return Ok(new { });

        return Ok(new { token });
    }
}
