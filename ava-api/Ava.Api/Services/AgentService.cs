using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ava.Api.Config;
using Ava.Api.Models;
using Ava.Api.Services.Security;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Ava.Api.Services;

/// <summary>
/// Unified agent loop: every message goes through the tool-enabled conversation.
/// The LLM decides whether to respond conversationally, query a KB, or trigger
/// a workflow via start_workflow. Semantic search provides a shortlist of
/// candidate workflows in the system prompt so the LLM can make an informed choice.
/// </summary>
public class AgentService
{
    private readonly SemanticSearchService _semanticSearch;
    private readonly AgentRunService _agentRun;
    private readonly WorkflowEngine _workflowEngine;
    private readonly ConversationService _conversation;
    private readonly IChatClientFactory _chatFactory;
    private readonly StreamingChatHelper _streaming;
    private readonly KnowledgeBaseService _kbService;
    private readonly DomainFileService _domainFileService;
    private readonly QueryRouterService _queryRouter;
    private readonly ToolRegistryService _toolRegistry;
    private readonly ToolExecutorService _toolExecutor;
    private readonly IDomainRepository _domains;
    private readonly KbExportCacheService _exportCache;
    private readonly AvaSettings _settings;
    private readonly AccessService _access;
    private readonly ILogger<AgentService> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AgentService(
        SemanticSearchService semanticSearch,
        AgentRunService agentRun,
        WorkflowEngine workflowEngine,
        ConversationService conversation,
        IChatClientFactory chatFactory,
        StreamingChatHelper streaming,
        KnowledgeBaseService kbService,
        DomainFileService domainFileService,
        QueryRouterService queryRouter,
        ToolRegistryService toolRegistry,
        ToolExecutorService toolExecutor,
        IDomainRepository domains,
        KbExportCacheService exportCache,
        IOptions<AvaSettings> settings,
        AccessService access,
        ILogger<AgentService> logger)
    {
        _semanticSearch  = semanticSearch;
        _agentRun        = agentRun;
        _workflowEngine  = workflowEngine;
        _conversation    = conversation;
        _chatFactory     = chatFactory;
        _streaming       = streaming;
        _kbService       = kbService;
        _domainFileService = domainFileService;
        _queryRouter     = queryRouter;
        _toolRegistry    = toolRegistry;
        _toolExecutor    = toolExecutor;
        _domains         = domains;
        _exportCache     = exportCache;
        _settings        = settings.Value;
        _access          = access;
        _logger          = logger;
    }

    /// <summary>
    /// Runs the agent loop, streaming events to the caller.
    /// Every message goes through the unified tool loop — the LLM decides
    /// whether to respond, query KBs, or start a workflow.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> RunAsync(ChatRequest request)
    {
        // ── /undo command handling ─────────────────────────────────────────
        if (request.Message.Trim().Equals("/undo", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await _conversation.PopSnapshotAsync(request.ConversationId);
            if (snapshot == null)
            {
                yield return new StreamEvent { Type = "error", Data = "Nothing to undo." };
                yield return new StreamEvent { Type = "done", Data = new { } };
                yield break;
            }

            await _conversation.RestoreSnapshotAsync(request.ConversationId, snapshot);
            var lastUserMessage = snapshot.Messages.LastOrDefault(m => m.Role == "user")?.Content;

            yield return new StreamEvent
            {
                Type = "undo",
                Data = new { restoredMessageCount = snapshot.Messages.Count, lastUserMessage }
            };
            yield return new StreamEvent { Type = "done", Data = new { } };
            yield break;
        }

        // ── Resume path ───────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(request.ResumeRunId))
        {
            var existing = await _agentRun.GetAsync(request.ResumeRunId);
            if (existing == null)
            {
                yield return new StreamEvent { Type = "error", Data = $"Run '{request.ResumeRunId}' not found." };
                yield break;
            }
            yield return new StreamEvent { Type = "run_started", Data = new { existing.AgentRunId } };
            await foreach (var evt in _workflowEngine.ResumeAsync(existing, request, default))
                yield return evt;
            yield break;
        }

        // ── Welcome event on first message ────────────────────────────────
        var convHistory = await _conversation.GetHistoryAsync(request.ConversationId);
        if (convHistory.Count == 0)
        {
            var domain = await _domains.GetByIdAsync(request.Context.Domain);
            if (domain != null)
            {
                var domainKbs = await _kbService.GetForDomainAsync(domain.Id);
                var kbSummaries = domainKbs.Select(kb => new
                {
                    id = kb.Id,
                    name = kb.Name,
                    type = kb.Type,
                    rowCount = kb.RowCount,
                    chunkCount = kb.ChunkCount
                }).ToList();

                yield return new StreamEvent
                {
                    Type = "welcome",
                    Data = new
                    {
                        domain = domain.Id,
                        domainName = domain.Name,
                        message = domain.WelcomeMessage ?? $"Welcome! I can help you with {domain.Name} tasks.",
                        suggestions = domain.Suggestions,
                        knowledgeBases = kbSummaries
                    }
                };
            }
        }

        // ── Create run ────────────────────────────────────────────────────
        var run = await _agentRun.CreateAsync(
            request.ConversationId, request.Context.UserId,
            request.Context.Domain, request.Message, request.Context.Username);

        // Stamp session + resolved model ID on the run for usage tracking
        run.SessionId = request.SessionId;
        run.ModelId   = await _chatFactory.GetModelIdAsync(request.SessionId);

        yield return new StreamEvent { Type = "run_started", Data = new { run.AgentRunId } };

        // ── Retrieve workflow candidates (top 5) ──────────────────────────
        var workflowCandidates = await GetWorkflowCandidatesAsync(
            request.Message, request.Context.Domain);

        _logger.LogInformation(
            "Chat [{ConvId}] user={User} domain={Domain} candidates=[{Candidates}]",
            request.ConversationId, request.Context.UserId, request.Context.Domain,
            string.Join(", ", workflowCandidates.Select(c => $"{c.Id}({c.Score:F2})")));

        // ── Single path: tool-enabled conversation ────────────────────────
        await foreach (var evt in RunToolLoopAsync(run, request, convHistory, workflowCandidates))
            yield return evt;
    }

    // ── Workflow candidate retrieval ──────────────────────────────────────────

    /// <summary>Domain IDs the current caller can browse — used to hide denied domains' workflows from candidate lists.</summary>
    private async Task<HashSet<string>> GetAccessibleDomainIdsAsync()
    {
        var accessible = await _access.FilterDomainsAsync(await _domains.GetAllAsync());
        return accessible.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<WorkflowCandidate>> GetWorkflowCandidatesAsync(
        string message, string domain, int topK = 5)
    {
        var candidates = new List<WorkflowCandidate>();

        // Denied-domain workflows must never surface as candidates — in "All domains" chat
        // (domain is empty) SearchAsync/KeywordSearch apply no domain filter of their own.
        var allowedDomains = await GetAccessibleDomainIdsAsync();

        // Try semantic search first
        var semanticResults = await _semanticSearch.SearchAsync(message, domain);
        if (semanticResults.Any())
        {
            candidates = semanticResults
                .Where(r => r.Prompt.Stages?.Any() == true) // only workflows
                .Where(r => allowedDomains.Contains(r.Prompt.Domain))
                .Take(topK)
                .Select(r => new WorkflowCandidate
                {
                    Id = r.Prompt.Id,
                    Description = r.Prompt.Description,
                    Score = r.Score
                })
                .ToList();
        }

        // If semantic search returned nothing (embeddings unavailable), fall back to keyword
        if (candidates.Count == 0)
        {
            var kwResults = _semanticSearch.KeywordSearch(message, domain, topK);
            candidates = kwResults
                .Where(r => r.Prompt.Stages?.Any() == true)
                .Where(r => allowedDomains.Contains(r.Prompt.Domain))
                .Take(topK)
                .Select(r => new WorkflowCandidate
                {
                    Id = r.Prompt.Id,
                    Description = r.Prompt.Description,
                    Score = r.Score
                })
                .ToList();
        }

        return candidates;
    }

    // ── Unified tool loop ─────────────────────────────────────────────────────

    private async IAsyncEnumerable<StreamEvent> RunToolLoopAsync(
        AgentRun run,
        ChatRequest request,
        IReadOnlyList<ConversationMessage> history,
        List<WorkflowCandidate> workflowCandidates)
    {
        // Snapshot for undo
        await _conversation.CreateSnapshotAsync(request.ConversationId);

        // Diagnostics tracker
        var diagnostics = new RunDiagnostics();

        // Reproducible CSV exports recorded from successful kb_query calls this turn.
        var tableExports = new List<TableExport>();

        // Track last chunk IDs from domain tools for conversation context persistence
        var lastDomainChunkIds = new List<string>();

        // Build system prompt (also populates diagnostics with domain files)
        var systemPrompt = await BuildSystemPromptAsync(request, history, workflowCandidates, diagnostics);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        // Add conversation history
        foreach (var turn in history.TakeLast(10))
        {
            if (turn.Role == "user")
                messages.Add(new UserChatMessage(turn.Content));
            else
                messages.Add(new AssistantChatMessage(turn.Content));
        }

        messages.Add(new UserChatMessage(request.Message));

        // Static tool set — all internal tools always available (industry standard).
        // The system prompt + KB summary drives the LLM's routing decisions.
        var chatTools = _toolRegistry.GetInternalChatTools();
        var options = new ChatCompletionOptions();
        foreach (var tool in chatTools)
            options.Tools.Add(tool);

        var maxIterations = _settings.KnowledgeBase.MaxToolIterations;
        var iterations = 0;
        var seenDomainChunkIds = new HashSet<string>();
        var cumulativeSearchTokens = 0;
        var agenticTokenBudget = _settings.DomainFiles.Retrieval.AgenticTokenBudget;

        while (iterations < maxIterations)
        {
            // Status: thinking
            yield return new StreamEvent { Type = "status", Data = new { state = "thinking" } };

            // Stream LLM call with tools
            var channel = Channel.CreateUnbounded<StreamEvent>();
            StreamingResult? streamResult = null;

            _ = Task.Run(async () =>
            {
                try
                {
                    var client = await _chatFactory.CreateAsync(request.SessionId);
                    streamResult = await _streaming.StreamWithToolsAsync(
                        client, messages, options,
                        evt => { channel.Writer.TryWrite(evt); return ValueTask.CompletedTask; });
                    if (streamResult.Usage != null)
                    {
                        run.TokensUsed      += streamResult.Usage.TotalTokenCount;
                        run.InputTokensUsed  += streamResult.Usage.InputTokenCount;
                        run.OutputTokensUsed += streamResult.Usage.OutputTokenCount;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Tool loop LLM call failed");
                    streamResult = new StreamingResult
                    {
                        Text = "I'm sorry, I couldn't process that. Please try again.",
                        FinishReason = ChatFinishReason.Stop
                    };
                    channel.Writer.TryWrite(new StreamEvent { Type = "token", Data = streamResult.Text });
                }
                finally
                {
                    channel.Writer.Complete();
                }
            });

            // Yield streaming tokens in real-time
            var firstToken = true;
            await foreach (var evt in channel.Reader.ReadAllAsync())
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
                var fullText = streamResult?.Text ?? "";
                var storedText = AppendChunkContext(fullText, lastDomainChunkIds);
                await _conversation.AddMessageAsync(request.ConversationId, "user", request.Message);
                await _conversation.AddMessageAsync(request.ConversationId, "assistant", storedText);
                await _agentRun.CompleteAsync(run, fullText, run.TokensUsed);

                yield return new StreamEvent
                {
                    Type = "done",
                    Data = new { run.AgentRunId, run.TokensUsed, run.ModelId, run.EstimatedCostUsd, diagnostics, tableExports }
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

            foreach (var toolCall in streamResult.ToolCalls!)
            {
                var argsJson = toolCall.FunctionArguments?.ToString() ?? "{}";

                // Track tool call in diagnostics
                diagnostics.ToolsCalled.Add(toolCall.FunctionName);

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
                    "search_docs" => "searching_docs",
                    "start_workflow" => "starting_workflow",
                    _ => "calling_tool"
                };
                yield return new StreamEvent
                {
                    Type = "status",
                    Data = new { state = statusState, detail = toolCall.FunctionName }
                };

                // ── start_workflow: look up by promptId, fall back to semantic search
                if (toolCall.FunctionName == "start_workflow")
                {
                    var toolParams = ParseArgs(argsJson);
                    var promptId = toolParams.GetValueOrDefault("promptId")?.ToString();
                    var description = toolParams.GetValueOrDefault("description")?.ToString();

                    PromptDefinition? wfPrompt = null;

                    // Primary: direct lookup by promptId from candidate list
                    if (!string.IsNullOrEmpty(promptId))
                    {
                        wfPrompt = workflowCandidates
                            .Where(c => c.Id.Equals(promptId, StringComparison.OrdinalIgnoreCase))
                            .Select(c => _semanticSearch.GetPromptById(c.Id))
                            .FirstOrDefault(p => p?.Stages?.Any() == true);
                    }

                    // Fallback: semantic search on description (handles edge cases)
                    if (wfPrompt == null && !string.IsNullOrEmpty(description))
                    {
                        var allowedDomains = await GetAccessibleDomainIdsAsync();
                        var wfCandidates = await _semanticSearch.SearchAsync(description, request.Context.Domain);
                        wfPrompt = wfCandidates
                            .Where(c => c.Prompt.Stages?.Any() == true)
                            .Where(c => allowedDomains.Contains(c.Prompt.Domain))
                            .Select(c => c.Prompt)
                            .FirstOrDefault();
                    }

                    if (wfPrompt != null)
                    {
                        run.MatchedPromptId = wfPrompt.Id;
                        diagnostics.WorkflowsExecuted.Add(wfPrompt.Id);
                        _logger.LogInformation("Starting workflow: {PromptId}", wfPrompt.Id);

                        // If the workflow specifies a model override, update the run's model ID
                        if (!string.IsNullOrEmpty(wfPrompt.ModelId))
                        {
                            run.ModelId = await _chatFactory.GetModelIdAsync(request.SessionId, wfPrompt.ModelId);
                            _logger.LogInformation("Workflow {PromptId} model override: '{ModelId}'", wfPrompt.Id, run.ModelId);
                        }

                        messages.Add(new ToolChatMessage(toolCall.Id,
                            JsonSerializer.Serialize(new { matched = true, promptId = wfPrompt.Id, description = wfPrompt.Description }, _jsonOpts)));

                        yield return new StreamEvent
                        {
                            Type = "tool_result",
                            Data = new { tool = "start_workflow", summary = $"Starting workflow: {wfPrompt.Description}" }
                        };

                        // If the workflow opts in, carry the prior conversation into it so AI stages
                        // can reference earlier discussion (e.g. pre-fill a support ticket).
                        if (wfPrompt.PreserveContext && history.Count > 0)
                        {
                            request.ConversationHistory = history
                                .TakeLast(_settings.KnowledgeBase.MaxContextTurns > 0
                                    ? _settings.KnowledgeBase.MaxContextTurns
                                    : 10)
                                .Select(h => new ConversationMessage { Role = h.Role, Content = h.Content })
                                .ToList();
                            _logger.LogInformation("Workflow {PromptId}: preserving {Count} conversation turns",
                                wfPrompt.Id, request.ConversationHistory.Count);
                        }

                        // Hand off to workflow engine — intercept done event to inject diagnostics
                        await foreach (var evt in _workflowEngine.ExecuteAsync(run, wfPrompt, request, default))
                        {
                            if (evt.Type == "done")
                            {
                                // Preserve resumeRunId from the original done event (set when workflow pauses for user input)
                                string? resumeRunId = null;
                                if (evt.Data != null)
                                {
                                    try
                                    {
                                        var json = JsonSerializer.Serialize(evt.Data, _jsonOpts);
                                        using var doc = JsonDocument.Parse(json);
                                        if (doc.RootElement.TryGetProperty("resumeRunId", out var rid))
                                            resumeRunId = rid.GetString();
                                    }
                                    catch { /* ignore parse errors */ }
                                }

                                yield return new StreamEvent
                                {
                                    Type = "done",
                                    Data = new { run.AgentRunId, resumeRunId, run.TokensUsed, run.ModelId, run.EstimatedCostUsd, diagnostics }
                                };
                            }
                            else
                            {
                                yield return evt;
                            }
                        }
                        yield break;
                    }

                    // No match
                    messages.Add(new ToolChatMessage(toolCall.Id,
                        JsonSerializer.Serialize(new { matched = false, message = "No matching workflow found. Try responding to the user directly." }, _jsonOpts)));
                    yield return new StreamEvent
                    {
                        Type = "tool_result",
                        Data = new { tool = "start_workflow", summary = "No matching workflow found" }
                    };
                    continue;
                }

                // ── Regular tool execution ─────────────────────────────────
                var toolDef = _toolRegistry.GetById(toolCall.FunctionName);
                if (toolDef == null)
                {
                    messages.Add(new ToolChatMessage(toolCall.Id,
                        JsonSerializer.Serialize(new { error = "tool_not_found", message = $"Tool '{toolCall.FunctionName}' not found." })));
                    yield return new StreamEvent
                    {
                        Type = "tool_error",
                        Data = new { tool = toolCall.FunctionName, error = "tool_not_found" }
                    };
                    continue;
                }

                var parsedParams = ParseArgs(argsJson);
                var (success, result, durationMs) = await _toolExecutor.ExecuteAsync(
                    toolDef, parsedParams, request.SessionId, request.ConversationId);

                // Dedup and budget enforcement for search_docs (agentic iteration)
                if (success && IsDomainTool(toolCall.FunctionName))
                {
                    result = DeduplicateSearchResult(result, seenDomainChunkIds, ref cumulativeSearchTokens, agenticTokenBudget);
                }

                messages.Add(new ToolChatMessage(toolCall.Id, result));

                if (success)
                {
                    var summary = result.Length > 500 ? result[..500] + "..." : result;
                    yield return new StreamEvent
                    {
                        Type = "tool_result",
                        Data = new { tool = toolCall.FunctionName, summary }
                    };

                    // Track empty search results for diagnostics
                    if (IsSearchTool(toolCall.FunctionName) && IsEmptySearchResult(result))
                        diagnostics.EmptySearchResults++;

                    // Track chunk IDs from domain tools for conversation context
                    if (IsDomainTool(toolCall.FunctionName))
                    {
                        ExtractChunkIds(result, lastDomainChunkIds);
                        diagnostics.SearchIterations++;
                    }

                    // Record a reproducible CSV export for successful kb_query calls.
                    // The export is bound to the exact SQL, so the CSV always reproduces
                    // the results regardless of what the assistant renders in-chat.
                    if (toolCall.FunctionName == "kb_query")
                    {
                        await TryRecordTableExportAsync(argsJson, result, tableExports);

                        // Surface truncation to the user with a deterministic warning
                        // event — independent of whether the model mentions it. The full
                        // result set is still available via the CSV export above.
                        var truncEvt = BuildTruncationWarning(result);
                        if (truncEvt != null)
                            yield return truncEvt;
                    }
                }
                else
                {
                    yield return new StreamEvent
                    {
                        Type = "tool_error",
                        Data = new { tool = toolCall.FunctionName, error = "execution_failed", message = result }
                    };
                }
            }

            iterations++;
        }

        // Max iterations reached — force a final text response
        messages.Add(new SystemChatMessage("Max tool calls reached. Summarize what you have so far and respond to the user."));
        yield return new StreamEvent { Type = "status", Data = new { state = "thinking" } };

        var finalChannel = Channel.CreateUnbounded<StreamEvent>();
        var finalText = "";

        _ = Task.Run(async () =>
        {
            try
            {
                var client = await _chatFactory.CreateAsync(request.SessionId);
                var (text, usage) = await _streaming.StreamTextAsync(
                    client, messages,
                    evt => { finalChannel.Writer.TryWrite(evt); return ValueTask.CompletedTask; });
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
                _logger.LogError(ex, "Final streaming call failed");
                finalText = "I'm sorry, I couldn't complete the analysis. Please try again.";
                finalChannel.Writer.TryWrite(new StreamEvent { Type = "token", Data = finalText });
            }
            finally
            {
                finalChannel.Writer.Complete();
            }
        });

        await foreach (var evt in finalChannel.Reader.ReadAllAsync())
        {
            yield return evt;
        }

        await _conversation.AddMessageAsync(request.ConversationId, "user", request.Message);
        await _conversation.AddMessageAsync(request.ConversationId, "assistant", AppendChunkContext(finalText, lastDomainChunkIds));
        await _agentRun.CompleteAsync(run, finalText, run.TokensUsed);

        yield return new StreamEvent
        {
            Type = "done",
            Data = new { run.AgentRunId, run.TokensUsed, run.ModelId, run.EstimatedCostUsd, diagnostics, tableExports }
        };
    }

    // ── System prompt builder ─────────────────────────────────────────────────

    private async Task<string> BuildSystemPromptAsync(
        ChatRequest request,
        IReadOnlyList<ConversationMessage> history,
        List<WorkflowCandidate> workflowCandidates,
        RunDiagnostics diagnostics)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are AVA, a helpful enterprise assistant.");
        sb.AppendLine("Answer the user's questions using conversation history and available tools.");
        sb.AppendLine("Be concise and accurate. Format results using markdown when helpful.");

        // Domain context
        var domain = await _domains.GetByIdAsync(request.Context.Domain);
        if (domain != null && history.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine($"This is the start of a new conversation. The user is in the {domain.Name} domain.");
        }

        // Domain-level agentic instruction — injected on EVERY message in this domain
        // (not just new conversations) so it consistently shapes AVA's behavior.
        if (domain != null && !string.IsNullOrWhiteSpace(domain.AgenticInstruction))
        {
            sb.AppendLine();
            sb.AppendLine("Domain instructions:");
            sb.AppendLine(domain.AgenticInstruction.Trim());
        }

        // KB summary
        var kbSummary = await _kbService.BuildKBSummaryAsync(
            request.Context.Domain, request.SessionId, request.ConversationId, domain?.KbPromptMode);
        if (!string.IsNullOrEmpty(kbSummary))
        {
            sb.AppendLine();
            sb.Append(kbSummary);
        }

        // Domain context files — manifest + routed retrieval
        var domainFileManifest = await _domainFileService.BuildManifestAsync(request.Context.Domain);
        if (!string.IsNullOrEmpty(domainFileManifest))
        {
            sb.AppendLine();
            sb.Append(domainFileManifest);

            // Route the query to determine retrieval strategy
            var conversationContext = history
                .Where(h => h.Role == "user")
                .Select(h => h.Content)
                .TakeLast(3)
                .ToList();

            var route = await _queryRouter.RouteAsync(request.Message, conversationContext);
            _logger.LogInformation("QueryRouter: strategy={Strategy} confidence={Confidence:F2}",
                route.Strategy, route.Confidence);

            // Populate diagnostics with router info
            diagnostics.RetrievalStrategy = route.Strategy.ToString().ToLower();
            diagnostics.RouterConfidence = route.Confidence;
            diagnostics.RouterUsedLlm = route.UsedLlm;

            List<DomainFileChunk> relevantChunks;
            switch (route.Strategy)
            {
                case RetrievalStrategy.None:
                    // Query doesn't need domain file search (workflow, greeting, etc.)
                    relevantChunks = new List<DomainFileChunk>();
                    break;

                case RetrievalStrategy.Fast:
                    // Use first rewritten query if available, otherwise original message
                    var fastQuery = route.SubQueries?.FirstOrDefault() ?? request.Message;
                    relevantChunks = await _domainFileService.ExecuteFastRetrievalAsync(
                        request.Context.Domain, fastQuery, request.Message);
                    break;

                case RetrievalStrategy.Expanded:
                    relevantChunks = await _domainFileService.ExecuteExpandedRetrievalAsync(
                        request.Context.Domain, request.Message, route.SubQueries);
                    break;

                case RetrievalStrategy.Agentic:
                    // For agentic mode, do a fast initial retrieval and let the tool loop handle the rest
                    var agenticQuery = route.SubQueries?.FirstOrDefault() ?? request.Message;
                    relevantChunks = await _domainFileService.ExecuteFastRetrievalAsync(
                        request.Context.Domain, agenticQuery);
                    break;

                default:
                    relevantChunks = await _domainFileService.ExecuteFastRetrievalAsync(
                        request.Context.Domain, request.Message);
                    break;
            }

            if (relevantChunks.Count > 0)
            {
                diagnostics.SearchIterations = 1; // count initial retrieval
                sb.AppendLine();
                sb.AppendLine($"Retrieved domain context ({route.Strategy} strategy, {relevantChunks.Count} sections):");
                var totalTokens = 0;
                foreach (var chunk in relevantChunks)
                {
                    var chunkLabel = !string.IsNullOrEmpty(chunk.Title) ? chunk.Title : chunk.Id;
                    var chunkText = $"[{chunk.Source}, {chunkLabel}]\n{chunk.Content}";
                    totalTokens += chunkText.Length / 4; // rough token estimate
                    if (totalTokens > _settings.DomainFiles.MaxAutoRetrieveTokens) break;
                    sb.AppendLine(chunkText);
                    if (!diagnostics.DomainFilesUsed.Contains(chunk.Source))
                        diagnostics.DomainFilesUsed.Add(chunk.Source);
                }
                sb.AppendLine();

                if (route.Strategy == RetrievalStrategy.Agentic)
                {
                    sb.AppendLine("ITERATIVE RETRIEVAL MODE — This is a complex question requiring multiple searches.");
                    sb.AppendLine("PROCESS:");
                    sb.AppendLine("1. Review the initial context above");
                    sb.AppendLine("2. Identify what information is STILL MISSING to fully answer the question");
                    sb.AppendLine("3. Call search_docs with a DIFFERENT query targeting the gap (use short 2-5 word noun-phrase queries)");
                    sb.AppendLine("4. Repeat steps 2-3 until you have all needed information");
                    sb.AppendLine("5. Synthesize a complete answer from ALL retrieved context");
                    sb.AppendLine();
                    sb.AppendLine("RULES:");
                    sb.AppendLine("- Each search_docs call MUST use different keywords than previous calls");
                    sb.AppendLine("- STOP searching when: you have enough to fully answer, OR search returns no new useful results");
                    sb.AppendLine("- Do NOT answer until you've gathered sufficient context for ALL parts of the question");
                }
                else
                {
                    sb.AppendLine("If this context doesn't fully answer the question, use the search_docs tool to retrieve more information.");
                }
                sb.AppendLine();
                sb.AppendLine("IMPORTANT: When answering from domain file context, always include a brief reference at the end of your response citing the source document and section heading. Use ONLY the document name and section title — do NOT include chunk IDs, parent IDs, or file paths. Format: \"Reference: [Document Name, Section Title]\" (e.g., \"Reference: [SMA System Specification, Section 7.2 - Location of Oracle Data Tables]\" or \"Reference: [SMA Manual, Source Data Verification]\").");
                sb.AppendLine();
            }
        }

        // Workflow candidates — the key innovation: LLM sees what's available
        if (workflowCandidates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Available workflows (call start_workflow tool with the promptId to trigger):");
            foreach (var c in workflowCandidates)
                sb.AppendLine($"  - promptId=\"{c.Id}\": {c.Description}");
            sb.AppendLine();
            sb.AppendLine("Workflow and tool usage rules:");
            sb.AppendLine("  - When the user's request clearly matches a workflow, call start_workflow immediately — do NOT ask for confirmation or clarification first.");
            sb.AppendLine("  - Use kb_query/kb_search ONLY for follow-up questions about data already loaded in a KB (e.g., filtering, aggregating, or drilling into the same dataset).");
            sb.AppendLine("  - If the user asks about a different topic, system, or dataset than what is in the loaded KBs, use start_workflow — do not query unrelated KB data.");
            sb.AppendLine("  - If no workflow matches and no KB is relevant, respond conversationally.");
        }

        // Retry / broadening guidance
        sb.AppendLine();
        sb.AppendLine("Search retry rules:");
        sb.AppendLine("  - If a search or query returns no results, rephrase with synonyms or broader terms before giving up.");
        sb.AppendLine("  - Try different search terms if the first attempt finds nothing relevant.");
        sb.AppendLine("  - After exhausting search options, clearly state what you searched and that no relevant information was found — do not guess or fabricate an answer.");

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build an effective search query for domain file retrieval.
    /// For very short follow-up messages that lack substance (e.g., "next section",
    /// "tell me more"), augments with context from the previous turn.
    /// Messages that contain clear topical keywords are used as-is regardless of length.
    /// </summary>
    private static string BuildSearchQuery(string currentMessage, IReadOnlyList<ConversationMessage> history)
    {
        // If no history, always use message as-is
        if (history.Count == 0)
            return currentMessage;

        // If message has clear topical substance (contains specific keywords/nouns),
        // use it as-is even if it's short. Only augment truly vague follow-ups.
        // Heuristic: messages with 4+ words that aren't all common/short words are standalone.
        var words = currentMessage.Split(new[] { ' ', '?', '!', ',', '.' },
            StringSplitOptions.RemoveEmptyEntries);
        var substantiveWords = words.Where(w => w.Length > 3).ToArray();
        if (substantiveWords.Length >= 3 || currentMessage.Length > 60)
            return currentMessage;

        // For truly short/vague follow-ups (e.g., "next section", "continue", "tell me more"),
        // add minimal context from the previous user message only (not the assistant response,
        // which often contains verbose "not found" explanations that pollute the search).
        var contextParts = new List<string>();

        string? lastUserContent = null;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == "user")
            {
                lastUserContent = history[i].Content;
                break;
            }
        }

        if (lastUserContent != null)
        {
            contextParts.Add(lastUserContent.Length > 100 ? lastUserContent[..100] : lastUserContent);
        }

        contextParts.Add(currentMessage);
        return string.Join(" ", contextParts);
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

    private static readonly HashSet<string> _searchToolIds = new(StringComparer.OrdinalIgnoreCase)
        { "kb_search", "search_docs" };

    private static readonly HashSet<string> _domainToolIds = new(StringComparer.OrdinalIgnoreCase)
        { "search_docs" };

    private static bool IsSearchTool(string toolName) => _searchToolIds.Contains(toolName);
    private static bool IsDomainTool(string toolName) => _domainToolIds.Contains(toolName);

    /// <summary>
    /// Builds a deterministic truncation warning event from a kb_query result, or null
    /// when the result was not truncated. Lets the UI surface "showing N of M rows" and
    /// point the user at the full CSV export regardless of what the model narrates.
    /// Fully guarded — any parse failure returns null and never disrupts the turn.
    /// </summary>
    private static StreamEvent? BuildTruncationWarning(string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (!root.TryGetProperty("truncated", out var tEl) || tEl.ValueKind != JsonValueKind.True)
                return null;

            var shown = root.TryGetProperty("rowCount", out var rcEl) && rcEl.ValueKind == JsonValueKind.Number
                ? rcEl.GetInt32()
                : (int?)null;
            long? total = root.TryGetProperty("totalRowCount", out var totEl) && totEl.ValueKind == JsonValueKind.Number
                ? totEl.GetInt64()
                : null;

            var message = total.HasValue
                ? $"Showing first {shown ?? 0} of {total.Value} rows. Download the CSV for the full result."
                : $"Showing first {shown ?? 0} rows (result was capped). Download the CSV for the full result.";

            return new StreamEvent
            {
                Type = "warning",
                Data = new { tool = "kb_query", truncated = true, rowCount = shown, totalRowCount = total, message }
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Records a reproducible CSV export from a successful kb_query. Parses the tool args
    /// (datasetIds + sql) and result (columns, rowCount, truncated), captures a per-dataset
    /// version marker, stores the SQL in the export cache, and appends a <see cref="TableExport"/>
    /// to the turn's list. Skips error results, empty results, and duplicate sql+datasetIds.
    /// Fully guarded — any parse failure is swallowed so it can never break the tool loop or
    /// alter the response.
    /// </summary>
    private async Task TryRecordTableExportAsync(string argsJson, string result, List<TableExport> exports)
    {
        try
        {
            using var argsDoc = JsonDocument.Parse(argsJson);
            using var resultDoc = JsonDocument.Parse(result);
            var resRoot = resultDoc.RootElement;

            // Skip failed or empty queries — nothing to export.
            if (resRoot.TryGetProperty("error", out _)) return;
            if (!resRoot.TryGetProperty("rowCount", out var rowCountEl)) return;
            var rowCount = rowCountEl.GetInt32();

            var truncated = resRoot.TryGetProperty("truncated", out var tEl)
                && tEl.ValueKind == JsonValueKind.True;

            // Only offer a download for results worth taking away as a spreadsheet:
            // multi-row results (any shape) or results large enough to have been capped.
            // This excludes scalars/counts (1×1) and single-row lookups, which produced
            // noisy download buttons for intermediate queries the model runs mid-turn.
            if (rowCount <= 1 && !truncated) return;

            var argsRoot = argsDoc.RootElement;
            if (!argsRoot.TryGetProperty("sql", out var sqlEl) || sqlEl.ValueKind != JsonValueKind.String)
                return;
            var rawSql = sqlEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(rawSql)) return;

            // Strip any trailing LIMIT/OFFSET so the CSV export returns the full result set,
            // not just the (often defensively) capped rows shown in the in-chat table.
            var sql = KbExportCacheService.StripTrailingLimitOffset(rawSql);
            var limitRemoved = !string.Equals(sql, rawSql.TrimEnd().TrimEnd(';').TrimEnd(), StringComparison.Ordinal);

            var datasetIds = new List<string>();
            if (argsRoot.TryGetProperty("datasetIds", out var idsEl))
            {
                if (idsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var id in idsEl.EnumerateArray())
                        if (id.ValueKind == JsonValueKind.String && id.GetString() is { } s)
                            datasetIds.Add(s);
                }
                else if (idsEl.ValueKind == JsonValueKind.String && idsEl.GetString() is { } raw)
                {
                    // Match ToolExecutorService.ParseStringArray: the model sometimes
                    // double-serializes the array (e.g. "[\"id1\"]") or passes a lone id.
                    var trimmed = raw.Trim();
                    if (trimmed.StartsWith("["))
                    {
                        try
                        {
                            var inner = JsonSerializer.Deserialize<string[]>(trimmed);
                            if (inner is { Length: > 0 }) datasetIds.AddRange(inner);
                        }
                        catch { /* fall through to single-id below */ }
                    }
                    if (datasetIds.Count == 0 && !string.IsNullOrWhiteSpace(raw))
                        datasetIds.Add(raw);
                }
            }
            if (datasetIds.Count == 0) return;

            var idsArray = datasetIds.ToArray();

            // Dedupe identical sql + datasetIds within the same turn.
            if (exports.Any(e => e.Sql == sql && e.DatasetIds.SequenceEqual(idsArray)))
                return;

            var columns = new List<string>();
            if (resRoot.TryGetProperty("columns", out var colsEl) && colsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in colsEl.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && c.GetString() is { } cn)
                        columns.Add(cn);
            }

            // Capture a version marker per dataset so the download endpoint can detect
            // whether a KB was refreshed/replaced between query time and download time.
            var datasetVersions = new Dictionary<string, string>();
            foreach (var id in idsArray)
            {
                var kb = await _kbService.GetByIdAsync(id);
                if (kb == null) return; // dataset vanished mid-turn — nothing reproducible
                datasetVersions[id] = KbExportCacheService.VersionMarker(kb);
            }

            var queryId = await _exportCache.StoreAsync(idsArray, sql, datasetVersions);

            exports.Add(new TableExport
            {
                QueryId      = queryId,
                Sql          = sql,
                DatasetIds   = idsArray,
                Columns      = columns,
                RowCount     = rowCount,
                Truncated    = truncated,
                LimitRemoved = limitRemoved,
                DownloadUrl  = $"/api/kb/export/{queryId}",
            });
        }
        catch
        {
            // Export recording is best-effort — never let it disrupt the turn.
        }
    }

    private static bool IsEmptySearchResult(string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            // Check for "chunks": [] pattern used by domain tools
            if (doc.RootElement.TryGetProperty("chunks", out var chunks)
                && chunks.ValueKind == JsonValueKind.Array
                && chunks.GetArrayLength() == 0)
                return true;
            // Check for "rowCount": 0 pattern used by kb_query
            if (doc.RootElement.TryGetProperty("rowCount", out var rowCount)
                && rowCount.GetInt32() == 0)
                return true;
        }
        catch { }
        return false;
    }

    /// <summary>Extract chunk IDs from a domain tool result JSON into the tracker list.</summary>
    private static void ExtractChunkIds(string result, List<string> tracker)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("chunks", out var chunks)
                && chunks.ValueKind == JsonValueKind.Array)
            {
                tracker.Clear();
                foreach (var chunk in chunks.EnumerateArray())
                {
                    if (chunk.TryGetProperty("id", out var id) && id.GetString() is { } idStr)
                        tracker.Add(idStr);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Appends a compact chunk navigation context to the stored assistant message.
    /// This allows subsequent turns to reference the last chunk IDs shown to the user.
    /// </summary>
    private static string AppendChunkContext(string text, List<string> chunkIds)
    {
        if (chunkIds.Count == 0) return text;
        var lastChunk = chunkIds[^1];
        var firstChunk = chunkIds[0];
        return $"{text}\n\n[Navigation context: chunks {firstChunk} through {lastChunk}]";
    }

    /// <summary>
    /// Removes already-seen chunks from a search_docs result and enforces the token budget.
    /// Tracks chunk IDs in seenChunkIds and cumulative tokens for iterative dedup.
    /// </summary>
    private static string DeduplicateSearchResult(string result, HashSet<string> seenChunkIds, ref int cumulativeTokens, int tokenBudget)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            if (!doc.RootElement.TryGetProperty("chunks", out var chunks) || chunks.ValueKind != JsonValueKind.Array)
                return result;

            // Budget exhausted — tell the LLM to stop searching
            if (cumulativeTokens >= tokenBudget)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "Token budget exhausted. Synthesize your answer from the context already retrieved.",
                    chunks = Array.Empty<object>()
                });
            }

            var filteredChunks = new List<JsonElement>();
            foreach (var chunk in chunks.EnumerateArray())
            {
                var id = chunk.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id) || seenChunkIds.Contains(id))
                    continue;

                // Check token budget before adding
                var content = chunk.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                var chunkTokens = content.Length / 4;
                if (cumulativeTokens + chunkTokens > tokenBudget)
                    break;

                seenChunkIds.Add(id);
                cumulativeTokens += chunkTokens;
                filteredChunks.Add(chunk);
            }

            if (filteredChunks.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = "No new results found (all chunks already retrieved). Try a different query or synthesize your answer.",
                    chunks = Array.Empty<object>()
                });

            return JsonSerializer.Serialize(new
            {
                message = $"Found {filteredChunks.Count} new sections ({cumulativeTokens}/{tokenBudget} token budget used).",
                chunks = filteredChunks.Select(ch => JsonSerializer.Deserialize<object>(ch.GetRawText()))
            });
        }
        catch
        {
            return result;
        }
    }
}

/// <summary>Lightweight DTO for workflow candidates shown to the LLM.</summary>
public class WorkflowCandidate
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public float Score { get; set; }
}

/// <summary>Diagnostics payload sent with the done event for debugging visibility.</summary>
public class RunDiagnostics
{
    public List<string> ToolsCalled { get; set; } = new();
    public List<string> WorkflowsExecuted { get; set; } = new();
    public List<string> DomainFilesUsed { get; set; } = new();
    public int EmptySearchResults { get; set; }
    public string? RetrievalStrategy { get; set; }
    public bool RouterUsedLlm { get; set; }
    public double RouterConfidence { get; set; }
    public int SearchIterations { get; set; }
}
