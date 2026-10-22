using Ava.Api.Models;

namespace Ava.Api.Services.Stages;

/// <summary>
/// Handles execution of a single workflow stage type.
/// Implementations are registered in DI and resolved by <see cref="StageType"/>.
/// </summary>
public interface IStageHandler
{
    /// <summary>The stage type this handler processes (e.g. "ai-extract", "tool").</summary>
    string StageType { get; }

    /// <summary>Execute the stage, yielding stream events. May pause the run.</summary>
    IAsyncEnumerable<StreamEvent> ExecuteAsync(StageHandlerContext context, CancellationToken ct);
}

/// <summary>
/// All the state a stage handler needs to execute. Passed in by the workflow engine.
/// </summary>
public class StageHandlerContext
{
    public required AgentRun Run { get; init; }
    public required PromptDefinition Prompt { get; init; }
    public required WorkflowStage Stage { get; init; }
    public required WorkflowContext WorkflowContext { get; init; }
    public required ChatRequest? Request { get; init; }
    public required string? SessionId { get; init; }
}
