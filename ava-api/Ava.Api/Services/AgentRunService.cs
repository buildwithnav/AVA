using Ava.Api.Config;
using Ava.Api.Data;
using Ava.Api.Models;
using Microsoft.Extensions.Options;

namespace Ava.Api.Services;

/// <summary>
/// Tracks agent runs. Run state is persisted to Redis (ISessionStore) for fast
/// in-flight access. On completion, a usage_events row and conversation archive rows
/// are also written to Postgres for durable analytics and traceability.
/// </summary>
public class AgentRunService
{
    private readonly ISessionStore _store;
    private readonly PostgresUsageRepository _usageRepo;
    private readonly PostgresConversationRepository _convRepo;
    private readonly AvaSettings _settings;
    private readonly ILogger<AgentRunService> _logger;

    public AgentRunService(
        ISessionStore store,
        PostgresUsageRepository usageRepo,
        PostgresConversationRepository convRepo,
        IOptions<AvaSettings> settings,
        ILogger<AgentRunService> logger)
    {
        _store    = store;
        _usageRepo = usageRepo;
        _convRepo  = convRepo;
        _settings  = settings.Value;
        _logger    = logger;
    }

    public async Task<AgentRun> CreateAsync(string conversationId, string userId, string domain, string input, string username = "")
    {
        var run = new AgentRun
        {
            ConversationId = conversationId,
            UserId = userId,
            Username = username,
            Domain = domain,
            Input = input,
            Status = AgentRunStatus.Running
        };
        await _store.SaveRunAsync(run);
        await _store.LinkConversationToSessionAsync(userId, conversationId);
        _logger.LogInformation("AgentRun {Id} created for {User} in {Domain}", run.AgentRunId, userId, domain);
        return run;
    }

    public async Task<AgentRun?> GetAsync(string runId) =>
        await _store.GetRunAsync(runId);

    public async Task<IReadOnlyList<AgentRun>> GetByConversationAsync(string conversationId) =>
        await _store.GetRunsByConversationAsync(conversationId);

    /// <summary>
    /// Returns the most recent run for this conversation that is paused at a refinable stage
    /// (ai-generate or ai-analyze). Returns null if no such run exists or if it's stale (>10 min).
    /// </summary>
    public async Task<AgentRun?> GetLastRefinableRunAsync(string conversationId)
    {
        var refinablePauseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ai-generate", "ai-analyze" };

        var runs = await _store.GetRunsByConversationAsync(conversationId);
        return runs
            .Where(r => r.Status == AgentRunStatus.WaitingForInput
                && r.MatchedPromptId != null
                && r.WorkflowContext?.PauseType != null
                && refinablePauseTypes.Contains(r.WorkflowContext.PauseType)
                && r.StartedAt > DateTime.UtcNow.AddMinutes(-10))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefault();
    }

    public async Task CompleteAsync(AgentRun run, string finalResponse, int tokensUsed)
    {
        run.FinalResponse = finalResponse;
        run.Status        = AgentRunStatus.Completed;
        run.CompletedAt   = DateTime.UtcNow;
        run.TokensUsed    = tokensUsed;

        // Compute estimated cost from ModelPricing config
        run.EstimatedCostUsd = ComputeCost(run.ModelId, run.InputTokensUsed, run.OutputTokensUsed);

        // 1. Persist run state to Redis (fast, operational)
        await _store.SaveRunAsync(run);

        // 2. Write usage_events row to Postgres (durable analytics)
        await _usageRepo.InsertAsync(new UsageEvent(
            AgentRunId:       run.AgentRunId,
            ConversationId:   run.ConversationId,
            SessionId:        run.SessionId,
            UserId:           run.UserId,
            Username:         run.Username,
            Domain:           run.Domain,
            ModelId:          run.ModelId,
            InputTokens:      run.InputTokensUsed,
            OutputTokens:     run.OutputTokensUsed,
            TotalTokens:      run.TokensUsed,
            EstimatedCostUsd: run.EstimatedCostUsd,
            PromptId:         run.MatchedPromptId,
            CreatedAt:        run.CompletedAt.Value));

        // 3. Archive conversation turn to Postgres (durable traceability)
        // Only archive when there's a real user message and a response to store.
        if (!string.IsNullOrEmpty(run.Input) && !string.IsNullOrEmpty(finalResponse)
            && !string.IsNullOrEmpty(run.ConversationId))
        {
            await _convRepo.UpsertConversationAsync(new ConversationRecord(
                Id:        run.ConversationId,
                SessionId: run.SessionId,
                UserId:    run.UserId,
                Username:  run.Username,
                Domain:    run.Domain,
                CreatedAt: run.StartedAt,
                UpdatedAt: run.CompletedAt.Value));

            await _convRepo.AppendMessagesAsync(
                run.ConversationId,
                run.AgentRunId,
                run.Input,
                finalResponse);
        }
    }

    /// <summary>
    /// Computes estimated USD cost from configured ModelPricing.
    /// Returns 0 if the model is not in the pricing table.
    /// </summary>
    private decimal ComputeCost(string? modelId, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrEmpty(modelId)) return 0m;

        // Try exact match first, then strip provider prefix (e.g. "azure-openai/gpt-4.1" → "gpt-4.1")
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

    public async Task FailAsync(AgentRun run, string error)
    {
        run.FinalResponse = error;
        run.Status = AgentRunStatus.Failed;
        run.CompletedAt = DateTime.UtcNow;
        await _store.SaveRunAsync(run);
    }

    /// <summary>
    /// Pauses the run for workflow input (clarify, user-select, ai-generate, confirmation).
    /// </summary>
    public async Task PauseForInputAsync(AgentRun run, WorkflowContext ctx, string pauseType)
    {
        ctx.PauseType = pauseType;
        run.Status = AgentRunStatus.WaitingForInput;
        run.WorkflowContext = ctx;
        await _store.SaveRunAsync(run);
        _logger.LogInformation("AgentRun {Id} paused — type={PauseType}", run.AgentRunId, pauseType);
    }

    /// <summary>Saves the current workflow context onto the run.</summary>
    public async Task SaveWorkflowContextAsync(AgentRun run, WorkflowContext ctx)
    {
        run.WorkflowContext = ctx;
        await _store.SaveRunAsync(run);
    }

    /// <summary>
    /// Merges a SelectRequest into the workflow context and advances past the pause stage.
    /// </summary>
    public async Task MergeSelectionAsync(AgentRun run, SelectRequest selection)
    {
        if (run.WorkflowContext == null) return;
        var ctx = run.WorkflowContext;

        switch (selection.InputType)
        {
            case "select" when selection.SelectedIndex.HasValue:
                ctx.Fields["_selectedIndex"] = selection.SelectedIndex.Value;
                if (ctx.PausePayload is { } payload)
                {
                    var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
                        if (doc.RootElement.TryGetProperty("options", out var opts))
                        {
                            var optList = opts.EnumerateArray().ToList();
                            if (selection.SelectedIndex.Value < optList.Count)
                            {
                                var selected = optList[selection.SelectedIndex.Value];
                                var selectedJson = selected.GetProperty("value").GetString();
                                var stageId = doc.RootElement.TryGetProperty("stageId", out var sid) ? sid.GetString() : "select";
                                ctx.Fields[$"_result_{stageId}"] = selectedJson;

                                if (doc.RootElement.TryGetProperty("outputMap", out var outputMapElem) && outputMapElem.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (!string.IsNullOrEmpty(selectedJson))
                                    {
                                        try
                                        {
                                            using var selectedDoc = System.Text.Json.JsonDocument.Parse(selectedJson);
                                            foreach (var mapping in outputMapElem.EnumerateObject())
                                            {
                                                var targetField = mapping.Name;
                                                var sourceField = mapping.Value.GetString();
                                                if (!string.IsNullOrEmpty(sourceField) && selectedDoc.RootElement.TryGetProperty(sourceField, out var sourceValue))
                                                {
                                                    var strValue = sourceValue.ValueKind switch
                                                    {
                                                        System.Text.Json.JsonValueKind.String => sourceValue.GetString(),
                                                        System.Text.Json.JsonValueKind.Number => sourceValue.ToString(),
                                                        _ => sourceValue.ToString()
                                                    };
                                                    if (strValue != null)
                                                        ctx.Fields[targetField] = strValue;
                                                }
                                            }
                                        }
                                        catch { /* ignore extraction errors */ }
                                    }
                                }
                            }
                        }
                    }
                    catch { /* ignore parse errors */ }
                }
                break;

            case "confirm":
                if (selection.ConfirmedData != null)
                {
                    foreach (var kvp in selection.ConfirmedData)
                        ctx.Fields[kvp.Key] = kvp.Value;
                }
                break;

            case "dismiss":
                run.Status = AgentRunStatus.Cancelled;
                await _store.SaveRunAsync(run);
                return;
        }

        // Advance past the pause stage
        ctx.CurrentStageIndex++;
        ctx.PauseType = null;
        ctx.PausePayload = null;
        run.Status = AgentRunStatus.Running;
        await _store.SaveRunAsync(run);
    }
}
