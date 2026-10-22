using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ava.Api.Models;
using OpenAI.Chat;

namespace Ava.Api.Services.Stages;

/// <summary>
/// Calls the LLM to extract structured JSON fields from the user's message.
/// Validates required fields; pauses with "clarify" if any are missing.
/// Runs PII check on extracted values when the prompt has piiCheck enabled.
/// Streams raw LLM output in real-time via StreamingChatHelper.
/// </summary>
public class AiExtractStageHandler : IStageHandler
{
    private readonly IChatClientFactory _chatFactory;
    private readonly StreamingChatHelper _streaming;
    private readonly IPiiDetectionService _piiDetection;
    private readonly AgentRunService _agentRun;
    private readonly ILogger<AiExtractStageHandler> _logger;

    public string StageType => "ai-extract";

    public AiExtractStageHandler(
        IChatClientFactory chatFactory,
        StreamingChatHelper streaming,
        IPiiDetectionService piiDetection,
        AgentRunService agentRun,
        ILogger<AiExtractStageHandler> logger)
    {
        _chatFactory   = chatFactory;
        _streaming     = streaming;
        _piiDetection  = piiDetection;
        _agentRun      = agentRun;
        _logger        = logger;
    }

    public async IAsyncEnumerable<StreamEvent> ExecuteAsync(
        StageHandlerContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (run, prompt, stage, ctx) = (context.Run, context.Prompt, context.Stage, context.WorkflowContext);

        var stagePrompt = stage.Prompt ?? "Extract the relevant fields from the user's message. Return only valid JSON.";

        // If the stage has required fields but the prompt doesn't mention them,
        // append explicit instructions so the LLM knows what JSON keys to return.
        if (stage.Required is { Count: > 0 })
        {
            var requiredLower = string.Join(", ", stage.Required);
            if (!stage.Required.Any(f => stagePrompt.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                stagePrompt += $"\n\nYou MUST return a JSON object with these exact keys: {requiredLower}. Extract them from the user's message.";
            }
        }

        var resolvedPrompt = TokenResolver.Resolve(stagePrompt, ctx.Fields);

        var userMessage = new StringBuilder();

        // Determine data source: DataFrom (explicit) → original message + prior stage results.
        // When DataFrom is configured, use only those stages.
        // When not configured, include the user's original message as primary intent, and append
        // any upstream _result_* values as supplemental context. This preserves correction-rewind
        // safety (user message is first/primary) while still giving the AI data it needs to
        // extract from prior tool outputs (e.g. getSprintId needs the sprint list from getSprints).
        if (stage.DataFrom is { Count: > 0 })
        {
            userMessage.AppendLine("Data to process:");
            foreach (var sourceStageId in stage.DataFrom)
            {
                var key = $"_result_{sourceStageId}";
                if (ctx.Fields.TryGetValue(key, out var val) && val != null)
                {
                    userMessage.AppendLine($"--- {sourceStageId} ---");
                    userMessage.AppendLine(val.ToString());
                }
            }
        }
        else
        {
            // When the workflow preserves context, prepend the prior conversation so the AI can
            // extract fields the user described in earlier turns (e.g. a support ticket's details).
            var convoBlock = TokenResolver.FormatConversationHistory(ctx.ConversationHistory);
            if (!string.IsNullOrEmpty(convoBlock))
                userMessage.AppendLine(convoBlock);

            userMessage.AppendLine("Latest message:");
            userMessage.AppendLine(ctx.OriginalMessage);

            // Append any prior stage results so the AI has upstream data to extract from.
            var priorResults = ctx.Fields
                .Where(f => f.Key.StartsWith("_result_") && f.Value != null)
                .ToList();
            if (priorResults.Count > 0)
            {
                userMessage.AppendLine("\nContext from prior stages:");
                foreach (var r in priorResults)
                {
                    var stageId = r.Key["_result_".Length..];
                    userMessage.AppendLine($"--- {stageId} ---");
                    userMessage.AppendLine(r.Value?.ToString());
                }
            }
        }

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage($"You are a data extraction assistant.\n{prompt.SystemPrompt}\n\n{resolvedPrompt}"),
            new UserChatMessage(userMessage.ToString())
        };

        var client = await _chatFactory.CreateAsync(context.SessionId, context.WorkflowContext.ModelOverride);
        var channel = Channel.CreateUnbounded<StreamEvent>();
        var resultHolder = new string[1];
        var failed = false;

        _ = Task.Run(async () =>
        {
            try
            {
                var (fullText, usage) = await _streaming.StreamTextAsync(
                    client, messages,
                    evt => { channel.Writer.TryWrite(evt); return ValueTask.CompletedTask; });
                if (usage != null)
                {
                    run.TokensUsed      += usage.TotalTokenCount;
                    run.InputTokensUsed  += usage.InputTokenCount;
                    run.OutputTokensUsed += usage.OutputTokenCount;
                }
                resultHolder[0] = fullText;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ai-extract AI call failed");
                failed = true;
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
        {
            // Extraction output is internal JSON — do not stream to the client.
            // User-facing messages (clarify, PII warnings) are yielded independently below.
        }

        if (failed)
        {
            await _agentRun.FailAsync(run, "ai-extract failed");
            yield return new StreamEvent { Type = "error", Data = "Extraction failed. Please try again." };
            yield break;
        }

        var rawJson = JsonHelper.StripMarkdownFences((resultHolder[0] ?? "").Trim());

        // Fallback: if the LLM returned markdown/narrative instead of bare JSON,
        // attempt to locate the JSON object by scanning for the outermost { ... } block.
        if (!rawJson.StartsWith("{", StringComparison.Ordinal) && !rawJson.StartsWith("[", StringComparison.Ordinal))
        {
            var start = rawJson.IndexOf('{');
            var end   = rawJson.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                _logger.LogWarning("ai-extract stage={StageId}: LLM returned non-JSON text — extracted JSON object at [{Start},{End}]",
                    stage.StageId, start, end);
                rawJson = rawJson[start..(end + 1)];
            }
            else
            {
                _logger.LogWarning("ai-extract stage={StageId}: LLM returned no JSON object in response — raw length={Len}",
                    stage.StageId, rawJson.Length);
            }
        }

        // Merge extracted fields into ctx, applying outputMap renames
        var extractedFields = new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // If the LLM returned a JSON array, unwrap the first element
            if (root.ValueKind == JsonValueKind.Array)
            {
                _logger.LogWarning("ai-extract stage={StageId}: LLM returned array instead of object — unwrapping first element", stage.StageId);
                var enumerator = root.EnumerateArray();
                if (enumerator.MoveNext() && enumerator.Current.ValueKind == JsonValueKind.Object)
                    root = enumerator.Current;
                else
                {
                    _logger.LogWarning("ai-extract stage={StageId}: array contained no usable object: {Raw}", stage.StageId, rawJson);
                    root = default;
                }
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    var val = JsonHelper.ExtractValue(prop.Value);
                    if (val != null)
                    {
                        // Check if outputMap renames this field (outputMap: { dest: source })
                        var fieldName = prop.Name;
                        if (stage.OutputMap != null)
                        {
                            foreach (var kvp in stage.OutputMap)
                            {
                                if (kvp.Value.Equals(prop.Name, StringComparison.OrdinalIgnoreCase))
                                {
                                    fieldName = kvp.Key;
                                    break;
                                }
                            }
                        }
                        ctx.SetField(stage.StageId, fieldName, val);
                        extractedFields[fieldName] = val;
                        _logger.LogInformation("ai-extract stage={StageId}: extracted '{Source}' → '{Field}'",
                            stage.StageId, prop.Name, fieldName);
                    }
                }
            }
        }
        catch (JsonException)
        {
            _logger.LogWarning("ai-extract stage={StageId}: response was not valid JSON — fields will be empty, required check will prompt the user. Preview: {Preview}",
                stage.StageId, rawJson.Length > 300 ? rawJson[..300] + "..." : rawJson);
        }

        // Emit step_done so consumers (test tab, flow tester) can see what was extracted
        var stepExec = new StepExecution
        {
            StepId = stage.StageId,
            ToolName = "ai-extract",
            Success = true,
            Result = JsonSerializer.Serialize(extractedFields, new JsonSerializerOptions { WriteIndented = true }),
            TokensUsed = TokenResolver.EstimateContextTokens(ctx.Fields),
        };
        run.Steps.Add(stepExec);
        yield return new StreamEvent { Type = "step_done", Data = stepExec };

        // PII check
        if (prompt.PiiCheck)
        {
            var stringValues = ctx.Fields.Values.Where(v => v is string).Cast<string>().ToList();
            var piiResult = await _piiDetection.DetectAsync(stringValues, run.Domain);
            if (piiResult.Detected)
            {
                var piiMsg = $"I detected potential personal information in your message ({piiResult.Summary}). Please remove personal identifiers and try again.";
                await _agentRun.PauseForInputAsync(run, ctx, "clarify");
                ctx.PausePayload = new { reason = "pii", message = piiMsg };
                yield return new StreamEvent { Type = "token", Data = piiMsg };
                yield return new StreamEvent { Type = "done", Data = new { run.AgentRunId, resumeRunId = run.AgentRunId } };
                yield break;
            }
        }

        // Validate required fields
        var missing = (stage.Required ?? new())
            .Where(f => !ctx.Fields.ContainsKey(f) ||
                        ctx.Fields[f] == null ||
                        string.IsNullOrWhiteSpace(ctx.Fields[f]?.ToString()))
            .ToList();

        if (missing.Any())
        {
            var question = !string.IsNullOrEmpty(stage.ClarifyPrompt)
                ? stage.ClarifyPrompt
                : missing.Count == 1
                    ? $"I need one more detail to continue — could you provide the **{missing[0]}**?"
                    : $"I need a few more details: **{string.Join("**, **", missing)}**. Could you provide those?";

            await _agentRun.PauseForInputAsync(run, ctx, "clarify");
            yield return new StreamEvent { Type = "token", Data = question };
            yield return new StreamEvent { Type = "done", Data = new { run.AgentRunId, resumeRunId = run.AgentRunId } };
            yield break;
        }
    }
}
