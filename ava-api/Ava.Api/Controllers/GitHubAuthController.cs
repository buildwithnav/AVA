using System.Text.Json;
using System.Text.Json.Serialization;
using Ava.Api.Config;
using Ava.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ava.Api.Controllers;

/// <summary>
/// GitHub OAuth Device Flow endpoints.
///
/// POST   /api/auth/github/start    — initiates device flow, returns user_code
/// POST   /api/auth/github/poll     — polls for token; on success stores it in session
/// GET    /api/auth/github/status   — returns connection status for a session
/// DELETE /api/auth/github/session  — revokes GitHub token from session
/// PUT  /api/auth/github/session  — sets the selected model for a session
/// GET    /api/auth/github/models   — returns config-driven list of available models
/// </summary>
[ApiController]
[Route("api/auth/github")]
public class GitHubAuthController : ControllerBase
{
    private readonly GitHubAuthService _gitHubAuth;
    private readonly UserSessionService _sessions;
    private readonly AvaSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubAuthController> _logger;

    public GitHubAuthController(
        GitHubAuthService gitHubAuth,
        UserSessionService sessions,
        IOptions<AvaSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubAuthController> logger)
    {
        _gitHubAuth         = gitHubAuth;
        _sessions           = sessions;
        _settings           = settings.Value;
        _httpClientFactory  = httpClientFactory;
        _logger             = logger;
    }

    // ── POST /api/auth/github/start ───────────────────────────────────────────

    [HttpPost("start")]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        var result = await _gitHubAuth.StartDeviceFlowAsync(ct);
        if (result == null)
            return StatusCode(503, new { error = "GitHub Copilot integration is not configured." });

        return Ok(new
        {
            userCode        = result.UserCode,
            deviceCode      = result.DeviceCode,
            verificationUri = result.VerificationUri,
            expiresIn       = result.ExpiresIn,
            interval        = result.Interval
        });
    }

    // ── POST /api/auth/github/poll ────────────────────────────────────────────

    /// <summary>
    /// Client calls this once per interval. If the user has authorized, the token is
    /// stored server-side and the status "authorized" is returned (no token in body).
    /// </summary>
    [HttpPost("poll")]
    public async Task<IActionResult> Poll([FromBody] PollRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceCode))
            return BadRequest(new { error = "deviceCode is required" });
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest(new { error = "sessionId is required" });

        var result = await _gitHubAuth.PollForTokenAsync(request.DeviceCode, ct);

        if (result.Status == "authorized" && result.Token != null)
        {
            await _sessions.SetGitHubTokenAsync(request.SessionId, result.Token);
            _logger.LogInformation("Session {Id}: GitHub connected", request.SessionId[..8]);
            return Ok(new { status = "authorized" });
        }

        return Ok(new
        {
            status      = result.Status,
            newInterval = result.NewInterval
        });
    }

    // ── GET /api/auth/github/status ───────────────────────────────────────────

    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "sessionId is required" });

        var session = await _sessions.GetAsync(sessionId);
        var connected = !string.IsNullOrEmpty(session?.GitHubToken);

        return Ok(new
        {
            connected,
            selectedModel = session?.SelectedModel
        });
    }

    // ── DELETE /api/auth/github/session ───────────────────────────────────────

    [HttpDelete("session")]
    public async Task<IActionResult> Revoke([FromQuery] string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "sessionId is required" });

        await _sessions.RevokeGitHubTokenAsync(sessionId);
        return NoContent();
    }

    // ── PUT /api/auth/github/session ────────────────────────────────────────

    [HttpPut("session")]
    public async Task<IActionResult> SetModel([FromBody] SetModelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest(new { error = "sessionId is required" });
        if (string.IsNullOrWhiteSpace(request.ModelId))
            return BadRequest(new { error = "modelId is required" });

        var token = await _sessions.GetGitHubTokenAsync(request.SessionId);
        if (string.IsNullOrEmpty(token))
            return Unauthorized(new { error = "GitHub is not connected for this session." });

        await _sessions.SetSelectedModelAsync(request.SessionId, request.ModelId);
        return NoContent();
    }

    // ── GET /api/auth/github/models ───────────────────────────────────────────

    /// <summary>
    /// Returns the list of available models.
    /// When sessionId is provided and the session has a GitHub token, the list is
    /// fetched live from api.githubcopilot.com/models (reflecting the user's actual
    /// Copilot subscription). Falls back to the static config list otherwise.
    /// </summary>
    [HttpGet("models")]
    public async Task<IActionResult> GetModels([FromQuery] string? sessionId, CancellationToken ct)
    {
        var token = string.IsNullOrWhiteSpace(sessionId)
            ? null
            : await _sessions.GetGitHubTokenAsync(sessionId);

        if (!string.IsNullOrEmpty(token))
        {
            var client = _httpClientFactory.CreateClient("github-auth");
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            client.DefaultRequestHeaders.Add("User-Agent", "GithubCopilot/1.155.0");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("Editor-Version", "Neovim/0.6.1");
            client.DefaultRequestHeaders.Add("Editor-Plugin-Version", "copilot.vim/1.16.0");

            try
            {
                var resp = await client.GetAsync($"{_settings.GitHubCopilot.Endpoint.TrimEnd('/')}/models", ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var raw  = JsonSerializer.Deserialize<CopilotModelsResponse>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (raw?.Data != null)
                    {
                        var models = raw.Data
                            .Where(m => m.Capabilities?.Type == "chat")
                            .Select(m => new
                            {
                                id        = m.Id,
                                label     = m.Name ?? m.Id,
                                publisher = m.Vendor ?? "GitHub"
                            })
                            .ToList();

                        if (models.Count > 0)
                            return Ok(models);
                    }
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Copilot /models returned {Status}: {Body}",
                        (int)resp.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch live Copilot models — using static list");
            }
        }

        // Not connected or API failed — return empty list
        return Ok(Array.Empty<object>());
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record PollRequest(string DeviceCode, string SessionId);
public record SetModelRequest(string SessionId, string ModelId);

// ── Copilot /models response shape ───────────────────────────────────────────

internal class CopilotModelsResponse
{
    [JsonPropertyName("data")] public List<CopilotModelEntry>? Data { get; set; }
}

internal class CopilotModelEntry
{
    [JsonPropertyName("id")]           public string?                  Id           { get; set; }
    [JsonPropertyName("name")]         public string?                  Name         { get; set; }
    [JsonPropertyName("vendor")]       public string?                  Vendor       { get; set; }
    [JsonPropertyName("capabilities")] public CopilotModelCapabilities? Capabilities { get; set; }
}

internal class CopilotModelCapabilities
{
    [JsonPropertyName("type")] public string? Type { get; set; }
}
