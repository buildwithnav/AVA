using Ava.Api.Config;

namespace Ava.Api.Services;

/// <summary>
/// Session store keyed by browser-generated sessionId.
/// Holds per-user GitHub token, ADO PAT, and selected model.
/// Delegates persistence to ISessionStore (Redis or InMemory).
/// </summary>
public class UserSessionService
{
    private readonly ISessionStore _store;
    private readonly ILogger<UserSessionService> _logger;

    public UserSessionService(ISessionStore store, ILogger<UserSessionService> logger)
    {
        _store = store;
        _logger = logger;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<UserSession?> GetAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        return await _store.GetSessionAsync(sessionId);
    }

    public async Task<string?> GetGitHubTokenAsync(string? sessionId)
        => (await GetAsync(sessionId))?.GitHubToken;

    public async Task<string?> GetAdoPatAsync(string? sessionId)
        => (await GetAsync(sessionId))?.AdoPat;

    public async Task<string?> GetSelectedModelAsync(string? sessionId)
        => (await GetAsync(sessionId))?.SelectedModel;

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task SetGitHubTokenAsync(string sessionId, string token)
    {
        await _store.SetSessionFieldAsync(sessionId, "github_token", token);
        _logger.LogInformation("Session {Id}: GitHub token stored", sessionId[..8]);
    }

    public async Task SetAdoPatAsync(string sessionId, string pat)
    {
        await _store.SetSessionFieldAsync(sessionId, "ado_pat", pat);
        _logger.LogInformation("Session {Id}: ADO PAT stored", sessionId[..8]);
    }

    public async Task SetSelectedModelAsync(string sessionId, string modelId)
    {
        await _store.SetSessionFieldAsync(sessionId, "selected_model", modelId);
        _logger.LogInformation("Session {Id}: model set to {Model}", sessionId[..8], modelId);
    }

    // ── Revoke ────────────────────────────────────────────────────────────────

    public async Task RevokeGitHubTokenAsync(string sessionId)
    {
        await _store.DeleteSessionFieldAsync(sessionId, "github_token");
        await _store.DeleteSessionFieldAsync(sessionId, "selected_model");
        _logger.LogInformation("Session {Id}: GitHub token revoked", sessionId[..8]);
    }

    public async Task RevokeAdoPatAsync(string sessionId)
    {
        await _store.DeleteSessionFieldAsync(sessionId, "ado_pat");
        _logger.LogInformation("Session {Id}: ADO PAT revoked", sessionId[..8]);
    }
}
