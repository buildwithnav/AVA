using System.Runtime.CompilerServices;
using Ava.Api.Config;
using Ava.Api.Models;
using Ava.Api.Services.Stages;

namespace Ava.Api.Services;

/// <summary>
/// Executes staged prompt workflows by dispatching each stage to its registered
/// <see cref="IStageHandler"/>. Owns the stage loop, pause/resume logic, and
/// context persistence — but no stage-type-specific logic.
/// </summary>
public class WorkflowEngine
{
    private readonly Dictionary<string, IStageHandler> _handlers;
    private readonly IPromptRepository _prompts;
    private readonly IToolRepository _tools;
    private readonly ConversationService _conversation;
    private readonly AgentRunService _agentRun;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IEnumerable<IStageHandler> handlers,
        IPromptRepository prompts,
        IToolRepository tools,
        ConversationService conversation,
        AgentRunService agentRun,
        ILogger<WorkflowEngine> logger)
    {
        _handlers     = handlers.ToDictionary(h => h.StageType, h => h, StringComparer.OrdinalIgnoreCase);
        _prompts      = prompts;
        _tools        = tools;
        _conversation = conversation;
        _agentRun     = agentRun;
        _logger       = logger;

        _logger.LogInformation("WorkflowEngine: registered stage handlers: {Types}",
            string.Join(", ", _handlers.Keys));
    }

    // ── Public entry points ───────────────────────────────────────────────────

    /// <summary>Start a fresh staged workflow execution.</summary>
    public async IAsyncEnumerable<StreamEvent> ExecuteAsync(
        AgentRun run,
        PromptDefinition prompt,
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ctx = new WorkflowContext
        {
            PromptId = prompt.Id,
            OriginalMessage = request.Message,
            CurrentStageIndex = 0,
            // Propagate per-workflow model override so all AI stage handlers use the same model
            ModelOverride = string.IsNullOrEmpty(prompt.ModelId) ? null : prompt.ModelId,
            // Carry prior conversation into the workflow when PreserveContext is enabled.
            // AgentService populates request.ConversationHistory only in that case.
            ConversationHistory = (prompt.PreserveContext && request.ConversationHistory is { Count: > 0 })
                ? request.ConversationHistory
                : null
        };
        await _agentRun.SaveWorkflowContextAsync(run, ctx);

        await foreach (var evt in RunStagesAsync(run, prompt, ctx, request, request.SessionId, ct))
            yield return evt;
    }

    /// <summary>
    /// Resume a paused workflow. Called when the client sends a new ChatRequest
    /// with ResumeRunId set.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> ResumeAsync(
        AgentRun run,
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (run.WorkflowContext == null)
        {
            yield return new StreamEvent { Type = "error", Data = "No workflow context found for run." };
            yield break;
        }

        var ctx = run.WorkflowContext;
        var prompt = await _prompts.GetByIdAsync(ctx.PromptId);
        if (prompt?.Stages == null)
        {
            yield return new StreamEvent { Type = "error", Data = $"Prompt '{ctx.PromptId}' not found or has no stages." };
            yield break;
        }

        // Only apply resume logic (advance past pause) when there's an active pause.
        // After a rewind, PauseType is null and CurrentStageIndex is already set to the target.
        await foreach (var evt in ResumeCoreAsync(run, prompt, ctx, request, request.SessionId, ct))
            yield return evt;
    }

    /// <summary>
    /// Resume a paused workflow with an inline prompt definition (for test-workflow
    /// where the workflow may not be saved to the repository).
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> ResumeAsync(
        AgentRun run,
        ChatRequest request,
        PromptDefinition prompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (run.WorkflowContext == null)
        {
            yield return new StreamEvent { Type = "error", Data = "No workflow context found for run." };
            yield break;
        }

        if (prompt?.Stages == null)
        {
            yield return new StreamEvent { Type = "error", Data = "Workflow definition has no stages." };
            yield break;
        }

        var ctx = run.WorkflowContext;

        await foreach (var evt in ResumeCoreAsync(run, prompt, ctx, request, request.SessionId, ct))
            yield return evt;
    }

    /// <summary>
    /// Shared resume pipeline for both saved and inline-prompt resumes. Applies the
    /// pending pause's resume logic (advance / cancel / correction-rewind) and then
    /// runs the remaining stages. A confirmation stage whose reply is a free-text
    /// correction rewinds to an earlier stage and threads the correction into context.
    /// </summary>
    private async IAsyncEnumerable<StreamEvent> ResumeCoreAsync(
        AgentRun run,
        PromptDefinition prompt,
        WorkflowContext ctx,
        ChatRequest request,
        string? sessionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Apply resume logic only when there's an active pause. After a UI-driven
        // rewind, PauseType is null and CurrentStageIndex already points at the target.
        if (ctx.PauseType != null)
        {
            var currentStage = prompt.Stages![ctx.CurrentStageIndex];
            var isForeach = currentStage.Type.Equals("foreach", StringComparison.OrdinalIgnoreCase);

            // Top-level confirmation stages get correction-aware handling: a free-text
            // reply (not a plain confirm/cancel) is treated as a correction that rewinds
            // to an earlier stage instead of silently ending the workflow.
            if (ctx.PauseType == "confirmation" && !isForeach)
            {
                var firstMutatingIndex = await GetFirstMutatingToolIndexAsync(prompt);
                var (blocked, message) = ApplyConfirmationResume(run, prompt, ctx, request, firstMutatingIndex);
                if (blocked)
                {
                    run.Status = AgentRunStatus.WaitingForInput;
                    await _agentRun.SaveWorkflowContextAsync(run, ctx);
                    yield return new StreamEvent
                    {
                        Type = "confirmation_required",
                        Data = new { run.AgentRunId, prompt = message }
                    };
                    yield return new StreamEvent
                    {
                        Type = "done",
                        Data = new { run.AgentRunId, resumeRunId = run.AgentRunId }
                    };
                    yield break;
                }
            }
            else
            {
                HandleResume(ctx, prompt, request);
            }
        }

        run.Status = AgentRunStatus.Running;
        await foreach (var evt in RunStagesAsync(run, prompt, ctx, request, sessionId, ct))
            yield return evt;
    }

    // ── Rewind logic ──────────────────────────────────────────────────────────

    /// <summary>
    /// Computes which user-select stages are safe rewind targets.
    /// A user-select stage is rewindable if it appears before any mutating tool stage
    /// (POST, PATCH, DELETE, PUT) in the pipeline.
    /// </summary>
    public async Task<List<RewindableStageInfo>> GetRewindableStagesAsync(PromptDefinition prompt)
    {
        var result = new List<RewindableStageInfo>();
        if (prompt.Stages == null) return result;

        var mutatingMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "POST", "PATCH", "DELETE", "PUT" };

        bool pastMutatingTool = false;

        foreach (var stage in prompt.Stages)
        {
            if (stage.Type.Equals("tool", StringComparison.OrdinalIgnoreCase) && !pastMutatingTool)
            {
                var toolId = stage.ToolId;
                if (!string.IsNullOrEmpty(toolId))
                {
                    var tool = await _tools.GetByIdAsync(toolId);
                    if (tool != null && mutatingMethods.Contains(tool.HttpMethod))
                        pastMutatingTool = true;
                }
                if (stage.PrePrompt?.AllowedToolIds != null)
                {
                    foreach (var id in stage.PrePrompt.AllowedToolIds)
                    {
                        var tool = await _tools.GetByIdAsync(id);
                        if (tool != null && mutatingMethods.Contains(tool.HttpMethod))
                        {
                            pastMutatingTool = true;
                            break;
                        }
                    }
                }
            }

            if (stage.Type.Equals("user-select", StringComparison.OrdinalIgnoreCase) && !pastMutatingTool)
            {
                result.Add(new RewindableStageInfo
                {
                    StageId = stage.StageId,
                    StageIndex = prompt.Stages.IndexOf(stage),
                    Label = stage.SelectPrompt ?? stage.StageId
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Rewinds a paused workflow to a target stage, rolling back all fields set
    /// by that stage and subsequent stages using the field journal.
    /// </summary>
    public async Task<(bool Success, string? Error)> RewindToStageAsync(AgentRun run, string targetStageId)
    {
        if (run.Status != AgentRunStatus.WaitingForInput && run.Status != AgentRunStatus.Completed)
            return (false, "Run is not in a rewindable state (must be paused or completed).");

        if (run.WorkflowContext == null)
            return (false, "No workflow context found for run.");

        var ctx = run.WorkflowContext;
        var prompt = await _prompts.GetByIdAsync(ctx.PromptId);
        if (prompt?.Stages == null)
            return (false, $"Prompt '{ctx.PromptId}' not found or has no stages.");

        var targetIndex = prompt.Stages.FindIndex(s =>
            s.StageId.Equals(targetStageId, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
            return (false, $"Stage '{targetStageId}' not found in prompt '{ctx.PromptId}'.");

        var rewindable = await GetRewindableStagesAsync(prompt);
        if (!rewindable.Any(r => r.StageId.Equals(targetStageId, StringComparison.OrdinalIgnoreCase)))
            return (false, $"Stage '{targetStageId}' is not a rewindable stage (it may be past a mutating tool).");

        // Collect stageIds at or after the target index
        // Collect stageIds at or after the target index, roll back their field-journal
        // entries and step results, and move the cursor to the target.
        RollbackToStage(ctx, run, prompt.Stages, targetIndex);

        ctx.PauseType = null;
        ctx.PausePayload = null;
        ctx.Revision++;
        run.Status = AgentRunStatus.Running;

        _logger.LogInformation(
            "WorkflowEngine: rewind run={RunId} to stage={StageId} (index={Idx}, revision={Rev})",
            run.AgentRunId, targetStageId, targetIndex, ctx.Revision);

        return (true, null);
    }

    /// <summary>
    /// Rolls back all field-journal entries and step results for stages at or after
    /// <paramref name="targetIndex"/>, restoring prior field values, and sets the stage
    /// cursor to the target. Shared by UI-driven rewind and confirmation corrections so
    /// re-running stages don't leave stale fields or duplicate step results behind.
    /// </summary>
    private static void RollbackToStage(WorkflowContext ctx, AgentRun run, List<WorkflowStage> stages, int targetIndex)
    {
        var rolledBackStageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = targetIndex; i < stages.Count; i++)
            rolledBackStageIds.Add(stages[i].StageId);

        // Roll back field journal in reverse order
        var journalToRestore = ctx.FieldJournal
            .Where(e => rolledBackStageIds.Contains(e.StageId))
            .ToList();

        for (int i = journalToRestore.Count - 1; i >= 0; i--)
        {
            var entry = journalToRestore[i];
            if (entry.HadPreviousValue)
                ctx.Fields[entry.FieldName] = entry.PreviousValue;
            else
                ctx.Fields.Remove(entry.FieldName);
        }

        ctx.FieldJournal.RemoveAll(e => rolledBackStageIds.Contains(e.StageId));
        run.Steps.RemoveAll(s => rolledBackStageIds.Contains(s.StepId));

        ctx.CurrentStageIndex = targetIndex;
    }

    /// <summary>
    /// Index of the first stage that runs a mutating tool (POST/PATCH/PUT/DELETE), or
    /// int.MaxValue if none. Used to decide whether a confirmation correction can safely
    /// rewind without re-executing an already-applied change.
    /// </summary>
    private async Task<int> GetFirstMutatingToolIndexAsync(PromptDefinition prompt)
    {
        var stages = prompt.Stages;
        if (stages == null) return int.MaxValue;

        var mutatingMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "POST", "PATCH", "DELETE", "PUT" };

        for (int i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            if (!stage.Type.Equals("tool", StringComparison.OrdinalIgnoreCase)) continue;

            var toolIds = new List<string>();
            if (!string.IsNullOrEmpty(stage.ToolId)) toolIds.Add(stage.ToolId);
            if (stage.PrePrompt?.AllowedToolIds != null) toolIds.AddRange(stage.PrePrompt.AllowedToolIds);

            foreach (var id in toolIds)
            {
                var tool = await _tools.GetByIdAsync(id);
                if (tool != null && mutatingMethods.Contains(tool.HttpMethod))
                    return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// Resume handling for a top-level confirmation stage. Returns (blocked, message):
    /// blocked=true means the workflow stays paused (with an explanatory message) because
    /// the requested correction would require rewinding past an already-executed change.
    /// Otherwise the stage cursor / context are updated in place (advance, cancel, or
    /// correction-rewind) and blocked=false.
    /// </summary>
    private (bool Blocked, string? Message) ApplyConfirmationResume(
        AgentRun run, PromptDefinition prompt, WorkflowContext ctx, ChatRequest request, int firstMutatingIndex)
    {
        var stages = prompt.Stages!;
        var reply = request.Message.Trim().ToLowerInvariant().TrimEnd('.', '!', ' ');
        var currentStage = stages[ctx.CurrentStageIndex];

        // 1) Plain confirmation → advance to the next stage.
        if (IsStrictConfirmation(reply))
        {
            ctx.CurrentStageIndex++;
            ctx.PauseType = null;
            ctx.PausePayload = null;
            return (false, null);
        }

        // 2) Explicit cancel → end the workflow (preserves prior reject-to-exit behavior).
        if (IsCancellation(reply))
        {
            ctx.CurrentStageIndex = stages.Count;
            ctx.PauseType = null;
            ctx.PausePayload = null;
            return (false, null);
        }

        // 3) Anything else is a correction. Only safe to auto-rewind when no mutating tool
        //    has executed before this confirmation; otherwise we'd re-run that mutation.
        if (ctx.CurrentStageIndex > firstMutatingIndex)
        {
            return (true,
                "I can't automatically apply that change because an action has already been " +
                "completed earlier in this workflow. Reply \"confirm\" to keep the current result, " +
                $"or \"cancel\" to stop. (Requested change: {request.Message})");
        }

        // Additive correction: append the correction to the accumulated message and rewind
        // the stage index. We intentionally do NOT call RollbackToStage — accumulated
        // context (tool results, parent data, etc.) is preserved so subsequent stages can
        // build on it rather than having to repeat expensive calls. Re-running stages will
        // naturally overwrite their own outputs with fresh values.
        var targetIndex = !string.IsNullOrEmpty(currentStage.RewindTo)
            ? ResolveRewindTarget(stages, currentStage.RewindTo)
            : Math.Max(0, ctx.CurrentStageIndex - 1);
        ctx.OriginalMessage = $"{ctx.OriginalMessage}. Correction: {request.Message}";
        ctx.CurrentStageIndex = targetIndex;
        ctx.PauseType = null;
        ctx.PausePayload = null;

        _logger.LogInformation(
            "WorkflowEngine: confirmation correction run={RunId} → rewind to index={Idx} ({StageId})",
            run.AgentRunId, targetIndex, stages[targetIndex].StageId);

        return (false, null);
    }

    // ── Resume logic ──────────────────────────────────────────────────────────

    private static void HandleResume(WorkflowContext ctx, PromptDefinition prompt, ChatRequest request)
    {
        var reply = request.Message.Trim().ToLowerInvariant();
        var currentStage = prompt.Stages![ctx.CurrentStageIndex];

        // ── Foreach-aware resume ──────────────────────────────────────────────
        // If the current stage is a foreach, the pause originated inside one of
        // its sub-stages. Set a ResumeSignal and do NOT advance CurrentStageIndex —
        // the foreach handler will interpret the signal on re-entry.
        if (currentStage.Type.Equals("foreach", StringComparison.OrdinalIgnoreCase))
        {
            switch (ctx.PauseType)
            {
                case "ai-generate":
                    if (IsConfirmation(reply))
                        ctx.ResumeSignal = "confirmed";
                    else if (IsNegation(reply))
                        ctx.ResumeSignal = "skip_item";
                    else
                    {
                        ctx.ResumeSignal = "correction";
                        ctx.OriginalMessage = $"{ctx.OriginalMessage}. Correction: {request.Message}";
                    }
                    break;

                case "clarify":
                    ctx.ResumeSignal = "correction";
                    ctx.OriginalMessage = $"{ctx.OriginalMessage}. Additional context: {request.Message}";
                    break;

                default: // confirmation, user-select, etc.
                    if (IsConfirmation(reply))
                        ctx.ResumeSignal = "confirmed";
                    else if (IsNegation(reply))
                        ctx.ResumeSignal = "skip_item";
                    else
                        ctx.ResumeSignal = "correction";
                    break;
            }

            ctx.PauseType = null;
            ctx.PausePayload = null;
            return;
        }

        // ── Standard resume (non-foreach) ─────────────────────────────────────
        switch (ctx.PauseType)
        {
            case "clarify":
                ctx.OriginalMessage = $"{ctx.OriginalMessage}. Additional context: {request.Message}";
                ctx.CurrentStageIndex = prompt.Stages!.FindIndex(s => s.Type == "ai-extract");
                if (ctx.CurrentStageIndex < 0) ctx.CurrentStageIndex = 0;
                break;

            case "ai-generate":
                if (IsConfirmation(reply))
                {
                    ctx.CurrentStageIndex++;
                }
                else
                {
                    ctx.OriginalMessage = $"{ctx.OriginalMessage}. Correction: {request.Message}";
                    var stage = prompt.Stages![ctx.CurrentStageIndex];
                    ctx.CurrentStageIndex = ResolveRewindTarget(prompt.Stages, stage.RewindTo);
                }
                break;

            default: // confirmation, user-select, etc.
                if (IsConfirmation(reply))
                    ctx.CurrentStageIndex++;
                else
                    ctx.CurrentStageIndex = prompt.Stages!.Count; // rejection — skip to end
                break;
        }

        ctx.PauseType = null;
        ctx.PausePayload = null;
    }

    private static bool IsConfirmation(string reply) =>
        reply is "yes" or "ok" or "confirm" or "proceed" or "looks good"
            or "looks correct" or "correct" or "submit" or "go" or "continue" or "confirmed"
            || reply.StartsWith("yes,") || reply.StartsWith("yes.")
            || reply.StartsWith("ok,") || reply.StartsWith("ok.");

    private static bool IsNegation(string reply) =>
        reply is "no" or "skip" or "next" or "no thanks" or "decline" or "reject" or "stop" or "abort"
            || reply.StartsWith("no,") || reply.StartsWith("no.");

    // ── Confirmation-stage classification ───────────────────────────────────────
    // For top-level confirmation stages we distinguish three intents so a correction
    // ("change the title to X", "no, use a different sprint") rewinds instead of exiting.

    /// <summary>
    /// True for a clean confirmation with no embedded correction clause. Unlike
    /// <see cref="IsConfirmation"/>, "yes, but change X" is NOT a confirmation here — it
    /// carries a correction cue and must be routed to the rewind path.
    /// </summary>
    private static bool IsStrictConfirmation(string reply) =>
        IsConfirmation(reply) && !ContainsCorrectionCue(reply);

    /// <summary>
    /// True only for explicit, standalone cancel intents (exact match). Phrases like
    /// "no, change X" are deliberately excluded so they are treated as corrections.
    /// </summary>
    private static bool IsCancellation(string reply) =>
        reply is "no" or "cancel" or "stop" or "abort" or "reject" or "decline"
            or "skip" or "next" or "no thanks" or "no, thanks" or "nevermind" or "never mind"
            or "quit" or "exit";

    /// <summary>Detects words signalling the user wants something changed.</summary>
    private static bool ContainsCorrectionCue(string reply)
    {
        string[] cues = { " but ", "change", "update", "instead", "actually", "except",
            "however", "edit", "wrong", "rather", "different", "modify", "fix" };
        foreach (var cue in cues)
            if (reply.Contains(cue, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static int ResolveRewindTarget(List<WorkflowStage> stages, string? rewindTo)
    {
        if (!string.IsNullOrEmpty(rewindTo))
        {
            var idx = stages.FindIndex(s =>
                s.StageId.Equals(rewindTo, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) return idx;
        }

        var extractIdx = stages.FindIndex(s => s.Type == "ai-extract");
        return extractIdx >= 0 ? extractIdx : 0;
    }

    // ── Core stage runner ─────────────────────────────────────────────────────

    private async IAsyncEnumerable<StreamEvent> RunStagesAsync(
        AgentRun run,
        PromptDefinition prompt,
        WorkflowContext ctx,
        ChatRequest request,
        string? sessionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var stages = prompt.Stages!;

        // Pre-compute which stages are rewindable for breadcrumb metadata
        var rewindableSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rewindableStages = await GetRewindableStagesAsync(prompt);
        foreach (var rs in rewindableStages)
            rewindableSet.Add(rs.StageId);

        // Emit selection_summary for the user-select stage we just resumed from
        if (ctx.CurrentStageIndex > 0)
        {
            var prevStage = stages[ctx.CurrentStageIndex - 1];
            if (prevStage.Type.Equals("user-select", StringComparison.OrdinalIgnoreCase))
            {
                yield return BuildSelectionSummary(prevStage, ctx, rewindableSet);
            }
        }

        for (int i = ctx.CurrentStageIndex; i < stages.Count; i++)
        {
            if (ct.IsCancellationRequested) yield break;

            ctx.CurrentStageIndex = i;
            var stage = stages[i];

            _logger.LogInformation("WorkflowEngine: run={RunId} stage={StageId} type={Type}",
                run.AgentRunId, stage.StageId, stage.Type);

            yield return new StreamEvent
            {
                Type = "stage_start",
                Data = new { stage.StageId, stage.Type }
            };

            if (!_handlers.TryGetValue(stage.Type, out var handler))
            {
                var errorMsg = $"Unknown stage type '{stage.Type}'";
                _logger.LogWarning("WorkflowEngine: {Msg}", errorMsg);
                await _agentRun.FailAsync(run, errorMsg);
                yield return new StreamEvent { Type = "error", Data = errorMsg };
                yield break;
            }

            var handlerContext = new StageHandlerContext
            {
                Run = run,
                Prompt = prompt,
                Stage = stage,
                WorkflowContext = ctx,
                Request = request,
                SessionId = sessionId
            };

            await foreach (var evt in handler.ExecuteAsync(handlerContext, ct))
                yield return evt;

            // Check if handler paused the run
            if (run.Status == AgentRunStatus.WaitingForInput)
            {
                await _agentRun.SaveWorkflowContextAsync(run, ctx);
                yield break;
            }

            // Check if a handler hard-failed the run (e.g. an unresolved data-flow token).
            // Stop immediately so later stages cannot run on a failed run or overwrite the
            // failed status with a misleading "completed" summary.
            if (run.Status == AgentRunStatus.Failed)
            {
                yield break;
            }

            // Emit selection_summary when the stage made a selection
            if (stage.Type.Equals("user-select", StringComparison.OrdinalIgnoreCase)
                && ctx.Fields.ContainsKey($"_result_{stage.StageId}"))
            {
                yield return BuildSelectionSummary(stage, ctx, rewindableSet);
            }
        }

        // All stages complete
        if (run.Status != AgentRunStatus.Completed && run.Status != AgentRunStatus.Failed)
        {
            var lastResultKey = ctx.Fields.Keys
                .Where(k => k.StartsWith("_result_"))
                .LastOrDefault();
            var summary = lastResultKey != null && ctx.Fields.TryGetValue(lastResultKey, out var lr) && lr != null
                ? lr.ToString()!
                : "Done.";
            await _conversation.AddMessageAsync(request.ConversationId, "user", ctx.OriginalMessage);
            await _conversation.AddMessageAsync(request.ConversationId, "assistant", summary);
            await _agentRun.CompleteAsync(run, summary, run.TokensUsed);
            yield return new StreamEvent
            {
                Type = "done",
                Data = new { run.AgentRunId, run.TokensUsed, run.ModelId, run.EstimatedCostUsd }
            };
        }
    }

    // ── Selection summary builder ──────────────────────────────────────────────

    private StreamEvent BuildSelectionSummary(
        WorkflowStage stage, WorkflowContext ctx, HashSet<string> rewindableSet)
    {
        var selectedLabel = "(selected)";
        var resultKey = $"_result_{stage.StageId}";

        if (ctx.Fields.TryGetValue(resultKey, out var selResult))
        {
            var selJson = selResult?.ToString() ?? "";
            if (!string.IsNullOrEmpty(selJson))
            {
                try
                {
                    using var selDoc = System.Text.Json.JsonDocument.Parse(selJson);
                    if (selDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (stage.LabelField != null && selDoc.RootElement.TryGetProperty(stage.LabelField, out var custom))
                            selectedLabel = custom.GetString() ?? selJson;
                        else if (selDoc.RootElement.TryGetProperty("label", out var lbl))
                            selectedLabel = lbl.GetString() ?? selJson;
                        else if (selDoc.RootElement.TryGetProperty("text", out var txt))
                            selectedLabel = txt.GetString() ?? selJson;
                        else
                            selectedLabel = selJson;
                    }
                    else
                    {
                        selectedLabel = selJson;
                    }
                }
                catch { selectedLabel = selJson; }
            }
        }

        // Fall back to human-readable output-mapped field values
        if (selectedLabel == "(selected)" && stage.OutputMap != null)
        {
            var mapped = stage.OutputMap.Keys
                .Select(k => ctx.Fields.TryGetValue(k, out var fv) ? fv?.ToString() : null)
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
            if (mapped.Any())
                selectedLabel = string.Join(" — ", mapped);
        }

        return new StreamEvent
        {
            Type = "selection_summary",
            Data = new
            {
                stageId = stage.StageId,
                label = TokenResolver.Resolve(stage.SelectPrompt ?? stage.StageId, ctx.Fields),
                selectedValue = selectedLabel,
                rewindable = rewindableSet.Contains(stage.StageId),
                revision = ctx.Revision
            }
        };
    }
}
