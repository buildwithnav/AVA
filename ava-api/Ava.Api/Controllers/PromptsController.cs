using Ava.Api.Config;
using Ava.Api.Models;
using Ava.Api.Services;
using Microsoft.AspNetCore.Mvc;
using OpenAI.Chat;

namespace Ava.Api.Controllers;

/// <summary>
/// Read-only endpoints for prompt definitions.
/// Used by the chat UI to display workflow suggestions and by the prompt builder for listing.
/// </summary>
[ApiController]
[Route("api/prompts")]
public class PromptsController : ControllerBase
{
    private readonly IPromptRepository _prompts;
    private readonly SemanticSearchService _semanticSearch;
    private readonly IChatClientFactory _chatFactory;
    private readonly ILogger<PromptsController> _logger;

    public PromptsController(
        IPromptRepository prompts,
        SemanticSearchService semanticSearch,
        IChatClientFactory chatFactory,
        ILogger<PromptsController> logger)
    {
        _prompts = prompts;
        _semanticSearch = semanticSearch;
        _chatFactory = chatFactory;
        _logger = logger;
    }

    /// <summary>GET /api/prompts — List all prompt definitions</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var prompts = await _prompts.GetAllAsync();
        return Ok(new { count = prompts.Count, prompts });
    }

    /// <summary>GET /api/prompts/domain/{domain} — List prompts for a domain</summary>
    [HttpGet("domain/{domain}")]
    public async Task<IActionResult> GetByDomain(string domain)
    {
        var prompts = await _prompts.GetByDomainAsync(domain);
        return Ok(new { count = prompts.Count, prompts });
    }

    /// <summary>GET /api/prompts/{id} — Get a single prompt by ID</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var prompt = await _prompts.GetByIdAsync(id);
        return prompt == null
            ? NotFound(new { error = $"Prompt '{id}' not found" })
            : Ok(prompt);
    }

    /// <summary>POST /api/prompts/search — Semantic/keyword search for prompts</summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] PromptSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { error = "query is required" });

        var topK = request.TopK > 0 ? request.TopK : 5;

        var results = await _semanticSearch.SearchAsync(request.Query, request.Domain, topK);
        if (results.Count == 0)
        {
            results = _semanticSearch.KeywordSearch(request.Query, request.Domain, topK);
        }

        var matches = results.Select(r => new
        {
            promptId = r.Prompt.Id,
            description = r.Prompt.Description,
            domain = r.Prompt.Domain,
            score = r.Score
        }).ToList();

        return Ok(new { count = matches.Count, matches });
    }

    /// <summary>POST /api/prompts — Create a new prompt definition</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PromptDefinition prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt.Id))
            return BadRequest(new { error = "id is required" });
        if (string.IsNullOrWhiteSpace(prompt.Domain))
            return BadRequest(new { error = "domain is required" });

        var existing = await _prompts.GetByIdAsync(prompt.Id);
        if (existing != null)
            return Conflict(new { error = $"Prompt '{prompt.Id}' already exists. Use PUT to update." });

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(prompt.SystemPrompt))
            warnings.Add("systemPrompt is empty");

        var created = await _prompts.CreateAsync(prompt);
        return Ok(new { prompt = created, warnings });
    }

    /// <summary>PUT /api/prompts/{id} — Update an existing prompt definition</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] PromptDefinition prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt.Domain))
            return BadRequest(new { error = "domain is required" });

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(prompt.SystemPrompt))
            warnings.Add("systemPrompt is empty");

        var updated = await _prompts.UpdateAsync(id, prompt);
        if (updated == null)
            return NotFound(new { error = $"Prompt '{id}' not found" });

        return Ok(new { prompt = updated, warnings });
    }

    /// <summary>GET /api/prompts/archived — List archived prompts/workflows</summary>
    [HttpGet("archived")]
    public async Task<IActionResult> GetArchived()
    {
        var prompts = await _prompts.GetArchivedAsync();
        return Ok(new { count = prompts.Count, prompts });
    }

    /// <summary>PUT /api/prompts/{id}/archive — Archive a prompt/workflow</summary>
    [HttpPut("{id}/archive")]
    public async Task<IActionResult> Archive(string id)
    {
        var existing = await _prompts.GetByIdAsync(id);
        if (existing == null)
            return NotFound(new { error = $"Prompt '{id}' not found" });

        await _prompts.ArchiveAsync(id);
        _logger.LogInformation("Prompt archived: {Id}", id);

        // Rebuild semantic index so archived workflow is immediately removed from routing
        var activePrompts = await _prompts.GetAllAsync();
        _ = _semanticSearch.BuildIndexAsync(activePrompts);

        return Ok(new { archived = true, id });
    }

    /// <summary>PUT /api/prompts/{id}/restore — Restore an archived prompt/workflow</summary>
    [HttpPut("{id}/restore")]
    public async Task<IActionResult> Restore(string id)
    {
        await _prompts.RestoreAsync(id);
        _logger.LogInformation("Prompt restored: {Id}", id);

        // Rebuild semantic index so restored workflow is available for routing again
        var activePrompts = await _prompts.GetAllAsync();
        _ = _semanticSearch.BuildIndexAsync(activePrompts);

        return Ok(new { restored = true, id });
    }

    /// <summary>
    /// POST /api/prompts/generate-sample — Use AI to generate a sample prompt
    /// from the workflow definition (metadata + stages).
    /// </summary>
    [HttpPost("generate-sample")]
    public async Task<IActionResult> GenerateSample([FromBody] PromptDefinition prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt.Id) || string.IsNullOrWhiteSpace(prompt.Description))
            return BadRequest(new { error = "id and description are required" });

        var stagesSummary = string.Empty;
        if (prompt.Stages is { Count: > 0 })
        {
            var stageLines = prompt.Stages.Select((s, i) =>
            {
                var detail = s.Type switch
                {
                    "tool" => $"calls tool '{s.ToolId}'",
                    "ai-extract" => $"extracts: {Truncate(s.Prompt, 80)}",
                    "ai-generate" => $"generates: {Truncate(s.Prompt, 80)}",
                    "ai-analyze" => $"analyzes: {Truncate(s.Prompt, 80)}",
                    "user-select" => $"user selects from '{s.OptionsFrom}'",
                    "confirmation" => "asks for confirmation",
                    "foreach" => $"loops over '{s.ItemsFrom}' with {s.Stages?.Count ?? 0} sub-stages",
                    _ => s.Type
                };
                return $"  {i + 1}. [{s.Type}] {detail}";
            });
            stagesSummary = $"\nWorkflow stages:\n{string.Join("\n", stageLines)}";
        }

        var systemMessage = """
            You are a UX copywriter for an enterprise assistant called AVA.
            Given a workflow description and its stages, generate a single realistic
            example prompt that a user would type into the chat box to trigger this workflow.
            The prompt should be natural, concise (one or two sentences), and specific enough
            to show what kind of request the workflow handles. Do NOT include any explanation,
            just output the example prompt text.
            """;

        var userMessage = $"""
            Workflow ID: {prompt.Id}
            Description: {prompt.Description}
            Domain: {prompt.Domain}
            Context tags: {string.Join(", ", prompt.ContextTags)}{stagesSummary}
            """;

        try
        {
            var chatClient = await _chatFactory.CreateAsync(null, prompt.ModelId);
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemMessage),
                new UserChatMessage(userMessage)
            };

            var options = new ChatCompletionOptions { MaxOutputTokenCount = 150, Temperature = 0.7f };
            var completion = await chatClient.CompleteChatAsync(messages, options, ct);

            var generated = completion.Value.Content[0].Text.Trim().Trim('"');
            return Ok(new { samplePrompt = generated });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate sample prompt for '{Id}'", prompt.Id);
            return StatusCode(500, new { error = "Failed to generate sample prompt" });
        }
    }

    private static string Truncate(string? text, int max)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";
}

public class PromptSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public int TopK { get; set; } = 5;
}
