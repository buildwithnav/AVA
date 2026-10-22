using Ava.Api.Config;
using Ava.Api.Models;
using Ava.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

/// <summary>
/// Domains, their children (e.g. individual studies under "Clinical Studies"), and the
/// AVA-native grants that gate children — folded into one controller since all three are
/// the same resource family and always change together. Children are full <c>DomainDefinition</c>
/// rows (self-referencing <c>ParentId</c>, capped at 2 tiers), so the same CRUD actions serve
/// both top-level domains and their children; only the <c>/children</c> and <c>/grants</c>
/// sub-routes are child-specific.
/// </summary>
[ApiController]
[Route("api/domains")]
public class DomainController : ControllerBase
{
    private readonly IDomainRepository _domains;
    private readonly AccessService _access;
    private readonly ILogger<DomainController> _logger;

    public DomainController(IDomainRepository domains, AccessService access, ILogger<DomainController> logger)
    {
        _domains = domains;
        _access = access;
        _logger = logger;
    }

    // ── Domains ──────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/domains — Returns enabled (active) top-level domains the caller can browse.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var domains = await _domains.GetAllAsync();
        var visible = await _access.FilterDomainsAsync(domains);
        return Ok(visible.OrderBy(d => d.Name).ToList());
    }

    /// <summary>
    /// GET /api/domains/all — Returns all top-level domains including archived, filtered by access.
    /// </summary>
    [HttpGet("all")]
    public async Task<IActionResult> GetAllIncludingArchived()
    {
        var domains = await _domains.GetAllIncludingArchivedAsync();
        var visible = await _access.FilterDomainsAsync(domains);
        return Ok(visible.OrderBy(d => d.Name).ToList());
    }

    /// <summary>
    /// GET /api/domains/{id} — Get a single domain (top-level or child) by ID.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var domain = await _domains.GetByIdAsync(id);
        if (domain == null)
            return NotFound(new { error = $"Domain '{id}' not found." });

        if (!await _access.HasDomainAccessAsync(id))
            return AccessDenied();

        return Ok(domain);
    }

    /// <summary>
    /// POST /api/domains — Create a new domain. Set ParentId to create a child domain
    /// (e.g. a study) — nesting is capped at 2 tiers, so the parent must itself be top-level.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DomainDefinition domain)
    {
        if (!await _access.HasModuleAsync(ModuleKeys.DomainAdmin, ModuleAccess.Insert))
            return AccessDenied();

        if (string.IsNullOrWhiteSpace(domain.Id))
            return BadRequest(new { error = "Domain ID is required." });

        if (!string.IsNullOrWhiteSpace(domain.ParentId))
        {
            var capError = await ValidateParentAsync(domain.ParentId, ModuleAccess.Insert);
            if (capError != null) return capError;
        }

        var existing = await _domains.GetByIdAsync(domain.Id);
        if (existing != null)
            return Conflict(new { error = $"Domain '{domain.Id}' already exists." });

        domain.Enabled = true;
        await _domains.AddAsync(domain);
        _logger.LogInformation("Created domain '{DomainId}'{Parent}",
            domain.Id, domain.ParentId != null ? $" under '{domain.ParentId}'" : "");
        return CreatedAtAction(nameof(GetById), new { id = domain.Id }, domain);
    }

    /// <summary>
    /// PUT /api/domains/{id} — Update an existing domain (top-level or child).
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] DomainDefinition domain)
    {
        if (!await _access.HasModuleAsync(ModuleKeys.DomainAdmin, ModuleAccess.Update) ||
            !await _access.HasDomainAccessAsync(id, ModuleAccess.Update))
            return AccessDenied();

        if (!string.IsNullOrWhiteSpace(domain.ParentId))
        {
            var capError = await ValidateParentAsync(domain.ParentId, ModuleAccess.Update);
            if (capError != null) return capError;
        }

        domain.Id = id;
        var success = await _domains.UpdateAsync(domain);
        if (!success)
            return NotFound(new { error = $"Domain '{id}' not found." });

        _logger.LogInformation("Updated domain '{DomainId}'", id);
        return Ok(domain);
    }

    /// <summary>
    /// PUT /api/domains/{id}/archive — Soft-delete (archive) a domain.
    /// </summary>
    [HttpPut("{id}/archive")]
    public async Task<IActionResult> Archive(string id)
    {
        if (!await _access.HasModuleAsync(ModuleKeys.DomainAdmin, ModuleAccess.Delete) ||
            !await _access.HasDomainAccessAsync(id, ModuleAccess.Delete))
            return AccessDenied();

        var success = await _domains.ArchiveAsync(id);
        if (!success)
            return NotFound(new { error = $"Domain '{id}' not found." });

        _logger.LogInformation("Archived domain '{DomainId}'", id);
        return Ok(new { archived = true, id });
    }

    /// <summary>
    /// PUT /api/domains/{id}/restore — Restore an archived domain.
    /// </summary>
    [HttpPut("{id}/restore")]
    public async Task<IActionResult> Restore(string id)
    {
        if (!await _access.HasModuleAsync(ModuleKeys.DomainAdmin, ModuleAccess.Update) ||
            !await _access.HasDomainAccessAsync(id, ModuleAccess.Update))
            return AccessDenied();

        var success = await _domains.RestoreAsync(id);
        if (!success)
            return NotFound(new { error = $"Domain '{id}' not found." });

        _logger.LogInformation("Restored domain '{DomainId}'", id);
        return Ok(new { restored = true, id });
    }

    /// <summary>
    /// DELETE /api/domains/{id} — Permanently remove a domain and its grants. Children only —
    /// top-level domains can only be archived via the API. Archive is preferred for children
    /// too; this exists for cleaning up mistakes and test data.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!await _access.HasModuleAsync(ModuleKeys.DomainAdmin, ModuleAccess.Delete) ||
            !await _access.HasDomainAccessAsync(id, ModuleAccess.Delete))
            return AccessDenied();

        var domain = await _domains.GetByIdAsync(id);
        if (domain == null)
            return NotFound(new { error = $"Domain '{id}' not found." });
        if (string.IsNullOrWhiteSpace(domain.ParentId))
            return BadRequest(new { error = "Top-level domains cannot be permanently deleted — archive instead." });

        var success = await _domains.DeleteAsync(id);
        if (!success)
            return NotFound(new { error = $"Domain '{id}' not found." });

        _logger.LogInformation("Deleted domain '{DomainId}' (child of '{ParentId}')", id, domain.ParentId);
        return Ok(new { deleted = true, id });
    }

    // ── Children ─────────────────────────────────────────────────────────────
    // e.g. individual studies under "Clinical Studies". Domain admins managing the hierarchy
    // see every child regardless of their own grants. Selection clients opt into grant filtering
    // so administrative hierarchy access remains separate from end-user chat access.

    [HttpGet("{parentId}/children")]
    public async Task<IActionResult> GetChildren(
        string parentId,
        [FromQuery] bool includeArchived = false,
        [FromQuery] bool filterForSelection = false)
    {
        if (!await _access.HasDomainAccessAsync(parentId))
            return AccessDenied();

        var all = await _domains.GetChildrenAsync(parentId, includeArchived);

        if (!filterForSelection && await _access.HasModuleAsync(ModuleKeys.DomainAdmin))
            return Ok(all);

        var visible = await _access.FilterChildDomainsAsync(parentId, all);
        return Ok(visible);
    }

    // ── Grants ───────────────────────────────────────────────────────────────
    // AVA-native (not gateway modules) because children number in the dozens per domain —
    // registering each with CDS isn't practical. Administering grants requires DOMAIN_ADMIN.

    [HttpGet("{parentId}/grants")]
    public async Task<IActionResult> GetGrants(string parentId)
    {
        if (!await _access.HasModuleAsync(ModuleKeys.DomainAdmin) ||
            !await _access.HasDomainAccessAsync(parentId))
            return AccessDenied();

        return Ok(await _domains.GetGrantsByParentAsync(parentId));
    }

    [HttpPut("{parentId}/grants")]
    public async Task<IActionResult> UpsertGrant(string parentId, [FromBody] DomainGrant grant)
    {
        if (!await _access.HasModuleAsync(ModuleKeys.DomainAdmin, ModuleAccess.Update) ||
            !await _access.HasDomainAccessAsync(parentId, ModuleAccess.Update))
            return AccessDenied();

        if (string.IsNullOrWhiteSpace(grant.PrincipalId))
            return BadRequest(new { error = "PrincipalId is required." });

        grant.ParentId = parentId;
        if (grant.PrincipalType != DomainGrant.PrincipalGroup)
            grant.PrincipalType = DomainGrant.PrincipalUser;

        await _domains.UpsertGrantAsync(grant);
        _logger.LogInformation("Upserted grant for {Principal} on {ParentId}/{DomainId}",
            grant.PrincipalId, parentId, grant.DomainId ?? "*");
        return Ok(grant);
    }

    [HttpDelete("{parentId}/grants/{id}")]
    public async Task<IActionResult> RemoveGrant(string parentId, string id)
    {
        if (!await _access.HasModuleAsync(ModuleKeys.DomainAdmin, ModuleAccess.Delete) ||
            !await _access.HasDomainAccessAsync(parentId, ModuleAccess.Delete))
            return AccessDenied();

        var removed = await _domains.RemoveGrantAsync(id);
        if (!removed)
            return NotFound(new { error = $"Grant '{id}' not found." });

        return Ok(new { removed = true, id });
    }

    /// <summary>Validates the 2-tier nesting cap and that the caller can manage the parent.</summary>
    private async Task<IActionResult?> ValidateParentAsync(string parentId, ModuleAccess access)
    {
        var parent = await _domains.GetByIdAsync(parentId);
        if (parent == null)
            return BadRequest(new { error = $"Parent domain '{parentId}' not found." });
        if (!string.IsNullOrWhiteSpace(parent.ParentId))
            return BadRequest(new { error = "Child domains cannot themselves have children (2-tier limit)." });
        if (!await _access.HasDomainAccessAsync(parentId, access))
            return AccessDenied();

        return null;
    }

    private IActionResult AccessDenied(string? message = null) =>
        StatusCode(StatusCodes.Status403Forbidden, new { error = message ?? "You do not have access to this resource." });
}
