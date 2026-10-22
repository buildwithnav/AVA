using Ava.Api.Models;

namespace Ava.Api.Services.Stages;

/// <summary>
/// Simple pause asking user to confirm before proceeding.
/// Resume: truthy reply advances; non-truthy stays at same stage.
/// </summary>
public class ConfirmationStageHandler : IStageHandler
{
    private readonly AgentRunService _agentRun;

    public string StageType => "confirmation";

    public ConfirmationStageHandler(AgentRunService agentRun)
    {
        _agentRun = agentRun;
    }

    public async IAsyncEnumerable<StreamEvent> ExecuteAsync(
        StageHandlerContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (run, stage, ctx) = (context.Run, context.Stage, context.WorkflowContext);

        var confirmText = TokenResolver.Resolve(
            stage.Prompt ?? "Please confirm you want to proceed.",
            ctx.Fields);

        await _agentRun.PauseForInputAsync(run, ctx, "confirmation");
        ctx.PausePayload = new { prompt = confirmText };

        yield return new StreamEvent
        {
            Type = "confirmation_required",
            Data = new { run.AgentRunId, prompt = confirmText }
        };
        yield return new StreamEvent { Type = "done", Data = new { run.AgentRunId, resumeRunId = run.AgentRunId } };
    }
}
