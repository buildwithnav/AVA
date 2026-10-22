using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Ava.Api.Config;
using Ava.Api.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Ava.Api.Services.Stages;

/// <summary>
/// Agentic stage handler — gives the LLM a scoped set of MCP tools and lets it
/// iterate autonomously within defined limits (maxIterations, maxTokens).
/// This enables the iterative exploration pattern (like Copilot CLI) within workflows.
/// </summary>
public class AgenticStageHandler : IStageHandler
{
    private readonly IChatClientFactory _chatFactory;
    private readonly McpClientService _mcpClient;
    private readonly StreamingChatHelper _streaming;
    private readonly AgentRunService _agentRun;
    private readonly ILogger<AgenticStageHandler> _logger;

    public string StageType => "agentic";

    public AgenticStageHandler(
        IChatClientFactory chatFactory,
        McpClientService mcpClient,
        StreamingChatHelper streaming,
        AgentRunService agentRun,
        ILogger<AgenticStageHandler> logger)
    {
        _chatFactory = chatFactory;
        _mcpClient = mcpClient;
        _streaming = streaming;
        _agentRun = agentRun;
        _logger = logger;
    }

    public async IAsyncEnumerable<StreamEvent> ExecuteAsync(
        StageHandlerContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (run, prompt, stage, ctx) = (context.Run, context.Prompt, context.Stage, context.WorkflowContext);

        // Parse agentic stage configuration from stage properties
        var config = ParseAgenticConfig(stage);

        yield return new StreamEvent
        {
            Type = "step_start",
            Data = new { stageId = stage.StageId, type = "agentic", toolDomains = config.ToolDomains, maxIterations = config.MaxIterations }
        };

        // 1. Validate service key then fetch MCP tools (domain-filtered when specified)
        if (string.IsNullOrEmpty(config.ServiceKey))
        {
            _logger.LogWarning("Agentic stage {StageId}: no ServiceKey configured", stage.StageId);
            yield return new StreamEvent { Type = "error", Data = "Agentic stage requires a serviceKey — configure which MCP service this stage should use." };
            yield break;
        }

        var mcpTools = await _mcpClient.ListToolsAsync(config.ServiceKey, context.SessionId, config.ToolDomains);
        if (mcpTools.Count == 0)
        {
            _logger.LogWarning("Agentic stage {StageId}: no MCP tools available from [{Service}]", stage.StageId, config.ServiceKey);
            yield return new StreamEvent { Type = "error", Data = $"No tools available from MCP service '{config.ServiceKey}' for this agentic stage." };
            yield break;
        }

        // 2. Convert MCP tools to OpenAI ChatTool format
        var chatTools = McpClientService.ConvertToChatTools(mcpTools);
        var toolNameSet = new HashSet<string>(mcpTools.Select(t => t.Name));

        _logger.LogInformation("Agentic stage {StageId}: {ToolCount} tools available, max {MaxIter} iterations",
            stage.StageId, chatTools.Count, config.MaxIterations);

        // 3. Build system prompt with token substitution.
        // Compact very large context fields (especially pbiDetails) so each iteration
        // doesn't repeatedly pay for giant payloads in the system message.
        int iterations = 0;
        int totalTokens = 0;
        var (promptFields, preSummaryTokens) = await BuildPromptFieldsAsync(ctx.Fields, context.SessionId ?? string.Empty, ct);
        if (preSummaryTokens > 0)
        {
            run.TokensUsed += preSummaryTokens;
            totalTokens += preSummaryTokens;
        }
        var systemPrompt = TokenResolver.Resolve(config.SystemPrompt, promptFields);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        // Add user context as the initial user message
        var userContext = TokenResolver.BuildContextSummary(ctx.Fields, onlyEssential: true, maxFieldLength: 200);
        if (!string.IsNullOrWhiteSpace(userContext))
            messages.Add(new UserChatMessage(userContext));
        int contextChars = systemPrompt.Length + (userContext?.Length ?? 0);

        // 4. Set up ChatCompletionOptions with scoped tools
        var options = new ChatCompletionOptions();
        foreach (var tool in chatTools)
            options.Tools.Add(tool);

        // 5. Run the iterative tool loop
        var toolCallLog = new List<string>();

        // Always require tool calls within the loop — the model's text-based "done" signal
        // is unreliable when given permission (Auto) to exit early. The designed exit is either
        // (a) maxIterations reached → forced summary below, or (b) token budget exceeded.
        // If a stage truly wants to allow early exit, set minIterations lower than maxIterations
        // in the stage config (not yet exposed in the builder, defaults to maxIterations).
        options.ToolChoice = ChatToolChoice.CreateRequiredChoice();

        while (iterations < config.MaxIterations && totalTokens < config.MaxTokens)
        {
            ct.ThrowIfCancellationRequested();

            yield return new StreamEvent
            {
                Type = "status",
                Data = new { state = "thinking", iteration = iterations + 1, maxIterations = config.MaxIterations }
            };

            // Stream LLM call
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
                    _logger.LogError(ex, "Agentic stage LLM call failed at iteration {Iter}", iterations);
                    streamResult = new StreamingResult
                    {
                        Text = $"Error during exploration (iteration {iterations + 1}): {ex.Message}",
                        FinishReason = ChatFinishReason.Stop
                    };
                    channel.Writer.TryWrite(new StreamEvent { Type = "token", Data = streamResult.Text });
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            // Yield streaming tokens
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }

            // If no tool calls despite Required (e.g. model error or API fallback) — exit gracefully
            if (streamResult == null || !streamResult.IsToolCall)
            {
                _logger.LogWarning("Agentic stage {StageId}: no tool call returned at iteration {Iter} despite Required tool choice — ending early",
                    stage.StageId, iterations + 1);

                var earlyText = streamResult?.Text ?? "";
                var earlyField = config.OutputField ?? $"_result_{stage.StageId}";
                ctx.Fields[earlyField] = earlyText;

                yield return new StreamEvent
                {
                    Type = "step_done",
                    Data = new
                    {
                        stageId = stage.StageId,
                        iterations = iterations + 1,
                        tokensUsed = totalTokens,
                        toolCallCount = toolCallLog.Count,
                        success = true
                    }
                };
                yield break;
            }

            // Process tool calls
            var assistantMsg = new AssistantChatMessage(streamResult.ToolCalls);
            if (!string.IsNullOrEmpty(streamResult.Text))
                assistantMsg = new AssistantChatMessage(streamResult.ToolCalls)
                {
                    Content = { ChatMessageContentPart.CreateTextPart(streamResult.Text) }
                };
            messages.Add(assistantMsg);
            contextChars += (streamResult.Text?.Length ?? 0);

            foreach (var toolCall in streamResult.ToolCalls!)
            {
                ct.ThrowIfCancellationRequested();

                var argsJson = toolCall.FunctionArguments?.ToString() ?? "{}";
                toolCallLog.Add(toolCall.FunctionName);
                contextChars += toolCall.FunctionName.Length + argsJson.Length;

                yield return new StreamEvent
                {
                    Type = "tool_call",
                    Data = new { tool = toolCall.FunctionName, @params = argsJson, iteration = iterations + 1 }
                };

                yield return new StreamEvent
                {
                    Type = "status",
                    Data = new { state = "calling_tool", detail = toolCall.FunctionName }
                };

                // Verify tool is in scope
                if (!toolNameSet.Contains(toolCall.FunctionName))
                {
                    var errorMsg = $"Tool '{toolCall.FunctionName}' not in allowed scope for this stage.";
                    messages.Add(new ToolChatMessage(toolCall.Id, errorMsg));
                    yield return new StreamEvent
                    {
                        Type = "tool_error",
                        Data = new { tool = toolCall.FunctionName, error = "out_of_scope" }
                    };
                    continue;
                }

                // Parse parameters and call MCP tool
                var toolParams = ParseToolParams(argsJson);
                var (success, result) = await _mcpClient.CallToolAsync(
                    config.ServiceKey, toolCall.FunctionName, toolParams, context.SessionId);

                // Truncate large results before adding to message history to control token usage.
                // The full result is shown in the UI event; the LLM gets a capped version.
                // Agents should call get_file_contents for detailed reading rather than relying
                // on bulk search output stored in context.
                // list_directory gets extra aggressive truncation since it's especially verbose.
                int maxTokensForTool = toolCall.FunctionName == "list_directory" ? 1000 : 3000;
                var messageResult = result.Length > maxTokensForTool
                    ? result[..maxTokensForTool] + $"\n...[result truncated — {result.Length - maxTokensForTool} additional characters omitted. Use get_file_contents for full file details.]"
                    : result;
                messages.Add(new ToolChatMessage(toolCall.Id, messageResult));
                contextChars += messageResult.Length;
                var contextTokenEstimate = (int)Math.Ceiling(contextChars / 4.0);

                if (success)
                {
                    var summary = result.Length > 500 ? result[..500] + "..." : result;
                    yield return new StreamEvent
                    {
                        Type = "tool_result",
                        Data = new { tool = toolCall.FunctionName, summary, tokensUsed = contextTokenEstimate }
                    };
                }
                else
                {
                    yield return new StreamEvent
                    {
                        Type = "tool_error",
                        Data = new { tool = toolCall.FunctionName, error = "execution_failed", message = result, tokensUsed = contextTokenEstimate }
                    };
                }
            }

            iterations++;
        }

        // Max iterations or token budget reached — force a final summary
        messages.Add(new SystemChatMessage(
            "You have reached the iteration/token limit. Summarize your findings so far in a clear, structured response."));

        // One final LLM call without tools to get the summary
        var finalOptions = new ChatCompletionOptions();
        StreamingResult? finalResult = null;
        var finalChannel = Channel.CreateUnbounded<StreamEvent>();

        _ = Task.Run(async () =>
        {
            try
            {
                var client = await _chatFactory.CreateAsync(context.SessionId, context.WorkflowContext.ModelOverride);
                finalResult = await _streaming.StreamWithToolsAsync(
                    client, messages, finalOptions,
                    evt => { finalChannel.Writer.TryWrite(evt); return ValueTask.CompletedTask; });
                if (finalResult.Usage != null)
                {
                    run.TokensUsed      += finalResult.Usage.TotalTokenCount;
                    run.InputTokensUsed  += finalResult.Usage.InputTokenCount;
                    run.OutputTokensUsed += finalResult.Usage.OutputTokenCount;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agentic stage final summary failed");
                finalResult = new StreamingResult { Text = "Exploration complete but summary generation failed." };
                finalChannel.Writer.TryWrite(new StreamEvent { Type = "token", Data = finalResult.Text });
            }
            finally
            {
                finalChannel.Writer.Complete();
            }
        }, ct);

        await foreach (var evt in finalChannel.Reader.ReadAllAsync(ct))
        {
            yield return evt;
        }

        var outputText = finalResult?.Text ?? "Exploration reached iteration limit.";
        var outputFieldName = config.OutputField ?? $"_result_{stage.StageId}";
        ctx.Fields[outputFieldName] = outputText;

        yield return new StreamEvent
        {
            Type = "step_done",
            Data = new
            {
                stageId = stage.StageId,
                iterations,
                tokensUsed = totalTokens,
                toolCallCount = toolCallLog.Count,
                limitReached = true,
                success = true
            }
        };
    }

    private static AgenticStageConfig ParseAgenticConfig(WorkflowStage stage)
    {
        var config = new AgenticStageConfig();

        if (stage.AgenticConfig != null)
        {
            config.ServiceKey = stage.AgenticConfig.ServiceKey;
            config.ToolDomains = stage.AgenticConfig.ToolDomains;
            config.MaxIterations = stage.AgenticConfig.MaxIterations ?? 15;
            config.MaxTokens = stage.AgenticConfig.MaxTokens ?? 50000;
            config.SystemPrompt = stage.AgenticConfig.SystemPrompt
                ?? "You are an AI assistant with access to tools. Use them to research and answer the question.";
            config.OutputField = stage.AgenticConfig.OutputField;
        }

        // System prompt can also come from stage.Prompt (standard field), which takes precedence
        if (!string.IsNullOrEmpty(stage.Prompt))
            config.SystemPrompt = stage.Prompt;

        return config;
    }

    private static Dictionary<string, object?> ParseToolParams(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private async Task<(Dictionary<string, object?> Fields, int TokensUsed)> BuildPromptFieldsAsync(
        Dictionary<string, object?> fields,
        string sessionId,
        CancellationToken ct)
    {
        var copy = new Dictionary<string, object?>(fields);
        if (copy.TryGetValue("pbiDetails", out var pbiRaw) && pbiRaw != null)
        {
            var raw = pbiRaw.ToString() ?? string.Empty;
            if (raw.Length > 1800 && !string.IsNullOrWhiteSpace(sessionId))
            {
                var (summary, tokens) = await SummarizePbiDetailsAsync(raw, sessionId, ct);
                copy["pbiDetails"] = summary;
                return (copy, tokens);
            }

            copy["pbiDetails"] = CompactPbiDetails(raw);
        }
        return (copy, 0);
    }

    private async Task<(string Summary, int TokensUsed)> SummarizePbiDetailsAsync(
        string raw,
        string sessionId,
        CancellationToken ct)
    {
        try
        {
            var client = await _chatFactory.CreateAsync(sessionId);
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "Summarize this work item for code implementation context. " +
                    "Return concise JSON with keys: id, title, state, priority, repositoryHints, requirements, constraints. " +
                    "requirements max 8 short bullets, constraints max 6 short bullets. No HTML."),
                new UserChatMessage(raw.Length > 14000 ? raw[..14000] : raw)
            };

            var response = await client.CompleteChatAsync(messages);
            var text = response.Value.Content.FirstOrDefault()?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return (CompactPbiDetails(raw), response.Value.Usage?.TotalTokenCount ?? 0);

            return (Truncate(text, 1800), response.Value.Usage?.TotalTokenCount ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to summarize pbiDetails for agentic stage; using deterministic compaction.");
            return (CompactPbiDetails(raw), 0);
        }
    }

    private static string CompactPbiDetails(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        if (!raw.TrimStart().StartsWith("{")) return Truncate(raw, 800);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            string? GetString(string name)
                => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            int? GetInt(string name)
                => root.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : null;

            var description = StripHtml(GetString("description") ?? string.Empty);
            var compact = new
            {
                id = GetInt("id"),
                type = GetString("type"),
                title = GetString("title"),
                state = GetString("state"),
                priority = GetInt("priority"),
                description = Truncate(description, 400)
            };

            return JsonSerializer.Serialize(compact);
        }
        catch
        {
            return Truncate(raw, 800);
        }
    }

    private static string StripHtml(string value)
        => Regex.Replace(value, "<[^>]+>", " ").Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("\r", " ").Replace("\n", " ");

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "... [truncated]";
}

/// <summary>
/// Resolved configuration for an agentic workflow stage (parsed from WorkflowStage top-level properties).
/// </summary>
public class AgenticStageConfig
{
    /// <summary>
    /// The MCP service key to use (must match a key in Ava:Services with IsMcp=true).
    /// Required — the stage will error immediately if not set.
    /// </summary>
    public string? ServiceKey { get; set; }

    /// <summary>
    /// Optional domain filter — only tools whose names match these domain prefixes or exact names
    /// (e.g. ["git"] matches "git_list_files", "git_read_file") will be exposed to the LLM.
    /// Null or empty = all tools from the service are exposed.
    /// </summary>
    public List<string>? ToolDomains { get; set; }

    /// <summary>Maximum number of tool call iterations before forcing a summary.</summary>
    public int MaxIterations { get; set; } = 15;

    /// <summary>Maximum token budget for this stage.</summary>
    public int MaxTokens { get; set; } = 50000;

    /// <summary>System prompt for the LLM (supports {{token}} substitution).</summary>
    public string SystemPrompt { get; set; } = "You are an AI assistant with access to tools. Use them to research and answer the question.";

    /// <summary>Where to store the final output in workflow context.</summary>
    public string? OutputField { get; set; }
}
