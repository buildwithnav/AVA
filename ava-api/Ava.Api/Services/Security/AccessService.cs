using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ava.Api.Services.Security;

/// <summary>
/// Central authorization decision point — identity resolution, gateway module fetch, and
/// domain/child-domain access evaluation in one place. Concrete class, no interface: nothing
/// else needs to swap this implementation, matching how the rest of Ava.Api's services
/// (AgentService, ConversationService, McpClientService, ...) are registered.
///
/// Domains are gated by a CDO Gateway module key (<c>domains.module_key</c>). A domain's
/// children (e.g. individual studies) reuse the same <c>domains</c> row via a self-referencing
/// <c>parent_id</c> — capped at 2 tiers — and are gated by AVA-native rows in
/// <c>domain_grants</c>, AND-ed with the parent domain's module. The SUPER_USER module
/// bypasses both, matching the Angular template's overrideIfSuper behavior.
/// </summary>
public class AccessService
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] UserIdClaims = { "buid", "uid", "preferred_username", "unique_name", "sub" };
    private static readonly string[] NameClaims = { "name", "displayName", "given_name" };
    private static readonly string[] EmailClaims = { "email", "upn" };

    private readonly IHttpContextAccessor _http;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDomainRepository _domains;
    private readonly IMemoryCache _cache;
    private readonly SecuritySettings _settings;
    private readonly ILogger<AccessService> _logger;

    public AccessService(
        IHttpContextAccessor http,
        IHttpClientFactory httpFactory,
        IDomainRepository domains,
        IMemoryCache cache,
        IOptionsSnapshot<AvaSettings> settings,
        ILogger<AccessService> logger)
    {
        _http = http;
        _httpFactory = httpFactory;
        _domains = domains;
        _cache = cache;
        _settings = settings.Value.Security;
        _logger = logger;
    }

    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the caller's identity from the CAS access-token cookie (cas-at), falling back to
    /// the configured local identity when Ava:Security:Enabled is false.
    ///
    /// The cas-at cookie is a JWT whose claims are read WITHOUT signature validation — the Pylon
    /// gateway in front of AVA has already authenticated the request, and AVA never treats this
    /// token as an authorization decision on its own (grants are looked up server-side by user ID).
    /// </summary>
    public CurrentUser GetUser()
    {
        if (!_settings.Enabled)
            return LocalUser();

        var context = _http.HttpContext;
        if (context == null)
        {
            _logger.LogWarning("No HTTP context available — falling back to local identity.");
            return LocalUser();
        }

        // Explicit header wins — lets a host app pass its own user through when CAS isn't in play.
        if (context.Request.Headers.TryGetValue("X-Ava-User", out var headerUser) &&
            !string.IsNullOrWhiteSpace(headerUser))
        {
            var id = headerUser.ToString().Trim();
            return new CurrentUser { UserId = id, DisplayName = id };
        }

        if (!context.Request.Cookies.TryGetValue("cas-at", out var token) || string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Security enabled but no cas-at cookie present on the request.");
            return new CurrentUser();
        }

        var user = ParseToken(token);
        if (string.IsNullOrEmpty(user.UserId))
            _logger.LogWarning("cas-at present but no recognizable user-id claim was found.");

        return user;
    }

    private CurrentUser LocalUser() => new()
    {
        UserId = _settings.LocalUserId,
        DisplayName = _settings.LocalUserName,
        IsLocal = true
    };

    private CurrentUser ParseToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return new CurrentUser();

            using var doc = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            var root = doc.RootElement;

            return new CurrentUser
            {
                UserId = FirstClaim(root, UserIdClaims) ?? string.Empty,
                DisplayName = FirstClaim(root, NameClaims) ?? FirstClaim(root, UserIdClaims) ?? string.Empty,
                Email = FirstClaim(root, EmailClaims)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse cas-at token.");
            return new CurrentUser();
        }
    }

    private static string? FirstClaim(JsonElement root, string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                var val = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        return null;
    }

    // ── Gateway modules ──────────────────────────────────────────────────────

    /// <summary>
    /// The caller's gateway modules, mirroring the Angular template's SecurityService.getModules().
    /// Returns LocalModules when Enabled is false. Cached per user for a short TTL — otherwise
    /// every request would hit the gateway.
    /// </summary>
    public async Task<List<Module>> GetModulesAsync()
    {
        if (!_settings.Enabled)
            return _settings.LocalModules;

        var user = GetUser();
        if (string.IsNullOrEmpty(user.UserId))
            return new List<Module>();

        var cacheKey = $"modules:{user.UserId}";
        if (_settings.ModuleCacheSeconds > 0 &&
            _cache.TryGetValue(cacheKey, out List<Module>? cached) &&
            cached != null)
        {
            return cached;
        }

        var modules = await FetchFromGatewayAsync();

        if (_settings.ModuleCacheSeconds > 0)
            _cache.Set(cacheKey, modules, TimeSpan.FromSeconds(_settings.ModuleCacheSeconds));

        return modules;
    }

    public void InvalidateModuleCache(string? userId = null)
    {
        if (string.IsNullOrEmpty(userId)) return;
        _cache.Remove($"modules:{userId}");
    }

    private async Task<List<Module>> FetchFromGatewayAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.GatewayBaseUrl) || string.IsNullOrWhiteSpace(_settings.AppKey))
        {
            _logger.LogError("Security is enabled but Ava:Security GatewayBaseUrl/AppKey are not configured — denying all module access.");
            return new List<Module>();
        }

        var url = $"{_settings.GatewayBaseUrl.TrimEnd('/')}/user/app/{_settings.AppKey}/modules";

        try
        {
            // Builds a client that replays the caller's CAS cookies so the gateway resolves the
            // same user. Deliberately not reusing IAuthService — it can fall back to a shared
            // service account, which would return the wrong person's modules.
            using var client = _httpFactory.CreateClient("cdo-gateway");
            var request = _http.HttpContext?.Request;
            if (request != null)
            {
                var cookies = new[] { "cas-at", "cas-rt" }
                    .Where(name => !string.IsNullOrEmpty(request.Cookies[name]))
                    .Select(name => $"{name}={request.Cookies[name]}")
                    .ToList();
                if (cookies.Count > 0)
                    client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
            }

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gateway module fetch returned {Status} from {Url}", response.StatusCode, url);
                return new List<Module>();
            }

            var body = await response.Content.ReadAsStringAsync();
            var modules = JsonSerializer.Deserialize<List<Module>>(body, _jsonOpts) ?? new List<Module>();
            _logger.LogInformation("Gateway returned {Count} modules for the current user.", modules.Count);
            return modules;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway module fetch failed for {Url}", url);
            return new List<Module>();
        }
    }

    // ── Access evaluation ────────────────────────────────────────────────────

    public async Task<bool> IsSuperUserAsync() => IsSuperUser(await GetModulesAsync());

    public async Task<bool> HasModuleAsync(string moduleKey, ModuleAccess access = ModuleAccess.Browse)
    {
        var modules = await GetModulesAsync();
        if (IsSuperUser(modules)) return true;

        var module = modules.FirstOrDefault(m => Matches(m.ModuleKey, moduleKey));
        return module?.Has(access) ?? false;
    }

    public async Task<bool> HasDomainAccessAsync(string domainId, ModuleAccess access = ModuleAccess.Browse)
    {
        if (string.IsNullOrWhiteSpace(domainId)) return false;

        var domain = await _domains.GetByIdAsync(domainId);
        if (domain == null || !domain.Enabled) return false;

        var modules = await GetModulesAsync();

        if (string.IsNullOrWhiteSpace(domain.ParentId))
            return EvaluateDomain(domain, modules, access);

        // domain is a child — its own module_key is ignored; access is normally the parent's
        // module check AND-ed with a grant (see EvaluateChildAsync). Domain admins managing the
        // hierarchy (files, KBs, settings) bypass the grant requirement — that only gates
        // end-user chat/data access, checked explicitly via HasChildDomainAccessAsync instead.
        var parent = await _domains.GetByIdAsync(domain.ParentId);
        if (parent == null || !parent.Enabled) return false;

        if (modules.Any(m => Matches(m.ModuleKey, ModuleKeys.DomainAdmin) && m.Has(access)))
            return EvaluateDomain(parent, modules, access);

        return await EvaluateChildAsync(parent, domain, access);
    }

    public async Task<List<DomainDefinition>> FilterDomainsAsync(
        IEnumerable<DomainDefinition> domains, ModuleAccess access = ModuleAccess.Browse)
    {
        var modules = await GetModulesAsync();
        if (IsSuperUser(modules)) return domains.ToList();

        return domains.Where(d => EvaluateDomain(d, modules, access)).ToList();
    }

    public async Task<bool> HasChildDomainAccessAsync(
        string parentId, string childId, ModuleAccess access = ModuleAccess.Browse)
    {
        // Parent domain access is a prerequisite — a child grant can't widen access to a
        // domain the user was never given.
        if (!await HasDomainAccessAsync(parentId, access))
            return false;

        var parent = await _domains.GetByIdAsync(parentId);
        var child = await _domains.GetByIdAsync(childId);
        if (parent == null || child == null || !child.Enabled || !Matches(child.ParentId, parentId))
            return false;

        return await EvaluateChildAsync(parent, child, access);
    }

    public async Task<List<DomainDefinition>> FilterChildDomainsAsync(
        string parentId, IEnumerable<DomainDefinition> children, ModuleAccess access = ModuleAccess.Browse)
    {
        if (!await HasDomainAccessAsync(parentId, access))
            return new List<DomainDefinition>();

        if (await IsSuperUserAsync() || (!_settings.Enabled && _settings.LocalGrantAllSubDomains))
            return children.ToList();

        var grants = await GetGrantsAsync();
        var parentGrants = grants.Where(g => Matches(g.ParentId, parentId) && g.Has(access)).ToList();

        if (parentGrants.Any(g => g.DomainId == null))
            return children.ToList();

        var allowed = parentGrants.Where(g => g.DomainId != null)
            .Select(g => g.DomainId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return children.Where(c => allowed.Contains(c.Id)).ToList();
    }

    public async Task<SecurityContext> GetSecurityContextAsync()
    {
        var user = GetUser();
        var modules = await GetModulesAsync();
        var isSuper = IsSuperUser(modules);

        var allDomains = await _domains.GetAllAsync();
        var grants = await GetGrantsAsync();
        var grantAllChildren = isSuper || (!_settings.Enabled && _settings.LocalGrantAllSubDomains);

        var domainInfos = new List<DomainAccessInfo>();
        foreach (var domain in allDomains)
        {
            if (!EvaluateDomain(domain, modules, ModuleAccess.Browse))
                continue;

            var info = new DomainAccessInfo
            {
                DomainId = domain.Id,
                Name = domain.Name,
                ModuleKey = domain.ModuleKey,
                Browse = EvaluateDomain(domain, modules, ModuleAccess.Browse),
                Update = EvaluateDomain(domain, modules, ModuleAccess.Update),
                Insert = EvaluateDomain(domain, modules, ModuleAccess.Insert),
                Delete = EvaluateDomain(domain, modules, ModuleAccess.Delete)
            };

            var children = await _domains.GetChildrenAsync(domain.Id);
            if (children.Count > 0)
            {
                var parentGrants = grants.Where(g => Matches(g.ParentId, domain.Id) && g.Browse).ToList();

                if (grantAllChildren || parentGrants.Any(g => g.DomainId == null))
                {
                    info.AllChildDomains = true;
                    info.ChildDomainIds = children.Select(c => c.Id).ToList();
                }
                else
                {
                    var allowed = parentGrants.Where(g => g.DomainId != null)
                        .Select(g => g.DomainId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    info.ChildDomainIds = children.Where(c => allowed.Contains(c.Id)).Select(c => c.Id).ToList();
                }
            }

            domainInfos.Add(info);
        }

        return new SecurityContext { User = user, Modules = modules, IsSuperUser = isSuper, Domains = domainInfos };
    }

    /// <summary>Only ever call with a top-level domain (ParentId null) — children go through EvaluateChildAsync.</summary>
    private bool EvaluateDomain(DomainDefinition domain, List<Module> modules, ModuleAccess access)
    {
        if (!domain.Enabled) return false;
        if (IsSuperUser(modules)) return true;

        if (string.IsNullOrWhiteSpace(domain.ModuleKey))
            return _settings.AllowUnmappedDomains;

        var module = modules.FirstOrDefault(m => Matches(m.ModuleKey, domain.ModuleKey));
        return module?.Has(access) ?? false;
    }

    /// <summary>
    /// A child's access = the parent's module-key check AND a domain_grants row matching
    /// this child (or a wildcard covering every child of the parent). Super user and the
    /// local "grant all sub-domains" dev shortcut both bypass the grant check.
    /// </summary>
    private async Task<bool> EvaluateChildAsync(DomainDefinition parent, DomainDefinition child, ModuleAccess access)
    {
        var modules = await GetModulesAsync();
        if (IsSuperUser(modules)) return true;
        if (!EvaluateDomain(parent, modules, access)) return false;

        if (!_settings.Enabled && _settings.LocalGrantAllSubDomains) return true;

        var grants = await GetGrantsAsync();
        return grants.Any(g =>
            Matches(g.ParentId, parent.Id) &&
            (g.DomainId == null || Matches(g.DomainId, child.Id)) &&
            g.Has(access));
    }

    /// <summary>
    /// Single source of truth for super-user status — a module scan, identical in local dev and
    /// production. Locally, GetModulesAsync() returns LocalModules, so disabling MresjWO's Browse
    /// flag there tests the denial path with the same rules that apply in production.
    /// </summary>
    private bool IsSuperUser(List<Module> modules) =>
        modules.Any(m => Matches(m.ModuleKey, ModuleKeys.SuperUser) && m.Browse);

    private async Task<List<DomainGrant>> GetGrantsAsync()
    {
        var user = GetUser();
        if (string.IsNullOrEmpty(user.UserId))
            return new List<DomainGrant>();

        // Group membership isn't resolvable from CAS yet — grants are user-scoped for now.
        return await _domains.GetGrantsForPrincipalAsync(user.UserId, Array.Empty<string>());
    }

    private static bool Matches(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
