using Ava.Api.Config;
using Ava.Api.Data;
using Ava.Api.Helpers;
using Ava.Api.Models;
using Ava.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Ava.Api.Controllers;

/// <summary>
/// Aperture-compatible parameterized prompt execution endpoint.
/// POST /api/prompt/execute — drop-in replacement for Aperture's POST /ai/submit.
/// Uses ChatClientFactory (GitHub Copilot or Azure AOAI) and records usage_events.
/// </summary>
[ApiController]
[Route("api/prompt")]
public class PromptController : ControllerBase
{
    private readonly IPromptRepository _prompts;
    private readonly ITemplateRepository _templates;
    private readonly AvaSettings _settings;
    private readonly IChatClientFactory _chatFactory;
    private readonly PostgresUsageRepository _usageRepo;
    private readonly ILogger<PromptController> _logger;

    public PromptController(
        IPromptRepository prompts,
        ITemplateRepository templates,
        IOptions<AvaSettings> settings,
        IChatClientFactory chatFactory,
        PostgresUsageRepository usageRepo,
        ILogger<PromptController> logger)
    {
        _prompts     = prompts;
        _templates   = templates;
        _settings    = settings.Value;
        _chatFactory = chatFactory;
        _usageRepo   = usageRepo;
        _logger      = logger;
    }

    /// <summary>
    /// POST /api/prompt/execute
    /// Executes a named prompt with the provided parameters.
    /// The prompt's system prompt is combined with parameter substitution to produce the response.
    /// </summary>
    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] PromptExecuteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "name is required" });

        // User tracking: identity comes from the Pylon JWT (see Program.cs middleware).
        var pylonUser = HttpContext.GetPylonUser();
        if (!pylonUser.IsPresent)
            return Unauthorized(new { error = "authentication required" });

        var prompt = await _prompts.GetByIdAsync(request.Name);
        if (prompt == null)
            return NotFound(new { error = $"Prompt '{request.Name}' not found." });

        _logger.LogInformation("PromptExecute: {Name} with {Count} parameters", request.Name, request.Parameters.Count);

        // Build user message from parameters
        var paramsSummary = request.Parameters.Any()
            ? string.Join("\n", request.Parameters.Select(kvp => $"{kvp.Key}: {kvp.Value}"))
            : "No parameters provided.";

        var systemPrompt = prompt.SystemPrompt;
        var userMessage  = $"Execute the task with the following inputs:\n\n{paramsSummary}";

        // Substitute {{tokens}} in the system prompt from parameters
        foreach (var kvp in request.Parameters)
            systemPrompt = systemPrompt.Replace($"{{{{{kvp.Key}}}}}", kvp.Value,
                StringComparison.OrdinalIgnoreCase);

        try
        {
            var modelId     = await _chatFactory.GetModelIdAsync(request.SessionId);
            var chatClient  = await _chatFactory.CreateAsync(request.SessionId);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userMessage)
            };

            var response   = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);
            var content    = response.Value.Content[0].Text.Trim();
            var usage      = response.Value.Usage;
            var totalTokens = usage?.TotalTokenCount ?? 0;
            var inputTokens = usage?.InputTokenCount  ?? 0;
            var outputTokens = usage?.OutputTokenCount ?? 0;

            // Compute estimated cost
            var estimatedCost = ComputeCost(modelId, inputTokens, outputTokens);

            // Write usage event to Postgres (no conversationId or userId for prompt/execute calls)
            var runId = Guid.NewGuid().ToString();
            await _usageRepo.InsertAsync(new UsageEvent(
                AgentRunId:       runId,
                ConversationId:   null,
                SessionId:        request.SessionId,
                UserId:           pylonUser.UserId,
                Username:         pylonUser.Username,
                Domain:           request.Domain ?? "unknown",
                ModelId:          modelId,
                InputTokens:      inputTokens,
                OutputTokens:     outputTokens,
                TotalTokens:      totalTokens,
                EstimatedCostUsd: estimatedCost,
                PromptId:         prompt.Id,
                CreatedAt:        DateTime.UtcNow));

            return Ok(new PromptExecuteResponse
            {
                PromptId   = prompt.Id,
                Content    = content,
                TokensUsed = totalTokens
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PromptExecute failed for {Name}", request.Name);
            return StatusCode(500, new { error = "Prompt execution failed. Please try again." });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private decimal ComputeCost(string? modelId, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrEmpty(modelId)) return 0m;

        if (!_settings.ModelPricing.TryGetValue(modelId, out var pricing))
        {
            var slash = modelId.IndexOf('/');
            if (slash >= 0)
                _settings.ModelPricing.TryGetValue(modelId[(slash + 1)..], out pricing);
        }

        if (pricing == null) return 0m;

        return (inputTokens  * pricing.InputPricePerMillionTokens  / 1_000_000m)
             + (outputTokens * pricing.OutputPricePerMillionTokens / 1_000_000m);
    }
}
