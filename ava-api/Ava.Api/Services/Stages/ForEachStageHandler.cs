using System.Runtime.CompilerServices;
using System.Text.Json;
using Ava.Api.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Ava.Api.Services.Stages;

/// <summary>
/// Iterates over an array field in the workflow context and executes a list of
/// sub-stages per item. Supports nested foreach (sub-stages can themselves be foreach).
///
/// Loop state is stored in ctx.Fields under well-known keys so that pause/resume
/// works correctly across the engine boundary:
///   _foreach_{stageId}_index    – current iteration index (0-based)
///   _foreach_{stageId}_subIndex – current sub-stage index within the iteration
///   _foreach_{stageId}_count    – total item count
///
/// The current item is injected into ctx.Fields under the configured ItemAlias,
/// enabling {{itemAlias.property}} token resolution in sub-stages.
/// </summary>
public class ForEachStageHandler : IStageHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AgentRunService _agentRun;
    private readonly ILogger<ForEachStageHandler> _logger;

    private Dictionary<string, IStageHandler>? _handlers;

    public string StageType => "foreach";

    public ForEachStageHandler(
        IServiceProvider serviceProvider,
        AgentRunService agentRun,
        ILogger<ForEachStageHandler> logger)
    {
        _serviceProvider = serviceProvider;
        _agentRun = agentRun;
        _logger   = logger;
    }

    /// <summary>
    /// Lazily resolves all registered stage handlers on first use,
    /// breaking the circular DI dependency at construction time.
    /// </summary>
    private Dictionary<string, IStageHandler> Handlers =>
        _handlers ??= _serviceProvider.GetServices<IStageHandler>()
            .ToDictionary(h => h.StageType, h => h, StringComparer.OrdinalIgnoreCase);

    public async IAsyncEnumerable<StreamEvent> ExecuteAsync(
        StageHandlerContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (run, prompt, stage, ctx) = (context.Run, context.Prompt, context.Stage, context.WorkflowContext);

        if (stage.Stages == null || stage.Stages.Count == 0)
        {
            _logger.LogWarning("foreach '{StageId}': no sub-stages defined — skipping", stage.StageId);
            yield break;
        }

        if (string.IsNullOrEmpty(stage.ItemsFrom))
        {
            _logger.LogWarning("foreach '{StageId}': no itemsFrom specified — skipping", stage.StageId);
            yield break;
        }

        // ── Resolve the items array from context ──────────────────────────────
        var items = ResolveItemsArray(stage.ItemsFrom, ctx.Fields, stage.StageId);
        if (items == null || items.Count == 0)
        {
            _logger.LogInformation("foreach '{StageId}': items array is empty — skipping", stage.StageId);
            yield return new StreamEvent
            {
                Type = "token",
                Data = $"No items found for iteration in stage '{stage.StageId}'."
            };
            yield break;
        }

        var alias = stage.ItemAlias ?? "_item";
        var indexKey    = $"_foreach_{stage.StageId}_index";
        var subIndexKey = $"_foreach_{stage.StageId}_subIndex";
        var countKey    = $"_foreach_{stage.StageId}_count";

        // ── Determine starting position (fresh or resumed) ────────────────────
        int startIter = 0;
        int startSub  = 0;
        bool resumedFromIterConfirm = false; // true when resume was for an iteration-level ConfirmEach pause

        if (ctx.ResumeSignal != null)
        {
            // Resuming after a pause inside this foreach
            startIter = ctx.Fields.TryGetValue(indexKey, out var si) ? Convert.ToInt32(si) : 0;
            startSub  = ctx.Fields.TryGetValue(subIndexKey, out var ss) ? Convert.ToInt32(ss) : 0;

            if (ctx.ResumeSignal == "confirmed")
            {
                if (startSub == -1)
                {
                    // Iteration-level ConfirmEach confirm: start sub-stages from 0
                    startSub = 0;
                    resumedFromIterConfirm = true;
                }
                else
                {
                    // Sub-stage confirm: advance past the sub-stage that paused
                    startSub++;
                    if (startSub >= stage.Stages.Count)
                    {
                        startSub = 0;
                        startIter++;
                    }
                }
            }
            else if (ctx.ResumeSignal == "skip_item")
            {
                // Skip the rest of this iteration, move to next item
                _logger.LogInformation(
                    "foreach '{StageId}': skipping iteration {Iter} by user request",
                    stage.StageId, startIter);
                startSub = 0;
                startIter++;
            }
            else if (ctx.ResumeSignal == "correction")
            {
                if (startSub == -1)
                {
                    // Correction during iteration-level confirm: re-show the confirm for this iteration
                    startSub = 0;
                    // resumedFromIterConfirm stays false so the gate fires again
                }
                else
                {
                    // Find the rewind target within sub-stages
                    var pausedSubStage = stage.Stages[startSub];
                    startSub = ResolveInnerRewindTarget(stage.Stages, pausedSubStage.RewindTo);
                }
            }

            ctx.ResumeSignal = null; // consumed
        }
        else if (ctx.Fields.ContainsKey(indexKey))
        {
            // Re-entry without resume signal means we pick up where we stored
            // This shouldn't normally happen but is a safety net
            startIter = Convert.ToInt32(ctx.Fields[indexKey]);
            startSub  = 0;
        }

        // Store count for reference by sub-stages
        ctx.Fields[countKey] = items.Count;

        // ── Main iteration loop ───────────────────────────────────────────────
        for (int i = startIter; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested) yield break;

            ctx.Fields[indexKey] = i;
            ctx.Fields[$"{indexKey}_1"] = i + 1; // 1-based for display
            var item = items[i];

            // Inject current item into context under the alias
            ctx.SetField(stage.StageId, alias, item.GetRawText());

            // Also inject flattened properties: {{alias.propName}}
            InjectItemProperties(ctx, stage.StageId, alias, item);

            _logger.LogInformation(
                "foreach '{StageId}': iteration {Index}/{Count}",
                stage.StageId, i + 1, items.Count);

            yield return new StreamEvent
            {
                Type = "stage_start",
                Data = new { stageId = $"{stage.StageId}[{i}]", stage.Type, iteration = i + 1, total = items.Count }
            };

            // ── ConfirmEach: pause for user confirmation before sub-stages ────
            int subStart = (i == startIter) ? startSub : 0;
            bool alreadyConfirmedThisIter = (i == startIter && resumedFromIterConfirm);
            resumedFromIterConfirm = false; // only relevant for the first resumed iteration

            if (stage.ConfirmEach && subStart == 0 && !alreadyConfirmedThisIter)
            {
                ctx.Fields[indexKey] = i;
                ctx.Fields[subIndexKey] = -1; // sentinel: iteration-level confirm pending
                var confirmText = $"Proceed with iteration {i + 1}/{items.Count}?";
                await _agentRun.PauseForInputAsync(run, ctx, "confirmation");
                ctx.PausePayload = new { prompt = confirmText };
                yield return new StreamEvent
                {
                    Type = "confirmation_required",
                    Data = new { run.AgentRunId, prompt = confirmText }
                };
                yield return new StreamEvent { Type = "done", Data = new { run.AgentRunId, resumeRunId = run.AgentRunId } };
                yield break;
            }

            // ── Sub-stage loop ────────────────────────────────────────────────
            for (int s = subStart; s < stage.Stages.Count; s++)
            {
                if (ct.IsCancellationRequested) yield break;

                ctx.Fields[subIndexKey] = s;
                var subStage = stage.Stages[s];

                // Generate a unique stageId for this iteration's sub-stage
                var iterStageId = $"{stage.StageId}_iter{i}_{subStage.StageId}";
                var effectiveSubStage = CloneStageWithId(subStage, iterStageId);

                _logger.LogInformation(
                    "foreach '{StageId}': iteration {Iter} sub-stage '{SubStage}' type={Type}",
                    stage.StageId, i, subStage.StageId, subStage.Type);

                if (!Handlers.TryGetValue(subStage.Type, out var handler))
                {
                    var errMsg = $"foreach '{stage.StageId}': unknown sub-stage type '{subStage.Type}'";
                    _logger.LogWarning("{Msg}", errMsg);
                    await _agentRun.FailAsync(run, errMsg);
                    yield return new StreamEvent { Type = "error", Data = errMsg };
                    yield break;
                }

                var subContext = new StageHandlerContext
                {
                    Run = run,
                    Prompt = prompt,
                    Stage = effectiveSubStage,
                    WorkflowContext = ctx,
                    Request = context.Request,
                    SessionId = context.SessionId
                };

                await foreach (var evt in handler.ExecuteAsync(subContext, ct))
                    yield return evt;

                // If a sub-stage paused the run, save loop state and exit
                if (run.Status == AgentRunStatus.WaitingForInput)
                {
                    ctx.Fields[indexKey]    = i;
                    ctx.Fields[subIndexKey] = s;
                    _logger.LogInformation(
                        "foreach '{StageId}': paused at iteration {Iter} sub-stage {Sub}",
                        stage.StageId, i, s);
                    yield break;
                }

                // If a sub-stage hard-failed (e.g. an unresolved data-flow token), abort
                // the whole loop instead of repeating the same failure for every item.
                if (run.Status == AgentRunStatus.Failed)
                {
                    _logger.LogWarning(
                        "foreach '{StageId}': aborting after sub-stage '{Sub}' failed at iteration {Iter}",
                        stage.StageId, subStage.StageId, i);
                    yield break;
                }
            }
        }

        // ── All iterations complete — clean up loop state ─────────────────────
        ctx.Fields.Remove(indexKey);
        ctx.Fields.Remove(subIndexKey);
        ctx.Fields.Remove(countKey);
        // Keep the alias pointing to the last item for downstream stages

        _logger.LogInformation("foreach '{StageId}': completed all {Count} iterations", stage.StageId, items.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves an items array from ctx.Fields using dot-path notation.
    /// Returns a list of JsonElement items, or null if resolution fails.
    /// </summary>
    private List<JsonElement>? ResolveItemsArray(
        string itemsFrom, Dictionary<string, object?> fields, string stageId)
    {
        // Split on first dot: root field vs JSON path
        string rootField = itemsFrom;
        string? jsonPath = null;
        var dotIdx = itemsFrom.IndexOf('.');
        if (dotIdx > 0)
        {
            rootField = itemsFrom[..dotIdx];
            jsonPath  = itemsFrom[(dotIdx + 1)..];
        }

        if (!fields.TryGetValue(rootField, out var rawValue) || rawValue == null)
        {
            _logger.LogWarning("foreach '{StageId}': field '{Field}' not found in context. Available fields: {Keys}",
                stageId, rootField, string.Join(", ", fields.Keys));
            return null;
        }

        var jsonStr = rawValue.ToString()!;

        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            // Navigate JSON path
            if (jsonPath != null)
            {
                foreach (var segment in jsonPath.Split('.'))
                {
                    if (root.TryGetProperty(segment, out var nested))
                        root = nested;
                    else
                    {
                        _logger.LogWarning(
                            "foreach '{StageId}': path segment '{Seg}' not found in '{Field}'. Raw preview: {Preview}",
                            stageId, segment, itemsFrom,
                            jsonStr.Length > 300 ? jsonStr[..300] + "..." : jsonStr);
                        return null;
                    }
                }
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "foreach '{StageId}': expected array but got {Kind} at '{Field}'. Raw preview: {Preview}",
                    stageId, root.ValueKind, itemsFrom,
                    jsonStr.Length > 300 ? jsonStr[..300] + "..." : jsonStr);
                return null;
            }

            // Clone elements so they survive JsonDocument disposal
            return root.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "foreach '{StageId}': failed to parse JSON from '{Field}'. Raw preview: {Preview}",
                stageId, rootField,
                jsonStr.Length > 300 ? jsonStr[..300] + "..." : jsonStr);
            return null;
        }
    }

    /// <summary>
    /// Recursively injects flattened item properties into ctx.Fields so sub-stages can
    /// reference them with {{alias.path.to.value}} tokens. Nested objects and arrays are
    /// walked so deeply-nested ids resolve (e.g. ADO work-item relations expose the id at
    /// {{pbi.target.id}}, not {{pbi.id}}). Each intermediate object/array is also stored as
    /// raw JSON so {{alias.target}} still yields the sub-object.
    /// </summary>
    private static void InjectItemProperties(
        WorkflowContext ctx, string stageId, string alias, JsonElement item)
        => FlattenInto(ctx, stageId, alias, item, depth: 0, budget: new[] { MaxFlattenKeysPerItem });

    private const int MaxFlattenDepth = 6;
    private const int MaxFlattenKeysPerItem = 250;
    private const int MaxFlattenArrayItems = 50;

    private static void FlattenInto(
        WorkflowContext ctx, string stageId, string prefix, JsonElement node, int depth, int[] budget)
    {
        if (budget[0] <= 0) return;

        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                // depth 0 == the whole item, already stored under the bare alias by the caller
                if (depth > 0) { ctx.SetField(stageId, prefix, node.GetRawText()); budget[0]--; }
                if (depth >= MaxFlattenDepth) return;
                foreach (var prop in node.EnumerateObject())
                {
                    if (budget[0] <= 0) return;
                    FlattenInto(ctx, stageId, $"{prefix}.{prop.Name}", prop.Value, depth + 1, budget);
                }
                break;

            case JsonValueKind.Array:
                if (depth > 0) { ctx.SetField(stageId, prefix, node.GetRawText()); budget[0]--; }
                if (depth >= MaxFlattenDepth) return;
                var idx = 0;
                foreach (var el in node.EnumerateArray())
                {
                    if (idx >= MaxFlattenArrayItems || budget[0] <= 0) break;
                    FlattenInto(ctx, stageId, $"{prefix}.{idx++}", el, depth + 1, budget);
                }
                break;

            default:
                ctx.SetField(stageId, prefix, ScalarToString(node));
                budget[0]--;
                break;
        }
    }

    private static string? ScalarToString(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.String => node.GetString(),
        JsonValueKind.Number => node.GetRawText(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        JsonValueKind.Null   => null,
        _                    => node.GetRawText()
    };

    /// <summary>
    /// Resolves the rewind target within a foreach's sub-stage list.
    /// If rewindTo is specified and found, returns that index; otherwise returns 0.
    /// </summary>
    private static int ResolveInnerRewindTarget(List<WorkflowStage> subStages, string? rewindTo)
    {
        if (!string.IsNullOrEmpty(rewindTo))
        {
            var idx = subStages.FindIndex(s =>
                s.StageId.Equals(rewindTo, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) return idx;
        }

        // Default: rewind to the first ai-extract sub-stage, or 0
        var extractIdx = subStages.FindIndex(s => s.Type == "ai-extract");
        return extractIdx >= 0 ? extractIdx : 0;
    }

    /// <summary>
    /// Creates a shallow copy of a stage with a new iteration-specific StageId.
    /// This ensures field journal entries and step executions are unique per iteration.
    /// </summary>
    private static WorkflowStage CloneStageWithId(WorkflowStage source, string newStageId)
    {
        return new WorkflowStage
        {
            StageId       = newStageId,
            Type          = source.Type,
            ToolId        = source.ToolId,
            TemplateRef   = source.TemplateRef,
            Required      = source.Required,
            Prompt        = source.Prompt,
            PrePrompt     = source.PrePrompt,
            OutputMap     = source.OutputMap,
            Parameters    = source.Parameters,
            SelectPrompt  = source.SelectPrompt,
            OptionsFrom   = source.OptionsFrom,
            LabelField    = source.LabelField,
            RewindTo      = source.RewindTo,
            ClarifyPrompt = source.ClarifyPrompt,
            DataFrom      = source.DataFrom,
            ItemsFrom     = source.ItemsFrom,
            ItemAlias     = source.ItemAlias,
            ConfirmEach   = source.ConfirmEach,
            Stages        = source.Stages
        };
    }
}
