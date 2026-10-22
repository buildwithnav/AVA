using System.Text.Json;
using System.Threading.Channels;
using Ava.Api.Models;
using OpenAI.Chat;

namespace Ava.Api.Services.Stages;

/// <summary>
/// Terminal analysis stage. KB tools are always available (industry standard — static
/// tool set). The system prompt includes prior step results which tell the LLM whether
/// a KB was loaded. The LLM decides whether to use kb_query or just summarize.
/// Streams tokens in real-time via StreamingChatHelper.
/// </summary>
public class AiAnalyzeStageHandler : IStageHandler
{
    private readonly IChatClientFactory _chatFactory;
    private readonly StreamingChatHelper _streaming;
    private readonly ConversationService _conversation;
    private readonly AgentRunService _agentRun;
    private readonly ToolRegistryService _toolRegistry;
    private readonly ToolExecutorService _toolExecutor;
    private readonly ILogger<AiAnalyzeStageHandler> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string StageType => "ai-analyze";

    public AiAnalyzeStageHandler(
        IChatClientFactory chatFactory,
        StreamingChatHelper streaming,
        ConversationService conversation,
        AgentRunService agentRun,
        ToolRegistryService toolRegistry,
        ToolExecutorService toolExecutor,
        ILogger<AiAnalyzeStageHandler> logger)
    {
        _chatFactory   = chatFactory;
        _streaming     = streaming;
        _conversation  = conversation;
        _agentRun      = agentRun;
        _toolRegistry  = toolRegistry;
        _toolExecutor  = toolExecutor;
        _logger        = logger;
    }

    public async IAsyncEnumerable<StreamEvent> ExecuteAsync(
        StageHandlerContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (run, prompt, stage, ctx, request) =
            (context.Run, context.Prompt, context.Stage, context.WorkflowContext, context.Request);

        var stagePrompt = TokenResolver.Resolve(
            stage.Prompt ?? "Analyze the data and provide a clear, concise summary.",
            ctx.Fields);

        // Prior step results — filtered by DataFrom if specified. Failed steps are
        // included (clearly marked) so the analysis reports failures instead of
        // hallucinating success when a tool errored.
        var stepsQuery = run.Steps.Where(s => (s.Success && s.Result != null) || !s.Success);
        if (stage.DataFrom is { Count: > 0 })
        {
            var allowed = new HashSet<string>(stage.DataFrom, StringComparer.OrdinalIgnoreCase);
            stepsQuery = stepsQuery.Where(s => allowed.Contains(s.StepId));
        }
        var relevantSteps = stepsQuery.ToList();
        var anyFailed = relevantSteps.Any(s => !s.Success);
        var toolResults = relevantSteps
            .Select(s => s.Success
                ? $"[{s.ToolName}]:\n{s.Result}"
                : $"[FAILED: {s.ToolName}]:\nError: {s.Error ?? "unknown error"}")
            .ToList();
        var toolContext = toolResults.Any()
            ? string.Join("\n\n", toolResults)
            : "No tool results available.";

        // Build selection context from user-select result fields
        var selectionContext = BuildSelectionContext(ctx, stage.SelectionFields);

        var systemPromptText =
            $"{prompt.SystemPrompt}\n\n" +
            $"You are analyzing data on behalf of the user. {stagePrompt}\n\n" +
            "If prior steps loaded data into a knowledge base (look for \"status\": \"loaded\" with a kbId in the step results), " +
            "you can use kb_query to run DuckDB SQL queries against it. Always double-quote table and column names " +
            "(e.g. SELECT \"col\" FROM \"table\"). Provide real numbers, breakdowns, and insights.\n\n" +
            "If no KB was loaded by prior steps, analyze and summarize the step results directly.\n\n" +
            "Any step marked \"[FAILED: ...]\" means that tool call did NOT succeed. You MUST explicitly " +
            "report such failures and their error, and you MUST NOT claim the operation or workflow " +
            "succeeded. Only describe as successful the steps that actually succeeded.\n\n" +
            "When reporting a failure, quote the tool's actual error message verbatim. Do NOT speculate " +
            "about or invent a root cause (such as permissions, PAT scope, authentication, expired tokens, " +
            "or configuration) unless the error text explicitly states that cause.\n\n" +
            "Format results clearly using markdown. Be concise and actionable.";

        // When the workflow preserves context, include the prior conversation so the analysis
        // can reference what the user discussed earlier in the conversation.
        var convoBlock = TokenResolver.FormatConversationHistory(ctx.ConversationHistory);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPromptText),
            new UserChatMessage(
                convoBlock +
                $"Original request: {ctx.OriginalMessage}\n\n" +
                (string.IsNullOrEmpty(selectionContext) ? "" : $"User selections:\n{selectionContext}\n\n") +
                $"Prior step results:\n{toolContext}" +
                (anyFailed ? "\n\nNote: one or more steps FAILED (see above). Report the failure(s) honestly; do not claim success." : ""))
        };

        // KB tools always available — the LLM decides whether to use them based on step results
        var chatTools = _toolRegistry.GetKBChatTools();
        var options = new ChatCompletionOptions();
        foreach (var tool in chatTools)
            options.Tools.Add(tool);

        const int maxIterations = 15;
        var iterations = 0;
        var totalTokens = 0;
        var finalText = "";

        while (iterations < maxIterations)
        {
            yield return new StreamEvent { Type = "status", Data = new { state = iterations == 0 ? "analyzing" : "thinking" } };

            var channel = Channel.CreateUnbounded<StreamEvent>();
            StreamingResult? streamResult = null;

            _ = Task.Run(async () =>
            {
                try
                {
                    var client = await _chatFactory.CreateAsync(context.SessionId, context.WorkflowContext.ModelOverride);
                    streamResult = await _streaming.StreamWithToolsAsync(
                        client, messages, options,
                        evt => { channel.Writer.TryWrite(evt); return ValueTask.CompletedTask; });
                    if (streamResult.Usage != null)
                    {
                        run.TokensUsed      += streamResult.Usage.TotalTokenCount;
                        run.InputTokensUsed  += streamResult.Usage.InputTokenCount;
                        run.OutputTokensUsed += streamResult.Usage.OutputTokenCount;
                        Interlocked.Add(ref totalTokens, streamResult.Usage.TotalTokenCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ai-analyze tool loop LLM call failed");
                    streamResult = new StreamingResult
                    {
                        Text = "Analysis could not be completed. Please try again.",
                        FinishReason = ChatFinishReason.Stop
                    };
                    channel.Writer.TryWrite(new StreamEvent { Type = "token", Data = streamResult.Text });
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            var firstToken = true;
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                if (firstToken && evt.Type == "token")
                {
                    yield return new StreamEvent { Type = "status", Data = new { state = "responding" } };
                    firstToken = false;
                }
                yield return evt;
            }

            // If text response (no tool calls) → done
            if (streamResult == null || !streamResult.IsToolCall)
            {
                finalText = streamResult?.Text ?? "Analysis could not be completed.";
                break;
            }

            // Process tool calls
            var assistantMsg = new AssistantChatMessage(streamResult.ToolCalls);
            if (!string.IsNullOrEmpty(streamResult.Text))
                assistantMsg = new AssistantChatMessage(streamResult.ToolCalls)
                {
                    Content = { ChatMessageContentPart.CreateTextPart(streamResult.Text) }
                };
            messages.Add(assistantMsg);

            foreach (var toolCall in streamResult.ToolCalls!)
            {
                var argsJson = toolCall.FunctionArguments?.ToString() ?? "{}";

                yield return new StreamEvent
                {
                    Type = "tool_call",
                    Data = new { tool = toolCall.FunctionName, @params = argsJson }
                };

                var statusState = toolCall.FunctionName switch
                {
                    "kb_schema" => "inspecting",
                    "kb_query" => "querying",
                    "kb_search" => "searching",
                    _ => "calling_tool"
                };
                yield return new StreamEvent
                {
                    Type = "status",
                    Data = new { state = statusState, detail = toolCall.FunctionName }
                };

                var toolDef = _toolRegistry.GetById(toolCall.FunctionName);
                if (toolDef == null)
                {
                    messages.Add(new ToolChatMessage(toolCall.Id,
                        JsonSerializer.Serialize(new { error = "tool_not_found" }, _jsonOpts)));
                    continue;
                }

                var parsedParams = ParseArgs(argsJson);
                var sessionId = request?.SessionId ?? context.SessionId;
                var conversationId = request?.ConversationId ?? "";
                var (success, result, durationMs) = await _toolExecutor.ExecuteAsync(
                    toolDef, parsedParams, sessionId, conversationId);

                messages.Add(new ToolChatMessage(toolCall.Id, result));

                var summary = result.Length > 500 ? result[..500] + "..." : result;
                yield return new StreamEvent
                {
                    Type = success ? "tool_result" : "tool_error",
                    Data = new { tool = toolCall.FunctionName, summary, tokensUsed = totalTokens }
                };
            }

            iterations++;
        }

        // If we hit max iterations, force a summary
        if (iterations >= maxIterations)
        {
            messages.Add(new SystemChatMessage("Max tool calls reached. Summarize what you have so far."));
            var summaryChannel = Channel.CreateUnbounded<StreamEvent>();

            _ = Task.Run(async () =>
            {
                try
                {
                    var client = await _chatFactory.CreateAsync(context.SessionId, context.WorkflowContext.ModelOverride);
                    var (text, usage) = await _streaming.StreamTextAsync(
                        client, messages,
                        evt => { summaryChannel.Writer.TryWrite(evt); return ValueTask.CompletedTask; });
                    if (usage != null)
                    {
                        run.TokensUsed      += usage.TotalTokenCount;
                        run.InputTokensUsed  += usage.InputTokenCount;
                        run.OutputTokensUsed += usage.OutputTokenCount;
                    }
                    finalText = text;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ai-analyze final summary failed");
                    finalText = "Analysis could not be completed.";
                    summaryChannel.Writer.TryWrite(new StreamEvent { Type = "token", Data = finalText });
                }
                finally
                {
                    summaryChannel.Writer.Complete();
                }
            }, ct);

            await foreach (var evt in summaryChannel.Reader.ReadAllAsync(ct))
                yield return evt;
        }

        // If the analysis output is a JSON object, merge its top-level properties
        // into ctx.Fields so downstream stages (e.g. a following foreach) can read
        // them via itemsFrom/tokens. Mirrors the ai-extract merge behaviour.
        // Non-JSON (prose) output simply leaves fields untouched.
        MergeJsonOutputIntoContext(finalText, stage, ctx);

        // Save to conversation history
        if (request != null)
        {
            await _conversation.AddMessageAsync(request.ConversationId, "user", ctx.OriginalMessage);
            await _conversation.AddMessageAsync(request.ConversationId, "assistant", finalText);
        }

        var stepExec = new StepExecution
        {
            StepId = stage.StageId,
            ToolName = "ai-analyze",
            Success = true,
            Result = finalText,
            TokensUsed = TokenResolver.EstimateContextTokens(ctx.Fields)
        };
        run.Steps.Add(stepExec);
        yield return new StreamEvent { Type = "step_done", Data = stepExec };

        await _agentRun.CompleteAsync(run, finalText, run.TokensUsed);

        yield return new StreamEvent
        {
            Type = "done",
            Data = new { run.AgentRunId, run.TokensUsed, run.ModelId, run.EstimatedCostUsd }
        };
    }

    /// <summary>
    /// Parses the analysis output as JSON and, when it is an object, merges its
    /// top-level properties into ctx.Fields (applying outputMap renames) so that
    /// downstream stages can consume them. Safe no-op for non-JSON / array output.
    /// </summary>
    private void MergeJsonOutputIntoContext(string? finalText, WorkflowStage stage, WorkflowContext ctx)
    {
        if (string.IsNullOrWhiteSpace(finalText)) return;

        var rawJson = JsonHelper.StripMarkdownFences(finalText.Trim());

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var val = JsonHelper.ExtractValue(prop.Value);
                if (val == null) continue;

                // Apply outputMap renames (outputMap: { dest: source })
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
                _logger.LogInformation("ai-analyze stage={StageId}: merged output field '{Source}' → '{Field}'",
                    stage.StageId, prop.Name, fieldName);
            }
        }
        catch (JsonException)
        {
            // Prose / non-JSON analysis output — nothing to merge.
        }
    }

    private static Dictionary<string, object?> ParseArgs(string argsJson)
    {
        var result = new Dictionary<string, object?>();
        try
        {
            var doc = JsonDocument.Parse(argsJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = prop.Value.Clone();
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Builds a human-readable summary of user selections from fields stored in workflow context.
    /// If selectionFields is configured on the stage, uses those mappings.
    /// Otherwise, falls back to showing all non-null, non-internal fields.
    /// </summary>
    private static string BuildSelectionContext(WorkflowContext ctx, List<SelectionFieldMapping>? selectionFields)
    {
        var lines = new List<string>();

        if (selectionFields is { Count: > 0 })
        {
            foreach (var mapping in selectionFields)
            {
                if (ctx.Fields.TryGetValue(mapping.Field, out var val) && val != null)
                    lines.Add($"- {mapping.Label}: {val}");
            }
        }
        else
        {
            foreach (var (key, val) in ctx.Fields)
            {
                if (val != null && !key.StartsWith("_"))
                    lines.Add($"- {key}: {val}");
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : string.Empty;
    }
}
