using Ava.Api.Config;
using Ava.Api.Models;
using Ava.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ava.Api.Tests;

/// <summary>
/// Access matrix for the domain / child-domain authorization rules. Children (e.g. individual
/// studies) are full DomainDefinition rows with ParentId set — nesting capped at 2 tiers, so a
/// child's ParentId must point at a top-level domain. These cover the decisions that would
/// otherwise leak data across studies.
///
/// Every scenario runs with Security:Enabled=false (the same "local dev" code path used at
/// runtime) and drives user identity + module grants purely through SecuritySettings — no
/// HttpContext or gateway HTTP calls are needed, since AccessService short-circuits to the
/// local settings before touching either when Enabled is false.
/// </summary>
public class AccessServiceTests
{
    private const string DomainId = "clinical-studies";
    private const string DomainModuleKey = "AVACLS";
    private const string StudyA = "study-a";
    private const string StudyB = "study-b";
    private const string UserId = "tester";

    // ── Domain-level ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DomainWithMatchingModule_IsAccessible()
    {
        var svc = Build(modules: new[] { Module(DomainModuleKey, browse: true) });
        Assert.True(await svc.HasDomainAccessAsync(DomainId));
    }

    [Fact]
    public async Task DomainWithoutMatchingModule_IsDenied()
    {
        var svc = Build(modules: new[] { Module("SOMETHING-ELSE", browse: true) });
        Assert.False(await svc.HasDomainAccessAsync(DomainId));
    }

    [Fact]
    public async Task DomainModuleWithoutUpdate_DeniesUpdate()
    {
        var svc = Build(modules: new[] { Module(DomainModuleKey, browse: true, update: false) });
        Assert.True(await svc.HasDomainAccessAsync(DomainId, ModuleAccess.Browse));
        Assert.False(await svc.HasDomainAccessAsync(DomainId, ModuleAccess.Update));
    }

    [Fact]
    public async Task SuperUser_BypassesDomainModule()
    {
        var svc = Build(modules: new[] { Module(ModuleKeys.SuperUser, browse: true) });
        Assert.True(await svc.HasDomainAccessAsync(DomainId, ModuleAccess.Delete));
    }

    [Fact]
    public async Task ArchivedDomain_IsDeniedEvenWithModule()
    {
        var svc = Build(
            modules: new[] { Module(DomainModuleKey, browse: true) },
            domains: new[] { Domain(enabled: false) });
        Assert.False(await svc.HasDomainAccessAsync(DomainId));
    }

    // ── Unmapped-domain policy ───────────────────────────────────────────────

    [Fact]
    public async Task UnmappedDomain_IsOpenWhenAllowed()
    {
        var svc = Build(domains: new[] { Domain(moduleKey: null) }, allowUnmapped: true);
        Assert.True(await svc.HasDomainAccessAsync(DomainId));
    }

    [Fact]
    public async Task UnmappedDomain_IsDeniedWhenNotAllowed()
    {
        var svc = Build(domains: new[] { Domain(moduleKey: null) }, allowUnmapped: false);
        Assert.False(await svc.HasDomainAccessAsync(DomainId));
    }

    // ── Child-domain grants ──────────────────────────────────────────────────

    [Fact]
    public async Task ExactGrant_AllowsOnlyThatChildDomain()
    {
        var svc = Build(
            modules: new[] { Module(DomainModuleKey, browse: true) },
            domains: new[] { Domain(), Child(StudyA), Child(StudyB) },
            grants: new[] { Grant(StudyA) });

        Assert.True(await svc.HasChildDomainAccessAsync(DomainId, StudyA));
        Assert.False(await svc.HasChildDomainAccessAsync(DomainId, StudyB));
    }

    [Fact]
    public async Task WildcardGrant_AllowsEveryChildDomain()
    {
        var svc = Build(
            modules: new[] { Module(DomainModuleKey, browse: true) },
            domains: new[] { Domain(), Child(StudyA), Child(StudyB) },
            grants: new[] { Grant(domainId: null) });

        Assert.True(await svc.HasChildDomainAccessAsync(DomainId, StudyA));
        Assert.True(await svc.HasChildDomainAccessAsync(DomainId, StudyB));
    }

    [Fact]
    public async Task NoGrant_DeniesChildDomain()
    {
        var svc = Build(
            modules: new[] { Module(DomainModuleKey, browse: true) },
            domains: new[] { Domain(), Child(StudyA) });
        Assert.False(await svc.HasChildDomainAccessAsync(DomainId, StudyA));
    }

    [Fact]
    public async Task GrantWithoutParentDomainModule_IsDenied()
    {
        // A grant must never widen access to a domain the user was never given.
        var svc = Build(
            domains: new[] { Domain(moduleKey: DomainModuleKey), Child(StudyA) },
            grants: new[] { Grant(domainId: null) });

        Assert.False(await svc.HasChildDomainAccessAsync(DomainId, StudyA));
    }

    [Fact]
    public async Task GrantWithoutUpdate_DeniesUpdateButAllowsBrowse()
    {
        var svc = Build(
            modules: new[] { Module(DomainModuleKey, browse: true, update: true) },
            domains: new[] { Domain(), Child(StudyA) },
            grants: new[] { Grant(StudyA, browse: true, update: false) });

        Assert.True(await svc.HasChildDomainAccessAsync(DomainId, StudyA, ModuleAccess.Browse));
        Assert.False(await svc.HasChildDomainAccessAsync(DomainId, StudyA, ModuleAccess.Update));
    }

    [Fact]
    public async Task SuperUser_BypassesChildDomainGrants()
    {
        var svc = Build(
            modules: new[] { Module(ModuleKeys.SuperUser, browse: true) },
            domains: new[] { Domain(), Child(StudyA) });
        Assert.True(await svc.HasChildDomainAccessAsync(DomainId, StudyA));
    }

    // ── 2-tier nesting cap ───────────────────────────────────────────────────
    // DomainController.ValidateParentAsync is what actually rejects creating a grandchild row.
    // This test documents the access-evaluation consequence: even if one existed, it could
    // never gain access, because its "parent" (a child itself) has no module key of its own.

    [Fact]
    public async Task GrandchildDomain_NeverGainsAccessEvenWithWildcardGrant()
    {
        var grandchild = Child(id: "sub-sub", parentId: StudyA);
        var svc = Build(
            modules: new[] { Module(DomainModuleKey, browse: true) },
            domains: new[] { Domain(), Child(StudyA), grandchild },
            grants: new[] { Grant(domainId: null) });

        Assert.False(await svc.HasDomainAccessAsync("sub-sub"));
    }

    // ── Filtering ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FilterDomains_DropsInaccessibleDomains()
    {
        var svc = Build(modules: new[] { Module(DomainModuleKey, browse: true) });

        var all = new List<DomainDefinition>
        {
            Domain(),
            Domain(id: "other", moduleKey: "OTHERKEY"),
        };

        var visible = await svc.FilterDomainsAsync(all);
        Assert.Single(visible);
        Assert.Equal(DomainId, visible[0].Id);
    }

    [Fact]
    public async Task FilterChildDomains_ReturnsOnlyGrantedStudies()
    {
        var svc = Build(
            modules: new[] { Module(DomainModuleKey, browse: true) },
            domains: new[] { Domain(), Child(StudyA), Child(StudyB) },
            grants: new[] { Grant(StudyA) });

        var children = new List<DomainDefinition> { Child(StudyA), Child(StudyB) };
        var visible = await svc.FilterChildDomainsAsync(DomainId, children);

        Assert.Single(visible);
        Assert.Equal(StudyA, visible[0].Id);
    }

    [Fact]
    public async Task FilterChildDomains_ReturnsNothingWhenDomainDenied()
    {
        var svc = Build(
            domains: new[] { Domain(), Child(StudyA) },
            grants: new[] { Grant(domainId: null) });

        var children = new List<DomainDefinition> { Child(StudyA) };
        Assert.Empty(await svc.FilterChildDomainsAsync(DomainId, children));
    }

    // ── Security context ─────────────────────────────────────────────────────

    [Fact]
    public async Task SecurityContext_ReportsGrantedChildDomainsOnly()
    {
        var svc = Build(
            modules: new[] { Module(DomainModuleKey, browse: true) },
            domains: new[] { Domain(), Child(StudyA), Child(StudyB) },
            grants: new[] { Grant(StudyA) });

        var context = await svc.GetSecurityContextAsync();

        var info = Assert.Single(context.Domains);
        Assert.False(info.AllChildDomains);
        Assert.Equal(new[] { StudyA }, info.ChildDomainIds);
    }

    [Fact]
    public async Task SecurityContext_FlagsWildcardAsAllChildDomains()
    {
        var svc = Build(
            modules: new[] { Module(DomainModuleKey, browse: true) },
            domains: new[] { Domain(), Child(StudyA), Child(StudyB) },
            grants: new[] { Grant(domainId: null) });

        var info = Assert.Single((await svc.GetSecurityContextAsync()).Domains);
        Assert.True(info.AllChildDomains);
        Assert.Equal(2, info.ChildDomainIds.Count);
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private static AccessService Build(
        IEnumerable<Module>? modules = null,
        IEnumerable<DomainDefinition>? domains = null,
        IEnumerable<DomainGrant>? grants = null,
        bool allowUnmapped = true)
    {
        var settings = new TestOptionsSnapshot<AvaSettings>(new AvaSettings
        {
            Security = new SecuritySettings
            {
                Enabled = false,
                LocalUserId = UserId,
                LocalModules = modules?.ToList() ?? new List<Module>(),
                AllowUnmappedDomains = allowUnmapped,
                LocalGrantAllSubDomains = false
            }
        });

        return new AccessService(
            new HttpContextAccessor(),
            new UnusedHttpClientFactory(),
            new FakeDomainRepository(
                domains?.ToList() ?? new List<DomainDefinition> { Domain() },
                grants?.ToList() ?? new List<DomainGrant>()),
            new MemoryCache(new MemoryCacheOptions()),
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AccessService>.Instance);
    }

    private static Module Module(string key, bool browse = false, bool update = false,
        bool insert = false, bool delete = false) => new()
    {
        ModuleKey = key,
        Browse = browse,
        Update = update,
        Insert = insert,
        Delete = delete
    };

    private static DomainDefinition Domain(
        string id = DomainId, string? moduleKey = DomainModuleKey, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        ModuleKey = moduleKey,
        Enabled = enabled
    };

    private static DomainDefinition Child(string id, string parentId = DomainId, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        ParentId = parentId,
        Enabled = enabled
    };

    private static DomainGrant Grant(
        string? domainId, bool browse = true, bool update = false,
        bool insert = false, bool delete = false) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        ParentId = DomainId,
        DomainId = domainId,
        PrincipalType = DomainGrant.PrincipalUser,
        PrincipalId = UserId,
        Browse = browse,
        Update = update,
        Insert = insert,
        Delete = delete
    };

    // ── Fakes ────────────────────────────────────────────────────────────────
    // Only the repository needs a fake — AccessService's identity/module logic is exercised
    // via Enabled=false + SecuritySettings above, not via mocked dependencies.

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Gateway calls should not happen with Security:Enabled=false.");
    }

    /// <summary>Combined domain + child + grant fake, matching the consolidated IDomainRepository.</summary>
    private sealed class FakeDomainRepository : IDomainRepository
    {
        private readonly List<DomainDefinition> _domains;
        private readonly List<DomainGrant> _grants;

        public FakeDomainRepository(List<DomainDefinition> domains, List<DomainGrant> grants)
        {
            _domains = domains;
            _grants = grants;
        }

        public Task<List<DomainDefinition>> GetAllAsync() =>
            Task.FromResult(_domains.Where(d => d.Enabled && d.ParentId == null).ToList());
        public Task<List<DomainDefinition>> GetAllIncludingArchivedAsync() =>
            Task.FromResult(_domains.Where(d => d.ParentId == null).ToList());
        public Task<DomainDefinition?> GetByIdAsync(string id) =>
            Task.FromResult(_domains.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)));
        public Task<List<DomainDefinition>> GetChildrenAsync(string parentId, bool includeArchived = false) =>
            Task.FromResult(_domains.Where(d =>
                string.Equals(d.ParentId, parentId, StringComparison.OrdinalIgnoreCase) && (includeArchived || d.Enabled)).ToList());
        public Task AddAsync(DomainDefinition domain) => Task.CompletedTask;
        public Task<bool> UpdateAsync(DomainDefinition domain) => Task.FromResult(true);
        public Task<bool> ArchiveAsync(string id) => Task.FromResult(true);
        public Task<bool> RestoreAsync(string id) => Task.FromResult(true);
        public Task<bool> DeleteAsync(string id) => Task.FromResult(true);

        public Task<List<DomainGrant>> GetGrantsForPrincipalAsync(string principalId, IEnumerable<string> groupIds) =>
            Task.FromResult(_grants.Where(g =>
                string.Equals(g.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task<List<DomainGrant>> GetGrantsByParentAsync(string parentId) => Task.FromResult(_grants);
        public Task UpsertGrantAsync(DomainGrant grant) => Task.CompletedTask;
        public Task<bool> RemoveGrantAsync(string id) => Task.FromResult(true);
    }

    /// <summary>Minimal IOptionsSnapshot stand-in — AccessService takes a snapshot for live config reload.</summary>
    private sealed class TestOptionsSnapshot<T> : IOptionsSnapshot<T> where T : class, new()
    {
        public TestOptionsSnapshot(T value) => Value = value;
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
