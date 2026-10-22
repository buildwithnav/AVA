using System.Text.Json;
using Ava.Api.Config;
using Ava.Api.Helpers;
using Ava.Api.Models;
using Ava.Api.Services;
using Ava.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly AgentService _agent;
    private readonly AgentRunService _agentRun;
    private readonly WorkflowEngine _workflowEngine;
    private readonly WorkflowCreatorService _workflowCreator;
    private readonly SemanticSearchService _semanticSearch;
    private readonly IDomainRepository _domains;
    private readonly ISessionStore _sessionStore;
    private readonly IChatClientFactory _chatFactory;
    private readonly AccessService _access;
    private readonly ILogger<ChatController> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ChatController(
        AgentService agent,
        AgentRunService agentRun,
        WorkflowEngine workflowEngine,
        WorkflowCreatorService workflowCreator,
        SemanticSearchService semanticSearch,
        IDomainRepository domains,
        ISessionStore sessionStore,
        IChatClientFactory chatFactory,
        AccessService access,
        ILogger<ChatController> logger)
    {
        _agent          = agent;
        _agentRun       = agentRun;
        _workflowEngine = workflowEngine;
        _workflowCreator = workflowCreator;
        _semanticSearch = semanticSearch;
        _domains        = domains;
        _sessionStore   = sessionStore;
        _chatFactory    = chatFactory;
        _access         = access;
        _logger         = logger;
    }

    /// <summary>
    /// POST /api/chat
    /// Runs the agent loop and streams events back as SSE.
    /// If ResumeRunId is set, resumes a paused staged workflow.
    /// </summary>
    [HttpPost]
    public async Task Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("""{"error":"message is required"}""", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
            request.ConversationId = Guid.NewGuid().ToString();

        var pylonUser = HttpContext.GetPylonUser();
        if (!pylonUser.IsPresent)
        {
            Response.StatusCode = 401;
            await Response.WriteAsync("""{"error":"authentication required"}""", ct);
            return;
        }
        request.Context.UserId   = pylonUser.UserId;
        request.Context.Username = pylonUser.Username;

        if (!await AuthorizeContextAsync(request.Context))
            return;

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        _logger.LogInformation("Chat request from {User} in domain {Domain}: {Msg}",
            request.Context.UserId, request.Context.Domain, request.Message);

        await foreach (var evt in _agent.RunAsync(request))
        {
            if (ct.IsCancellationRequested) break;
            var data = JsonSerializer.Serialize(evt, _jsonOptions);
            await Response.WriteAsync($"data: {data}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Verifies the caller can reach the requested domain/sub-domain. Writes an SSE error and
    /// returns false when access is denied — callers must return immediately.
    /// </summary>
    private async Task<bool> AuthorizeContextAsync(UserContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Domain))
            return true;   // "All domains" chat — domain filtering happens per-lookup downstream.

        var allowed = string.IsNullOrWhiteSpace(context.SubDomain)
            ? await _access.HasDomainAccessAsync(context.Domain)
            : await _access.HasChildDomainAccessAsync(context.Domain, context.SubDomain);

        if (allowed) return true;

        _logger.LogWarning("Access denied for {User} on {Domain}/{SubDomain}",
            context.UserId, context.Domain, context.SubDomain ?? "-");

        Response.StatusCode = 403;
        await Response.WriteAsync("""{"error":"You do not have access to this domain."}""");
        return false;
    }

    /// <summary>
    /// POST /api/chat/select
    /// Merges a user selection (user-select, ai-generate confirm) into the paused run context.
    /// Returns { resumeRunId } so the client can resume via POST /api/chat with ResumeRunId.
    /// </summary>
    [HttpPost("select")]
    public async Task<IActionResult> Select([FromBody] SelectRequest request)
    {
        var run = await _agentRun.GetAsync(request.AgentRunId);
        if (run == null)
            return NotFound(new { error = $"Run '{request.AgentRunId}' not found." });

        if (run.Status != AgentRunStatus.WaitingForInput)
            return BadRequest(new { error = "Run is not waiting for input." });

        if (request.InputType == "dismiss")
        {
            await _agentRun.FailAsync(run, "User dismissed the workflow.");
            return Ok(new { message = "Workflow dismissed." });
        }

        await _agentRun.MergeSelectionAsync(run, request);

        if (run.Status == AgentRunStatus.Cancelled)
            return Ok(new { message = "Workflow cancelled." });

        return Ok(new { resumeRunId = run.AgentRunId });
    }

    /// <summary>
    /// GET /api/chat/runs/{runId}
    /// Returns the full AgentRun record.
    /// </summary>
    [HttpGet("runs/{runId}")]
    public async Task<IActionResult> GetRun(string runId)
    {
        var run = await _agentRun.GetAsync(runId);
        if (run == null) return NotFound(new { error = $"Run '{runId}' not found" });
        return Ok(run);
    }

    /// <summary>
    /// GET /api/chat/conversations/{conversationId}/runs
    /// Lists all agent runs for a conversation.
    /// </summary>
    [HttpGet("conversations/{conversationId}/runs")]
    public async Task<IActionResult> GetConversationRuns(string conversationId)
    {
        var runs = await _agentRun.GetByConversationAsync(conversationId);
        return Ok(new { count = runs.Count, runs });
    }

    /// <summary>
    /// GET /api/chat/sessions/{sessionId}/conversations
    /// Lists all conversation IDs linked to a session (most recent first).
    /// </summary>
    [HttpGet("sessions/{sessionId}/conversations")]
    public async Task<IActionResult> GetSessionConversations(string sessionId)
    {
        var conversations = await _sessionStore.GetConversationsBySessionAsync(sessionId);
        return Ok(new { count = conversations.Count, conversations });
    }


    /// <summary>
    /// POST /api/chat/rewind
    /// Rewinds a paused workflow to a previous user-select stage.
    /// Returns { resumeRunId, revision } so the client can resume via POST /api/chat.
    /// </summary>
    [HttpPost("rewind")]
    public async Task<IActionResult> Rewind([FromBody] RewindRequest request)
    {
        var run = await _agentRun.GetAsync(request.AgentRunId);
        if (run == null)
            return NotFound(new { error = $"Run '{request.AgentRunId}' not found." });

        if (run.Status != AgentRunStatus.WaitingForInput && run.Status != AgentRunStatus.Completed)
            return BadRequest(new { error = "Run is not in a rewindable state (must be paused or completed)." });

        var (success, error) = await _workflowEngine.RewindToStageAsync(run, request.TargetStageId);
        if (!success)
            return BadRequest(new { error });

        return Ok(new { resumeRunId = run.AgentRunId, revision = run.WorkflowContext?.Revision ?? 0 });
    }

    /// <summary>
    /// POST /api/chat/test-workflow
    /// Directly executes a workflow definition against a test message.
    /// Bypasses the agent loop and semantic search — used by the workflow builder
    /// Test tab to preview in-progress (potentially unsaved) workflows.
    /// </summary>
    [HttpPost("test-workflow")]
    public async Task TestWorkflow([FromBody] TestWorkflowRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("""{"error":"message is required"}""", ct);
            return;
        }

        if (request.WorkflowDefinition?.Stages == null || request.WorkflowDefinition.Stages.Count == 0)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("""{"error":"workflowDefinition with at least one stage is required"}""", ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        _logger.LogInformation("Test-workflow request from {User} in domain {Domain}: {Msg}",
            request.UserId, request.Domain, request.Message);

        var conversationId = Guid.NewGuid().ToString();
        var run = await _agentRun.CreateAsync(conversationId, request.UserId, request.Domain, request.Message);
        run.MatchedPromptId = request.WorkflowDefinition.Id;
        run.SessionId = request.SessionId;
        run.ModelId   = await _chatFactory.GetModelIdAsync(
            request.SessionId, request.WorkflowDefinition.ModelId);

        // Emit run_started
        var startEvt = new StreamEvent { Type = "run_started", Data = new { run.AgentRunId } };
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(startEvt, _jsonOptions)}\n\n", ct);
        await Response.Body.FlushAsync(ct);

        // Build a ChatRequest for the workflow engine
        var chatRequest = new ChatRequest
        {
            Message = request.Message,
            ConversationId = conversationId,
            SessionId = request.SessionId,
            Context = new UserContext
            {
                Domain = request.Domain,
                UserId = request.UserId,
                Roles = new List<string>()
            }
        };

        string? resumeRunId = null;
        try
        {
            await foreach (var evt in _workflowEngine.ExecuteAsync(run, request.WorkflowDefinition, chatRequest, ct))
            {
                if (ct.IsCancellationRequested) break;

                // Capture resumeRunId from the engine's done event (workflow paused for user input)
                if (evt.Type == "done" && evt.Data != null)
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(evt.Data, _jsonOptions);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("resumeRunId", out var rid))
                            resumeRunId = rid.GetString();
                    }
                    catch { /* ignore parse errors */ }
                }

                var data = JsonSerializer.Serialize(evt, _jsonOptions);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test-workflow execution failed");
            var errEvt = new StreamEvent { Type = "error", Data = ex.Message };
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(errEvt, _jsonOptions)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        // Emit final done — preserve resumeRunId if workflow paused for user input
        var doneEvt = new StreamEvent { Type = "done", Data = new { run.AgentRunId, resumeRunId, run.TokensUsed, run.ModelId, run.EstimatedCostUsd } };
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(doneEvt, _jsonOptions)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// POST /api/chat/test-workflow/resume
    /// Resumes a paused test-workflow run (e.g. after a confirmation stage).
    /// The client sends the agentRunId from the paused run plus the user's response.
    /// </summary>
    [HttpPost("test-workflow/resume")]
    public async Task TestWorkflowResume([FromBody] TestWorkflowRequest request, CancellationToken ct)
    {
        var agentRunId = request.AgentRunId;
        if (string.IsNullOrWhiteSpace(agentRunId))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("""{"error":"agentRunId is required to resume"}""", ct);
            return;
        }

        var existing = await _agentRun.GetAsync(agentRunId);
        if (existing == null)
        {
            Response.StatusCode = 404;
            await Response.WriteAsync($"{{\"error\":\"Run '{agentRunId}' not found.\"}}", ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        _logger.LogInformation("Test-workflow resume for run {RunId}, user response: {Msg}",
            agentRunId, request.Message);

        var chatRequest = new ChatRequest
        {
            Message = request.Message,
            ConversationId = existing.ConversationId,
            SessionId = request.SessionId,
            Context = new UserContext
            {
                Domain = request.Domain,
                UserId = request.UserId,
                Roles = new List<string>()
            }
        };

        // Emit run_started
        var startEvt = new StreamEvent { Type = "run_started", Data = new { existing.AgentRunId } };
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(startEvt, _jsonOptions)}\n\n", ct);
        await Response.Body.FlushAsync(ct);

        string? resumeRunId = null;
        try
        {
            await foreach (var evt in _workflowEngine.ResumeAsync(existing, chatRequest, request.WorkflowDefinition, ct))
            {
                if (ct.IsCancellationRequested) break;

                if (evt.Type == "done" && evt.Data != null)
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(evt.Data, _jsonOptions);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("resumeRunId", out var rid))
                            resumeRunId = rid.GetString();
                    }
                    catch { /* ignore parse errors */ }
                }

                var data = JsonSerializer.Serialize(evt, _jsonOptions);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test-workflow resume failed");
            var errEvt = new StreamEvent { Type = "error", Data = ex.Message };
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(errEvt, _jsonOptions)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        var doneEvt = new StreamEvent { Type = "done", Data = new { existing.AgentRunId, resumeRunId, existing.TokensUsed, existing.ModelId, existing.EstimatedCostUsd } };
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(doneEvt, _jsonOptions)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// POST /api/chat/create-workflow
    /// AI-assisted workflow creation. Streams text tokens and creator_action events.
    /// The agent uses structured tools to incrementally build the workflow definition.
    /// </summary>
    [HttpPost("create-workflow")]
    public async Task CreateWorkflow([FromBody] CreateWorkflowRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("""{"error":"message is required"}""", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
            request.ConversationId = Guid.NewGuid().ToString();

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        _logger.LogInformation("Create-workflow request from {User}: {Msg}",
            request.UserId, request.Message);

        try
        {
            await foreach (var evt in _workflowCreator.CreateWorkflowAsync(request, ct))
            {
                if (ct.IsCancellationRequested) break;
                var data = JsonSerializer.Serialize(evt, _jsonOptions);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create-workflow failed");
            var errEvt = new StreamEvent { Type = "error", Data = ex.Message };
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(errEvt, _jsonOptions)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>
    /// POST /api/chat/detect-domain
    /// Detects the best domain for a message using semantic/keyword search.
    /// </summary>
    [HttpPost("detect-domain")]
    public async Task<IActionResult> DetectDomain([FromBody] DetectDomainRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required" });

        // Detection must never surface a domain the caller can't open, or it becomes a
        // side channel revealing which domains (and workflows) exist.
        var accessible = await _access.FilterDomainsAsync(await _domains.GetAllAsync());
        var allowedDomains = accessible.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = await _semanticSearch.SearchAsync(request.Message, null);
        var topMatch = results.Where(r => allowedDomains.Contains(r.Prompt.Domain)).Take(1).ToList();
        if (topMatch.Count > 0)
            return Ok(new { domain = topMatch[0].Prompt.Domain, confidence = topMatch[0].Score, promptId = topMatch[0].Prompt.Id });

        // Fallback to keyword search
        var kwResults = _semanticSearch.KeywordSearch(request.Message, null, 10);
        var topKeyword = kwResults.Where(r => allowedDomains.Contains(r.Prompt.Domain)).Take(1).ToList();
        if (topKeyword.Count > 0)
            return Ok(new { domain = topKeyword[0].Prompt.Domain, confidence = topKeyword[0].Score, promptId = topKeyword[0].Prompt.Id });

        return Ok(new { domain = (string?)null, confidence = 0.0, promptId = (string?)null });
    }
}

