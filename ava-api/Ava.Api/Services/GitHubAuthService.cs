using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ava.Api.Config;
using Microsoft.Extensions.Options;

namespace Ava.Api.Services;

/// <summary>
/// Handles the GitHub OAuth Device Authorization Grant flow.
/// Step 1: POST /login/device/code  → returns user_code + device_code
/// Step 2: Poll POST /login/oauth/access_token until authorized/expired/denied
///
/// Docs: https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow
/// </summary>
public class GitHubAuthService
{
    private const string DeviceCodeUrl   = "https://github.com/login/device/code";
    private const string AccessTokenUrl  = "https://github.com/login/oauth/access_token";

    private readonly AvaSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubAuthService> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GitHubAuthService(
        IOptions<AvaSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubAuthService> logger)
    {
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ── Step 1: request device + user codes ───────────────────────────────────

    /// <summary>
    /// Initiates the device flow. Returns the user_code the user must enter at
    /// https://github.com/login/device, plus the device_code used for polling.
    /// </summary>
    public async Task<DeviceCodeResponse?> StartDeviceFlowAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.GitHubCopilot.ClientId))
        {
            _logger.LogWarning("GitHubCopilot:ClientId is not configured — device flow unavailable");
            return null;
        }

        var client = CreateClient();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _settings.GitHubCopilot.ClientId,
            ["scope"]     = "read:user copilot",  // copilot scope required for Copilot API
        });

        var response = await client.PostAsync(DeviceCodeUrl, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("GitHub device/code returned {Status}: {Body}",
                (int)response.StatusCode, json);
            throw new HttpRequestException(
                $"GitHub device/code returned {(int)response.StatusCode}: {json}");
        }
        var result = JsonSerializer.Deserialize<GitHubDeviceCodeRaw>(json, _jsonOpts);
        if (result == null) return null;

        _logger.LogInformation("Device flow started — user_code={Code} expires_in={Exp}s",
            result.UserCode, result.ExpiresIn);

        return new DeviceCodeResponse(
            DeviceCode:      result.DeviceCode,
            UserCode:        result.UserCode,
            VerificationUri: result.VerificationUri,
            ExpiresIn:       result.ExpiresIn,
            Interval:        result.Interval);
    }

    // ── Step 2: poll for access token ─────────────────────────────────────────

    /// <summary>
    /// Polls GitHub once for an access token. The caller is responsible for
    /// repeating the call at the correct interval.
    /// Returns status: "authorized" | "pending" | "slow_down" | "expired" | "denied" | "error"
    /// </summary>
    public async Task<GitHubPollResult> PollForTokenAsync(string deviceCode, CancellationToken ct = default)
    {
        var client = CreateClient();
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]   = _settings.GitHubCopilot.ClientId,
            ["device_code"] = deviceCode,
            ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code"
        });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(AccessTokenUrl, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GitHub token poll HTTP call failed");
            return new GitHubPollResult("error", null, null);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var raw  = JsonSerializer.Deserialize<GitHubTokenRaw>(json, _jsonOpts);
        if (raw == null) return new GitHubPollResult("error", null, null);

        // Success — access_token present
        if (!string.IsNullOrEmpty(raw.AccessToken))
        {
            _logger.LogInformation("GitHub device flow: token acquired");
            return new GitHubPollResult("authorized", null, raw.AccessToken);
        }

        // Error states
        var status = raw.Error switch
        {
            "authorization_pending" => "pending",
            "slow_down"             => "slow_down",
            "expired_token"         => "expired",
            "access_denied"         => "denied",
            _                       => "error"
        };

        // slow_down adds 5 seconds to the interval — return new interval to caller
        int? newInterval = raw.Error == "slow_down" ? raw.Interval : null;

        _logger.LogDebug("GitHub token poll: status={Status}", status);
        return new GitHubPollResult(status, newInterval, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("github-auth");
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int    ExpiresIn,
    int    Interval);

public record GitHubPollResult(
    string  Status,       // authorized | pending | slow_down | expired | denied | error
    int?    NewInterval,  // set when status == slow_down
    string? Token);       // set when status == authorized

// JSON deserialization shapes (snake_case from GitHub API)

internal class GitHubDeviceCodeRaw
{
    [JsonPropertyName("device_code")]      public string DeviceCode      { get; set; } = string.Empty;
    [JsonPropertyName("user_code")]        public string UserCode        { get; set; } = string.Empty;
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = string.Empty;
    [JsonPropertyName("expires_in")]       public int    ExpiresIn       { get; set; }
    [JsonPropertyName("interval")]         public int    Interval        { get; set; } = 5;
}

internal class GitHubTokenRaw
{
    [JsonPropertyName("access_token")]  public string? AccessToken { get; set; }
    [JsonPropertyName("token_type")]    public string? TokenType   { get; set; }
    [JsonPropertyName("scope")]         public string? Scope       { get; set; }
    [JsonPropertyName("error")]         public string? Error       { get; set; }
    [JsonPropertyName("interval")]      public int     Interval    { get; set; } = 5;
}
