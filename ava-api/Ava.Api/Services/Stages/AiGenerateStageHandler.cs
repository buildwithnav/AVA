using System.Text;
using System.Threading.Channels;
using Ava.Api.Config;
using Ava.Api.Models;
using OpenAI.Chat;

namespace Ava.Api.Services.Stages;

/// <summary>
/// Renders a preview using a template + optional LLM embellishment.
/// Pauses for user confirmation — "yes" proceeds, anything else triggers edit/rewind.
/// Streams LLM tokens in real-time via StreamingChatHelper.
/// </summary>
public class AiGenerateStageHandler : IStageHandler
{
    private readonly IChatClientFactory _chatFactory;
    private readonly StreamingChatHelper _streaming;
    private readonly ITemplateRepository _templates;
    private readonly AgentRunService _agentRun;
    private readonly ILogger<AiGenerateStageHandler> _logger;

    public string StageType => "ai-generate";

    public AiGenerateStageHandler(
        IChatClientFactory chatFactory,
        StreamingChatHelper streaming,
        ITemplateRepository templates,
        AgentRunService agentRun,
        ILogger<AiGenerateStageHandler> logger)
    {
        _chatFactory = chatFactory;
        _streaming   = streaming;
        _templates   = templates;
        _agentRun    = agentRun;
        _logger      = logger;
    }

    public async IAsyncEnumerable<StreamEvent> ExecuteAsync(
        StageHandlerContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (run, prompt, stage, ctx) = (context.Run, context.Prompt, context.Stage, context.WorkflowContext);

        var templateId = stage.TemplateRef;
        TemplateDefinition? template = null;
        if (!string.IsNullOrEmpty(templateId))
            template = await _templates.GetByIdAsync(templateId);

        var sb = new StringBuilder();
        if (template != null)
        {
            sb.AppendLine($"**{template.Description}**\n");
            foreach (var field in template.Fields)
            {
                var val = ctx.Fields.TryGetValue(field.Name, out var v) ? v?.ToString() : null;
                if (!string.IsNullOrEmpty(val))
                    sb.AppendLine($"- **{field.Label}**: {val}");
                else if (field.Required)
                    sb.AppendLine($"- **{field.Label}**: *(not provided)*");
            }
        }

        // Emit template header immediately
        var templateHeader = sb.ToString();
        if (!string.IsNullOrEmpty(templateHeader))
        {
            yield return new StreamEvent { Type = "token", Data = templateHeader };
        }

        if (!string.IsNullOrEmpty(stage.Prompt))
        {
            var aiPrompt = TokenResolver.Resolve(stage.Prompt, ctx.Fields);
            var contextSummary = TokenResolver.BuildContextSummary(ctx.Fields, onlyEssential: true, maxFieldLength: 300);
            var client = await _chatFactory.CreateAsync(context.SessionId, context.WorkflowContext.ModelOverride);

            var systemSuffix = template != null
                ? "Generate a clear preview for the user to review and confirm. Be concise."
                : "Generate the full content for the user to review and confirm.";

            // When the workflow preserves context, include the prior conversation for reference.
            var convoBlock = TokenResolver.FormatConversationHistory(ctx.ConversationHistory);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage($"{prompt.SystemPrompt}\n\n{systemSuffix}"),
                new UserChatMessage($"{convoBlock}{aiPrompt}\n\nExtracted data:\n{contextSummary}")
            };

            var channel = Channel.CreateUnbounded<StreamEvent>();
            var resultHolder = new string[1];
            var streamFailed = false;

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
                    _logger.LogError(ex, "ai-generate AI call failed for stage '{StageId}'", stage.StageId);
                    streamFailed = true;
                    resultHolder[0] = ex.Message;
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }

            if (streamFailed)
            {
                var errorMsg = $"\n⚠️ Content generation encountered an error: {resultHolder[0]}";
                sb.AppendLine(errorMsg);
                yield return new StreamEvent { Type = "token", Data = errorMsg };
            }
            else
            {
                sb.AppendLine("\n" + resultHolder[0]);
            }
        }

        var previewText = sb.ToString().Trim();

        if (!string.IsNullOrEmpty(previewText))
            ctx.SetField(stage.StageId, "_generatedContent", previewText);

        if (string.IsNullOrEmpty(previewText))
        {
            var fallback = new StringBuilder();
            fallback.AppendLine("**Preview of extracted fields:**\n");
            foreach (var kvp in ctx.Fields.Where(f => !f.Key.StartsWith("_result_") && f.Key != "_generatedContent"))
            {
                if (kvp.Value != null)
                    fallback.AppendLine($"- **{kvp.Key}**: {kvp.Value}");
            }
            previewText = fallback.ToString().Trim();
            yield return new StreamEvent { Type = "token", Data = previewText };
        }

        var confirmPrompt = "\n\nDoes this look correct? Reply **yes** to proceed or describe any changes.";
        yield return new StreamEvent { Type = "token", Data = confirmPrompt };

        var stepExec = new StepExecution
        {
            StepId = stage.StageId,
            ToolName = "ai-generate",
            Success = true,
            Result = previewText,
            TokensUsed = TokenResolver.EstimateContextTokens(ctx.Fields)
        };
        run.Steps.Add(stepExec);
        yield return new StreamEvent { Type = "step_done", Data = stepExec };

        await _agentRun.PauseForInputAsync(run, ctx, "ai-generate");
        ctx.PausePayload = new { preview = previewText, templateId };

        yield return new StreamEvent
        {
            Type = "user_input_required",
            Data = new { run.AgentRunId, inputType = "confirm", preview = previewText, run.TokensUsed }
        };
        yield return new StreamEvent { Type = "done", Data = new { run.AgentRunId, resumeRunId = run.AgentRunId, run.TokensUsed } };
    }
}
