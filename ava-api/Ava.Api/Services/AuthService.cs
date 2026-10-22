using System.Net;
using Ava.Api.Config;
using Microsoft.Extensions.Options;

namespace Ava.Api.Services;

public interface IAuthService
{
    /// <summary>Returns an HttpClient pre-configured with auth cookies and the given base address.</summary>
    Task<HttpClient> CreateAuthenticatedClientAsync(string baseUrl);

    /// <summary>Forces a token refresh on next call.</summary>
    void InvalidateToken();
}

public class AuthService : IAuthService
{
    private readonly AuthSettings _auth;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthService> _logger;

    private CookieContainer? _cookies;
    private DateTime _cookieExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // Cached authenticated clients keyed by base URL (only used for password auth mode)
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _cookieLifetime = TimeSpan.FromMinutes(25);

    public AuthService(
        IOptions<AuthSettings> auth,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthService> logger)
    {
        _auth = auth.Value;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(string baseUrl)
    {
        if (_auth.UseCookieForwarding)
        {
            return CreateClientWithForwardedCookies(baseUrl);
        }

        // Password-based auth (original behavior)
        return await CreateClientWithPasswordAuthAsync(baseUrl);
    }

    /// <summary>
    /// Creates an HttpClient with cookies forwarded from the incoming request.
    /// Used when UseCookieForwarding is enabled (server deployment behind Pylon).
    /// </summary>
    private HttpClient CreateClientWithForwardedCookies(string baseUrl)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null)
        {
            _logger.LogError("Cookie forwarding enabled but no HTTP context available");
            throw new InvalidOperationException("No HTTP context available for cookie forwarding");
        }

        var uri = new Uri(baseUrl);
        var cookies = new CookieContainer();

        // Extract cas-at (access token)
        if (context.Request.Cookies.TryGetValue("cas-at", out var accessToken) && !string.IsNullOrEmpty(accessToken))
        {
            cookies.Add(new Cookie("cas-at", accessToken) { Domain = uri.Host });
            _logger.LogDebug("Forwarding cas-at cookie to {Host}", uri.Host);
        }

        // Extract cas-rt (refresh token)
        if (context.Request.Cookies.TryGetValue("cas-rt", out var refreshToken) && !string.IsNullOrEmpty(refreshToken))
        {
            cookies.Add(new Cookie("cas-rt", refreshToken) { Domain = uri.Host });
            _logger.LogDebug("Forwarding cas-rt cookie to {Host}", uri.Host);
        }

        if (cookies.Count == 0)
        {
            _logger.LogWarning("Cookie forwarding enabled but no cas-at/cas-rt cookies found in request");
            throw new UnauthorizedAccessException("Authentication cookies (cas-at/cas-rt) not found in request");
        }

        _logger.LogInformation("Cookie forwarding: {Count} cookies forwarded to {BaseUrl}", cookies.Count, baseUrl);

        var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        return new HttpClient(handler) { BaseAddress = uri };
    }

    /// <summary>
    /// Creates an HttpClient using password-based authentication.
    /// Used for local development when UseCookieForwarding is disabled.
    /// </summary>
    private async Task<HttpClient> CreateClientWithPasswordAuthAsync(string baseUrl)
    {
        await EnsureCookiesAsync();

        if (_clients.TryGetValue(baseUrl, out var existing))
            return existing;

        await _tokenLock.WaitAsync();
        try
        {
            if (_clients.TryGetValue(baseUrl, out existing))
                return existing;

            var handler = new HttpClientHandler
            {
                CookieContainer = _cookies!,
                UseCookies = true
            };
            var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
            _clients[baseUrl] = client;
            return client;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void InvalidateToken()
    {
        _cookies = null;
        _cookieExpiry = DateTime.MinValue;
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
        _logger.LogInformation("Auth token invalidated — will refresh on next call");
    }

    private async Task EnsureCookiesAsync()
    {
        if (_cookies != null && DateTime.UtcNow < _cookieExpiry)
            return;

        await _tokenLock.WaitAsync();
        try
        {
            if (_cookies != null && DateTime.UtcNow < _cookieExpiry)
                return;

            _cookies = await FetchCookiesAsync();
            _cookieExpiry = DateTime.UtcNow.Add(_cookieLifetime);

            // Dispose stale clients — they'll be rebuilt with fresh cookies
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<CookieContainer> FetchCookiesAsync()
    {
        _logger.LogInformation("Fetching auth token from {Url} (password auth mode)", _auth.TokenUrl);

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var client = new HttpClient(handler);

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _auth.ClientId,
            ["grant_type"] = _auth.GrantType,
            ["username"] = _auth.Username,
            ["password"] = _auth.Password,
            ["set_cookie"] = _auth.SetCookie.ToString().ToLowerInvariant()
        });

        var response = await client.PostAsync(_auth.TokenUrl, body);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Token request failed: {Status} — {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Authentication failed: {response.StatusCode} — {errorBody}");
        }

        var cookieCount = cookies.GetAllCookies().Count;
        _logger.LogInformation("Auth token obtained — {CookieCount} cookies captured", cookieCount);

        if (cookieCount == 0)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("No cookies from token endpoint. Response: {Body}", responseBody);
            throw new HttpRequestException("Authentication succeeded but returned 0 cookies.");
        }

        return cookies;
    }
}
