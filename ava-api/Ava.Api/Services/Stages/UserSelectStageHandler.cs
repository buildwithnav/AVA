using System.Text.Json;
using Ava.Api.Models;

namespace Ava.Api.Services.Stages;

/// <summary>
/// Presents multiple options from a context field to the user for selection.
/// Auto-skips if there are no options.
/// Pauses the run; client resumes via POST /api/chat/select.
/// Supports dot-path notation for optionsFrom (e.g., "_result_initFilters.studies")
/// and custom LabelField for display labels.
/// </summary>
public class UserSelectStageHandler : IStageHandler
{
    private readonly AgentRunService _agentRun;
    private readonly ILogger<UserSelectStageHandler> _logger;

    public string StageType => "user-select";

    public UserSelectStageHandler(
        AgentRunService agentRun,
        ILogger<UserSelectStageHandler> logger)
    {
        _agentRun = agentRun;
        _logger   = logger;
    }

    public async IAsyncEnumerable<StreamEvent> ExecuteAsync(
        StageHandlerContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (run, stage, ctx) = (context.Run, context.Stage, context.WorkflowContext);

        _logger.LogInformation("user-select: Available fields: {Fields}",
            string.Join(", ", ctx.Fields.Keys.Select(k =>
                $"{k}={ctx.Fields[k]?.ToString()?.Substring(0, Math.Min(50, ctx.Fields[k]?.ToString()?.Length ?? 0))}")));

        var options = new List<object>();
        var sourceField = stage.OptionsFrom ?? ctx.Fields.Keys
            .Where(k => k.StartsWith("_result_"))
            .LastOrDefault() ?? "_result_";

        // Support dot-path notation (e.g., "_result_initFilters.studies")
        string? rootField = sourceField;
        string? jsonPath = null;
        var dotIdx = sourceField.IndexOf('.');
        if (dotIdx > 0)
        {
            rootField = sourceField[..dotIdx];
            jsonPath = sourceField[(dotIdx + 1)..];
        }

        if (ctx.Fields.TryGetValue(rootField, out var rawResult) && rawResult is string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Navigate the JSON path if specified
                if (jsonPath != null)
                {
                    foreach (var segment in jsonPath.Split('.'))
                    {
                        if (root.TryGetProperty(segment, out var nested))
                            root = nested;
                        else
                        {
                            _logger.LogWarning("user-select: path segment '{Seg}' not found in '{Field}'",
                                segment, sourceField);
                            root = default;
                            break;
                        }
                    }
                }

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        string label;
                        if (stage.LabelField != null && item.TryGetProperty(stage.LabelField, out var customProp))
                            label = customProp.GetString() ?? item.ToString();
                        else if (item.TryGetProperty("label", out var labelProp))
                            label = labelProp.GetString() ?? item.ToString();
                        else if (item.TryGetProperty("text", out var textProp))
                            label = textProp.GetString() ?? item.ToString();
                        else
                            label = item.ToString();
                        options.Add(new { label, value = item.ToString() });
                    }
                }
                else
                {
                    _logger.LogWarning("user-select: expected array but got {Type} in '{Field}'",
                        root.ValueKind, sourceField);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "user-select: failed to parse options from '{Field}'", sourceField);
            }
        }
        else
        {
            _logger.LogWarning("user-select: field '{Field}' not found. Available: {Fields}",
                sourceField, string.Join(", ", ctx.Fields.Keys));
        }

        if (options.Count == 0)
        {
            _logger.LogWarning("user-select: no options found — skipping stage '{Stage}'", stage.StageId);
            await Task.CompletedTask;
            yield break;
        }

        var selectPrompt = TokenResolver.Resolve(
            stage.SelectPrompt ?? "Please select one of the following:", ctx.Fields);
        var payload = new { prompt = selectPrompt, options, stageId = stage.StageId, outputMap = stage.OutputMap };

        await _agentRun.PauseForInputAsync(run, ctx, "user-select");
        ctx.PausePayload = payload;

        yield return new StreamEvent
        {
            Type = "user_input_required",
            Data = new { run.AgentRunId, inputType = "select", payload }
        };
        yield return new StreamEvent { Type = "done", Data = new { run.AgentRunId, resumeRunId = run.AgentRunId } };
    }
}
