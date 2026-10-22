using Ava.Api.Config;
using Ava.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

/// <summary>
/// CRUD endpoints for template definitions.
/// Used by the workflow builder to list, create, and update templates.
/// </summary>
[ApiController]
[Route("api/templates")]
public class TemplatesController : ControllerBase
{
    private readonly ITemplateRepository _templates;
    private readonly ILogger<TemplatesController> _logger;

    public TemplatesController(ITemplateRepository templates, ILogger<TemplatesController> logger)
    {
        _templates = templates;
        _logger = logger;
    }

    /// <summary>GET /api/templates — List all template definitions</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var templates = await _templates.GetAllAsync();
        return Ok(new { count = templates.Count, templates });
    }

    /// <summary>GET /api/templates/{id} — Get a single template by ID</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var template = await _templates.GetByIdAsync(id);
        return template == null
            ? NotFound(new { error = $"Template '{id}' not found" })
            : Ok(template);
    }

    /// <summary>POST /api/templates — Create a new template</summary>
    [HttpPost]
    public IActionResult Create([FromBody] TemplateDefinition template)
    {
        if (string.IsNullOrWhiteSpace(template.Id))
            return BadRequest(new { error = "id is required" });

        _templates.Add(template);
        _logger.LogInformation("Template created: {Id}", template.Id);
        return CreatedAtAction(nameof(GetById), new { id = template.Id }, template);
    }

    /// <summary>PUT /api/templates/{id} — Update an existing template</summary>
    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] TemplateDefinition template)
    {
        template.Id = id;
        bool updated = _templates.Update(template);
        if (!updated)
            return NotFound(new { error = $"Template '{id}' not found" });

        _logger.LogInformation("Template updated: {Id}", id);
        return Ok(template);
    }

    /// <summary>GET /api/templates/archived — List archived templates</summary>
    [HttpGet("archived")]
    public async Task<IActionResult> GetArchived()
    {
        var templates = await _templates.GetArchivedAsync();
        return Ok(new { count = templates.Count, templates });
    }

    /// <summary>PUT /api/templates/{id}/archive — Archive a template</summary>
    [HttpPut("{id}/archive")]
    public async Task<IActionResult> Archive(string id)
    {
        var existing = await _templates.GetByIdAsync(id);
        if (existing == null)
            return NotFound(new { error = $"Template '{id}' not found" });

        await _templates.ArchiveAsync(id);
        _logger.LogInformation("Template archived: {Id}", id);
        return Ok(new { archived = true, id });
    }

    /// <summary>PUT /api/templates/{id}/restore — Restore an archived template</summary>
    [HttpPut("{id}/restore")]
    public async Task<IActionResult> Restore(string id)
    {
        await _templates.RestoreAsync(id);
        _logger.LogInformation("Template restored: {Id}", id);
        return Ok(new { restored = true, id });
    }
}
