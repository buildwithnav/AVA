using Ava.Api.Models;
using Ava.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

/// <summary>
/// Module-access endpoints consumed by both Angular apps on startup.
/// Mirrors the CDO Gateway contract the Angular template's SecurityService expects.
/// </summary>
[ApiController]
[Route("api/security")]
public class SecurityController : ControllerBase
{
    private readonly AccessService _access;

    public SecurityController(AccessService access)
    {
        _access = access;
    }

    /// <summary>GET /api/security/modules — raw gateway module list for the caller.</summary>
    [HttpGet("modules")]
    public async Task<IActionResult> GetModules() => Ok(await _access.GetModulesAsync());

    /// <summary>
    /// GET /api/security/context — modules plus resolved domain/sub-domain access.
    /// This is what the UIs load in their APP_INITIALIZER.
    /// </summary>
    [HttpGet("context")]
    public async Task<IActionResult> GetContext() => Ok(await _access.GetSecurityContextAsync());

    /// <summary>GET /api/security/me — the resolved caller identity.</summary>
    [HttpGet("me")]
    public IActionResult Me() => Ok(_access.GetUser());

    /// <summary>POST /api/security/refresh — drop the cached gateway module list for the caller.</summary>
    [HttpPost("refresh")]
    public IActionResult Refresh()
    {
        var user = _access.GetUser();
        _access.InvalidateModuleCache(user.UserId);
        return Ok(new { refreshed = true });
    }
}
