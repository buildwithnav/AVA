using System.Text;
using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Models;
using OpenAI.Chat;

namespace Ava.Api.Services.Stages;

/// <summary>
/// Executes an API tool. Two modes:
/// - Fixed: stage specifies toolId + parameters with {{token}} substitution.
/// - PrePrompt: LLM picks the tool from allowedToolIds.
/// </summary>
public class ToolStageHandler : IStageHandler
{
    private readonly IChatClientFactory _chatFactory;
    private readonly IToolRepository _tools;
    private readonly ToolExecutorService _toolExecutor;
    private readonly IPiiDetectionService _piiDetection;
    private readonly AgentRunService _agentRun;
    private readonly McpClientService _mcp;
    private readonly ILogger<ToolStageHandler> _logger;

    public string StageType => "tool";

    /// <summary>Matches any {{token}} left unresolved after token substitution.</summary>
    private static readonly System.Text.RegularExpressions.Regex UnresolvedTokenRegex =
        new(@"\{\{\s*[^{}]+?\s*\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    public ToolStageHandler(
        IChatClientFactory chatFactory,
        IToolRepository tools,
        ToolExecutorService toolExecutor,
        IPiiDetectionService piiDetection,
        AgentRunService agentRun,
        McpClientService mcp,
        ILogger<ToolStageHandler> logger)
    {
        _chatFactory   = chatFactory;
        _tools         = tools;
        _toolExecutor  = toolExecutor;
        _piiDetection  = piiDetection;
        _agentRun      = agentRun;
        _mcp           = mcp;
        _logger        = logger;
    }

    public async IAsyncEnumerable<StreamEvent> ExecuteAsync(
        StageHandlerContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (run, prompt, stage, ctx) = (context.Run, context.Prompt, context.Stage, context.WorkflowContext);

        string toolId = string.Empty;
        Dictionary<string, object?> toolParams = new();

        if (stage.PrePrompt != null)
        {
            var pp = stage.PrePrompt;
            var resolvedPrompt = TokenResolver.Resolve(pp.Prompt, ctx.Fields);
            var allowedTools = await GetToolDescriptionsAsync(pp.AllowedToolIds);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    $"You are a tool selection assistant. Given the user context and available tools, " +
                    $"select the correct tool and provide its parameters.\n\n" +
                    $"Available tools:\n{allowedTools}\n\n" +
                    $"Return ONLY valid JSON in this format: {{ \"toolId\": \"...\", \"params\": {{ ... }} }}\n\n" +
                    $"{resolvedPrompt}"),
                new UserChatMessage(TokenResolver.BuildContextSummary(ctx.Fields, onlyEssential: true, maxFieldLength: 200))
            };

            string rawJson;
            string? prePromptError = null;
            try
            {
                var client = await _chatFactory.CreateAsync(context.SessionId);
                var response = await client.CompleteChatAsync(messages);
                rawJson = JsonHelper.StripMarkdownFences(response.Value.Content[0].Text.Trim());
                run.TokensUsed += response.Value.Usage.TotalTokenCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "tool prePrompt AI call failed");
                prePromptError = "Tool selection failed.";
                rawJson = string.Empty;
            }

            if (prePromptError != null)
            {
                await _agentRun.FailAsync(run, "tool prePrompt failed");
                yield return new StreamEvent { Type = "error", Data = prePromptError };
                yield break;
            }

            string? parseError = null;
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                toolId = doc.RootElement.GetProperty("toolId").GetString() ?? string.Empty;
                var paramsEl = doc.RootElement.GetProperty("params");
                toolParams = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    paramsEl.GetRawText(), JsonHelper.Options) ?? new();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse prePrompt tool selection: {Raw}", rawJson);
                parseError = "Could not determine which tool to use.";
            }

            if (parseError != null)
            {
                await _agentRun.FailAsync(run, "tool prePrompt parse failed");
                yield return new StreamEvent { Type = "error", Data = parseError };
                yield break;
            }

            if (!pp.AllowedToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("AI selected disallowed tool '{ToolId}'", toolId);
                await _agentRun.FailAsync(run, "disallowed tool selected");
                yield return new StreamEvent { Type = "error", Data = $"Selected tool '{toolId}' is not allowed for this stage." };
                yield break;
            }

            // Merge static param overrides on top of LLM-selected params (static wins).
            // Supports {{token}} substitution so authors can wire context fields.
            if (pp.StaticParams?.Count > 0)
            {
                foreach (var (key, raw) in pp.StaticParams)
                {
                    var resolved = TokenResolver.Resolve(raw, ctx.Fields);
                    if (!string.IsNullOrEmpty(resolved))
                        toolParams[key] = resolved;
                }
            }
        }
        else
        {
            toolId = stage.ToolId ?? string.Empty;
            toolParams = (stage.Parameters ?? new())
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object?)TokenResolver.Resolve(kvp.Value, ctx.Fields));

            // Strip params that are empty or still contain an unresolved {{token}}.
            // Unresolved tokens mean no upstream stage produced that value — treat the
            // param as optional/absent so the tool call proceeds without it.
            var keysToStrip = toolParams
                .Where(kvp =>
                {
                    var val = kvp.Value?.ToString();
                    return string.IsNullOrEmpty(val) || UnresolvedTokenRegex.IsMatch(val);
                })
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in keysToStrip)
            {
                _logger.LogInformation("tool stage '{StageId}': stripping unresolved/empty param '{Key}'", stage.StageId, key);
                toolParams.Remove(key);
            }
        }

        // PII check on tool params
        if (prompt.PiiCheck)
        {
            var stringValues = toolParams.Values.Where(v => v is string).Cast<string>().ToList();
            var piiResult = await _piiDetection.DetectAsync(stringValues, run.Domain);
            if (piiResult.Detected)
            {
                var piiMsg = $"PII detected in tool parameters ({piiResult.Summary}). Request blocked.";
                await _agentRun.PauseForInputAsync(run, ctx, "clarify");
                yield return new StreamEvent { Type = "token", Data = piiMsg };
                yield return new StreamEvent { Type = "done", Data = new { run.AgentRunId, resumeRunId = run.AgentRunId } };
                yield break;
            }
        }

        var toolDef = await _tools.GetByIdAsync(toolId);
        if (toolDef == null)
        {
            // MCP tools (ado-*) are not stored in the DB — they are surfaced dynamically
            // by ToolsController from the live MCP server (Context Forge gateway). Synthesize a
            // virtual ToolDefinition pointed at the "context-forge" service so ToolExecutorService
            // can route it through its enrichment table and McpClientService's canonical/prefix-
            // stripped name resolver (do NOT set mcp: prefix — let the executor/resolver resolve).
            if (toolId.StartsWith("ado-", StringComparison.OrdinalIgnoreCase))
            {
                toolDef = new ToolDefinition
                {
                    Id          = toolId,
                    Service     = "context-forge",
                    Route       = toolId,   // no "mcp:" prefix — executor uses mapping/convention
                    HttpMethod  = "MCP",
                    Description = "",
                    Parameters  = new(),
                    Internal    = false,
                };
                _logger.LogInformation(
                    "Tool '{ToolId}' not in DB — synthesizing virtual MCP tool definition (context-forge)", toolId);
            }
            else
            {
                await _agentRun.FailAsync(run, $"tool not found: {toolId}");
                yield return new StreamEvent { Type = "error", Data = $"Tool '{toolId}' not found." };
                yield break;
            }
        }

        yield return new StreamEvent { Type = "step_start", Data = new { stageId = stage.StageId, toolId } };

        var (success, result, durationMs) = await _toolExecutor.ExecuteAsync(toolDef, toolParams, context.SessionId, context.Request?.ConversationId);

        var stepExec = new StepExecution
        {
            StepId = stage.StageId,
            ToolName = toolId,
            Success = success,
            Result = success ? result : null,
            Error = success ? null : result,
            DurationMs = durationMs,
            TokensUsed = TokenResolver.EstimateContextTokens(ctx.Fields)
        };
        run.Steps.Add(stepExec);

        if (success)
        {
            // Snapshot before extraction so we can journal all new/modified keys.
            // Tool outputs are written directly to ctx.Fields (bypassing ctx.SetField)
            // because JsonHelper.ExtractOutputs doesn't have context awareness. We
            // reconstruct the journal entries here so that UI-driven RewindToStageAsync
            // can cleanly roll back tool outputs alongside ai-extract outputs.
            var snapshot = new Dictionary<string, object?>(ctx.Fields);

            JsonHelper.ExtractOutputs(result, stage.OutputMap, ctx.Fields);
            ctx.Fields[$"_result_{stage.StageId}"] = result;

            foreach (var key in ctx.Fields.Keys)
            {
                bool wasPresent = snapshot.TryGetValue(key, out var prev);
                if (!wasPresent || !Equals(prev, ctx.Fields[key]))
                {
                    ctx.FieldJournal.Add(new FieldWriteEntry
                    {
                        StageId          = stage.StageId,
                        FieldName        = key,
                        PreviousValue    = wasPresent ? prev : null,
                        HadPreviousValue = wasPresent
                    });
                }
            }
        }

        yield return new StreamEvent { Type = "step_done", Data = stepExec };

        if (!success)
        {
            _logger.LogWarning("Tool '{ToolId}' failed: {Error}", toolId, result);
            // Non-fatal — workflow continues; ai-analyze will report the failure
        }
    }

    private async Task<string> GetToolDescriptionsAsync(List<string> toolIds)
    {
        var sb = new StringBuilder();
        var mcpCache = _mcp.GetCachedToolDefinitions();

        foreach (var id in toolIds)
        {
            var tool = await _tools.GetByIdAsync(id);
            if (tool == null)
            {
                // MCP tool not in DB — look up cached schema from the live MCP server
                if (id.StartsWith("ado-", StringComparison.OrdinalIgnoreCase)
                    && mcpCache.TryGetValue(id, out var mcpTool))
                {
                    sb.AppendLine($"- {id}: {mcpTool.Description}");
                    var mcpParams = McpClientService.ParseSchemaToParameters(mcpTool.InputSchema);
                    var required = mcpParams.Where(p => p.Required).Select(p => p.Name).ToList();
                    var optional = mcpParams.Where(p => !p.Required).Select(p => p.Name).ToList();
                    if (required.Count > 0) sb.AppendLine($"  Required params: {string.Join(", ", required)}");
                    if (optional.Count > 0) sb.AppendLine($"  Optional params: {string.Join(", ", optional)}");
                }
                else
                {
                    sb.AppendLine($"- {id}: ADO MCP tool (context-forge service)");
                }
                continue;
            }
            sb.AppendLine($"- {tool.Id}: {tool.Description}");
            var req = tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToList();
            var opt = tool.Parameters.Where(p => !p.Required).Select(p => p.Name).ToList();
            if (req.Any()) sb.AppendLine($"  Required params: {string.Join(", ", req)}");
            if (opt.Any()) sb.AppendLine($"  Optional params: {string.Join(", ", opt)}");
        }
        return sb.ToString();
    }
}
