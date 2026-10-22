using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Ava.Api.Config;
using Ava.Api.Models;
using OpenAI.Chat;

namespace Ava.Api.Services;

/// <summary>
/// Orchestrates the AI-assisted workflow creation flow.
/// Runs an agentic loop where the LLM uses structured "creator tools" to build
/// a workflow definition incrementally. Tool calls are emitted as creator_action
/// events that the frontend renders as live-updating cards.
/// </summary>
public class WorkflowCreatorService
{
    private readonly IChatClientFactory _chatFactory;
    private readonly StreamingChatHelper _streaming;
    private readonly IPromptRepository _prompts;
    private readonly IToolRepository _tools;
    private readonly IDomainRepository _domains;
    private readonly SemanticSearchService _semanticSearch;
    private readonly ILogger<WorkflowCreatorService> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Regex TokenRegex = new(@"\{\{\s*([^}]+?)\s*\}\}", RegexOptions.Compiled);

    public WorkflowCreatorService(
        IChatClientFactory chatFactory,
        StreamingChatHelper streaming,
        IPromptRepository prompts,
        IToolRepository tools,
        IDomainRepository domains,
        SemanticSearchService semanticSearch,
        ILogger<WorkflowCreatorService> logger)
    {
        _chatFactory = chatFactory;
        _streaming = streaming;
        _prompts = prompts;
        _tools = tools;
        _domains = domains;
        _semanticSearch = semanticSearch;
        _logger = logger;
    }

    /// <summary>
    /// Runs the creator agent loop, streaming both text tokens and creator_action events.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> CreateWorkflowAsync(
        CreateWorkflowRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _logger.LogInformation("Workflow creator session for {User}: {Msg}",
            request.UserId, request.Message);

        // 1. Build system prompt with workflow catalog + tool catalog
        var editMode = string.Equals(request.Mode, "edit", StringComparison.OrdinalIgnoreCase);
        var systemPrompt = await BuildSystemPromptAsync(editMode, request.EditingWorkflowId);

        // 2. Build conversation messages
        var messages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };

        // Include conversation history
        if (request.ConversationHistory?.Count > 0)
        {
            foreach (var msg in request.ConversationHistory)
            {
                if (msg.Role == "user")
                    messages.Add(new UserChatMessage(msg.Content));
                else if (msg.Role == "assistant")
                    messages.Add(new AssistantChatMessage(msg.Content));
            }
        }

        // Include current draft state if present
        if (request.CurrentDraft != null)
        {
            var draftJson = JsonSerializer.Serialize(request.CurrentDraft, _jsonOpts);
            messages.Add(new UserChatMessage($"[CURRENT WORKFLOW DRAFT]\n{draftJson}"));
        }

        // Include edit buffer
        if (request.EditBuffer?.Count > 0)
        {
            var edits = string.Join("\n", request.EditBuffer.Select(e => $"- {e}"));
            messages.Add(new UserChatMessage($"[USER EDITS SINCE LAST MESSAGE]\n{edits}"));
        }

        // Add current user message
        messages.Add(new UserChatMessage(request.Message));

        // 3. Set up tools
        var chatTools = GetCreatorTools();
        var options = new ChatCompletionOptions();
        foreach (var tool in chatTools)
            options.Tools.Add(tool);

        // 4. Run agentic loop (max 10 iterations)
        const int maxIterations = 10;
        int iterations = 0;
        var emittedAssistantText = false;
        var emittedCreatorAction = false;
        var creatorActionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (iterations < maxIterations)
        {
            ct.ThrowIfCancellationRequested();

            // Stream LLM call
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
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Creator agent LLM call failed at iteration {Iter}", iterations);
                    streamResult = new StreamingResult
                    {
                        Text = $"I encountered an error: {ex.Message}. Please try again.",
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
                ObserveCreatorStreamEvent(evt, ref emittedAssistantText, ref emittedCreatorAction, creatorActionCounts);
                yield return evt;
            }

            // If text response (no tool calls) → agent is done for this turn
            if (streamResult == null || !streamResult.IsToolCall)
            {
                break;
            }

            // Process tool calls — each emits a creator_action event
            var assistantMsg = new AssistantChatMessage(streamResult.ToolCalls);
            if (!string.IsNullOrEmpty(streamResult.Text))
            {
                assistantMsg = new AssistantChatMessage(streamResult.ToolCalls)
                {
                    Content = { ChatMessageContentPart.CreateTextPart(streamResult.Text) }
                };
            }
            messages.Add(assistantMsg);

            foreach (var toolCall in streamResult.ToolCalls!)
            {
                ct.ThrowIfCancellationRequested();

                var result = await ExecuteCreatorToolAsync(toolCall.FunctionName, toolCall.FunctionArguments?.ToString() ?? "{}", request.CurrentDraft);

                // Emit the creator_action event to the frontend
                var actionEvent = new StreamEvent
                {
                    Type = "creator_action",
                    Data = new
                    {
                        action = toolCall.FunctionName,
                        payload = result.Payload
                    }
                };
                ObserveCreatorStreamEvent(actionEvent, ref emittedAssistantText, ref emittedCreatorAction, creatorActionCounts);
                yield return actionEvent;

                // Add tool result back to conversation
                messages.Add(new ToolChatMessage(toolCall.Id, result.ResultText));
            }

            iterations++;
        }

        if (emittedCreatorAction && !emittedAssistantText)
        {
            yield return new StreamEvent { Type = "token", Data = BuildCreatorActionSummary(creatorActionCounts) };
        }

        yield return new StreamEvent { Type = "done", Data = new { iterations } };
    }

    private static void ObserveCreatorStreamEvent(
        StreamEvent evt,
        ref bool emittedAssistantText,
        ref bool emittedCreatorAction,
        Dictionary<string, int> creatorActionCounts)
    {
        if (string.Equals(evt.Type, "token", StringComparison.OrdinalIgnoreCase) &&
            evt.Data is not null &&
            !string.IsNullOrWhiteSpace(evt.Data.ToString()))
        {
            emittedAssistantText = true;
        }

        if (string.Equals(evt.Type, "creator_action", StringComparison.OrdinalIgnoreCase))
        {
            emittedCreatorAction = true;
            var action = ExtractCreatorActionName(evt.Data);
            if (!string.IsNullOrWhiteSpace(action))
            {
                creatorActionCounts[action] = creatorActionCounts.TryGetValue(action, out var count) ? count + 1 : 1;
            }
        }
    }

    private static string? ExtractCreatorActionName(object? data)
    {
        if (data is null)
            return null;

        var json = JsonSerializer.SerializeToElement(data, _jsonOpts);
        return json.TryGetProperty("action", out var action) ? action.GetString() : null;
    }

    private static string BuildCreatorActionSummary(IReadOnlyDictionary<string, int> actionCounts)
    {
        var parts = new List<string>();

        AddCountPart("add_stage", "added", "stage");
        AddCountPart("update_stage", "updated", "stage");
        AddCountPart("remove_stage", "removed", "stage");
        AddCountPart("reorder_stages", "reordered", "stage set");
        AddSimplePart("set_metadata", "refreshed metadata");
        AddSimplePart("set_data_flow", "updated data flow");
        AddSimplePart("clone_workflow", "loaded workflow details");
        AddSimplePart("search_workflows", "reviewed similar workflows");
        AddSimplePart("get_workflow_details", "reviewed workflow details");
        AddSimplePart("list_available_tools", "reviewed available tools");
        AddSimplePart("register_tool", "prepared tool registration");
        AddSimplePart("validate_draft", "validated the draft");

        if (parts.Count == 0)
            return "Applied your workflow changes.";

        return $"Applied your changes ({string.Join(", ", parts)}).";

        void AddCountPart(string action, string verb, string noun)
        {
            if (!actionCounts.TryGetValue(action, out var count) || count <= 0)
                return;

            var plural = count == 1 ? noun : noun + "s";
            parts.Add($"{verb} {count} {plural}");
        }

        void AddSimplePart(string action, string text)
        {
            if (actionCounts.TryGetValue(action, out var count) && count > 0)
                parts.Add(text);
        }
    }

    /// <summary>
    /// Build the system prompt with Ava Engine Reference + catalogs.
    /// </summary>
    private async Task<string> BuildSystemPromptAsync(bool editMode = false, string? editingWorkflowId = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CreatorAgentPrompt.SystemPrompt);

        if (editMode)
        {
            sb.AppendLine(CreatorAgentPrompt.EditModeAddendum);
            if (!string.IsNullOrWhiteSpace(editingWorkflowId))
                sb.AppendLine($"\nYou are editing the existing workflow **{editingWorkflowId}**. Its current definition is provided as [CURRENT WORKFLOW DRAFT]. Apply the user's requested changes to it. Do NOT change its `id`.");
        }

        // Append workflow catalog (IDs + descriptions)
        var allPrompts = await _prompts.GetAllAsync();
        sb.AppendLine("\n## Existing Workflow Catalog");
        sb.AppendLine("Use this to find similar workflows or reference existing patterns.\n");
        foreach (var p in allPrompts)
        {
            var stageCount = p.Stages?.Count ?? 0;
            sb.AppendLine($"- **{p.Id}** ({p.Domain}): {p.Description} [{stageCount} stages]");
        }

        // Append tool catalog (IDs + descriptions)
        var allTools = await _tools.GetAllAsync();
        sb.AppendLine("\n## Available Tools");
        sb.AppendLine("Reference these in tool stages. If a needed tool doesn't exist, use register_tool.\n");
        foreach (var t in allTools)
        {
            var paramNames = string.Join(", ", t.Parameters.Select(p => p.Name));
            sb.AppendLine($"- **{t.Id}** ({t.HttpMethod} {t.Route}): {t.Description ?? "No description"} — params: [{paramNames}]");
        }

        // Append domain catalog
        var allDomains = await _domains.GetAllAsync();
        sb.AppendLine("\n## Available Domains");
        foreach (var d in allDomains)
        {
            sb.AppendLine($"- {d.Id}: {d.Name}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Execute a creator tool and return the result + action payload for the frontend.
    /// </summary>
    private async Task<CreatorToolResult> ExecuteCreatorToolAsync(string toolName, string argsJson, WorkflowDraft? currentDraft = null)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argsJson);

            return toolName switch
            {
                "set_metadata" => HandleSetMetadata(args),
                "add_stage" => HandleAddStage(args, currentDraft),
                "update_stage" => HandleUpdateStage(args),
                "remove_stage" => HandleRemoveStage(args),
                "reorder_stages" => HandleReorderStages(args),
                "search_workflows" => await HandleSearchWorkflows(args),
                "get_workflow_details" => await HandleGetWorkflowDetails(args),
                "clone_workflow" => await HandleCloneWorkflow(args),
                "set_data_flow" => HandleSetDataFlow(args),
                "validate_draft" => HandleValidateDraft(args),
                "register_tool" => HandleRegisterTool(args),
                "list_available_tools" => await HandleListAvailableTools(),
                _ => new CreatorToolResult("Unknown tool", new { error = "unknown_tool", name = toolName })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creator tool {Tool} failed", toolName);
            return new CreatorToolResult(
                $"Error executing {toolName}: {ex.Message}",
                new { error = "execution_failed", tool = toolName });
        }
    }

    // ── Tool Handlers ──────────────────────────────────────────────────────────

    private CreatorToolResult HandleSetMetadata(JsonElement args)
    {
        // Pass the metadata through to the frontend — it applies it to the draft
        var payload = JsonSerializer.Deserialize<object>(args.GetRawText(), _jsonOpts);
        return new CreatorToolResult(
            "Metadata updated successfully.",
            payload ?? new { });
    }

    private CreatorToolResult HandleAddStage(JsonElement args, WorkflowDraft? currentDraft)
    {
        var payload = JsonSerializer.Deserialize<object>(args.GetRawText(), _jsonOpts);
        var stageId = args.TryGetProperty("stageId", out var sid) ? sid.GetString() : "unknown";
        var stageType = args.TryGetProperty("type", out var st) ? st.GetString() : "unknown";

        // Dedup guard: if this stageId already exists in the current draft, instruct the LLM to use update_stage
        if (currentDraft?.Stages?.Any(s => s.StageId == stageId) == true)
        {
            return new CreatorToolResult(
                $"Stage '{stageId}' already exists in the draft. Use update_stage to modify it instead of adding a duplicate.",
                payload ?? new { });
        }

        return new CreatorToolResult(
            $"Added stage '{stageId}' (type: {stageType}).",
            payload ?? new { });
    }

    private CreatorToolResult HandleUpdateStage(JsonElement args)
    {
        var payload = JsonSerializer.Deserialize<object>(args.GetRawText(), _jsonOpts);
        var stageId = args.TryGetProperty("stageId", out var sid) ? sid.GetString() : "unknown";
        return new CreatorToolResult(
            $"Updated stage '{stageId}'.",
            payload ?? new { });
    }

    private CreatorToolResult HandleRemoveStage(JsonElement args)
    {
        var stageId = args.TryGetProperty("stageId", out var sid) ? sid.GetString() : "unknown";
        return new CreatorToolResult(
            $"Removed stage '{stageId}'.",
            new { stageId });
    }

    private CreatorToolResult HandleReorderStages(JsonElement args)
    {
        var payload = JsonSerializer.Deserialize<object>(args.GetRawText(), _jsonOpts);
        return new CreatorToolResult(
            "Stages reordered.",
            payload ?? new { });
    }

    private async Task<CreatorToolResult> HandleSearchWorkflows(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";

        var results = await _semanticSearch.SearchAsync(query, null);
        var matches = results.Take(5).Select(r => new
        {
            id = r.Prompt.Id,
            domain = r.Prompt.Domain,
            description = r.Prompt.Description,
            score = Math.Round(r.Score, 2),
            stageCount = r.Prompt.Stages?.Count ?? 0,
            stages = r.Prompt.Stages?.Select(s => new { s.StageId, s.Type }).ToList()
        }).ToList();

        var resultText = matches.Count > 0
            ? $"Found {matches.Count} similar workflows:\n" +
              string.Join("\n", matches.Select(m => $"- {m.id} ({m.domain}): {m.description} — {m.score * 100:F0}% match"))
            : "No similar workflows found.";

        return new CreatorToolResult(resultText, new { matches });
    }

    private async Task<CreatorToolResult> HandleGetWorkflowDetails(JsonElement args)
    {
        var workflowId = args.TryGetProperty("workflowId", out var wid) ? wid.GetString() ?? "" : "";
        var prompt = await _prompts.GetByIdAsync(workflowId);

        if (prompt == null)
            return new CreatorToolResult($"Workflow '{workflowId}' not found.", new { error = "not_found" });

        var details = new
        {
            prompt.Id,
            prompt.Domain,
            prompt.Description,
            prompt.ContextTags,
            prompt.SystemPrompt,
            prompt.PiiCheck,
            stages = prompt.Stages?.Select(ProjectStage).ToList()
        };

        return new CreatorToolResult(
            $"Retrieved details for '{workflowId}'.",
            details);
    }

    /// <summary>
    /// Project a WorkflowStage into the creator-action payload shape, recursing
    /// foreach sub-stages so loop structure survives clone/details round-trips.
    /// </summary>
    private static object ProjectStage(WorkflowStage s) => new
    {
        s.StageId,
        s.Type,
        s.Prompt,
        s.ToolId,
        s.Required,
        s.Parameters,
        s.OutputMap,
        prePrompt = s.PrePrompt != null ? new { s.PrePrompt.Prompt, s.PrePrompt.AllowedToolIds } : null,
        s.SelectPrompt,
        s.OptionsFrom,
        s.TemplateRef,
        s.ItemsFrom,
        s.ItemAlias,
        s.ConfirmEach,
        stages = s.Stages?.Select(ProjectStage).ToList()
    };

    private async Task<CreatorToolResult> HandleCloneWorkflow(JsonElement args)
    {
        var sourceId = args.TryGetProperty("sourceId", out var sid) ? sid.GetString() ?? "" : "";
        var prompt = await _prompts.GetByIdAsync(sourceId);

        if (prompt == null)
            return new CreatorToolResult($"Workflow '{sourceId}' not found.", new { error = "not_found" });

        // Return the full workflow as a clone payload — frontend populates the draft
        var clonePayload = new
        {
            metadata = new
            {
                id = prompt.Id + "-copy",
                domain = prompt.Domain,
                description = prompt.Description,
                contextTags = prompt.ContextTags,
                systemPrompt = prompt.SystemPrompt,
                piiCheck = prompt.PiiCheck
            },
            stages = prompt.Stages?.Select(ProjectStage).ToList()
        };

        return new CreatorToolResult(
            $"Cloned workflow '{sourceId}'. All stages and metadata have been loaded into the draft.",
            clonePayload);
    }

    private CreatorToolResult HandleSetDataFlow(JsonElement args)
    {
        var payload = JsonSerializer.Deserialize<object>(args.GetRawText(), _jsonOpts);
        return new CreatorToolResult(
            "Data flow connections updated.",
            payload ?? new { });
    }

    private CreatorToolResult HandleValidateDraft(JsonElement args)
    {
        // The validation logic checks common issues
        var issues = new List<string>();
        var draft = args;

        if (draft.TryGetProperty("metadata", out var meta))
        {
            if (!meta.TryGetProperty("id", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
                issues.Add("Metadata: id is required");
            if (!meta.TryGetProperty("domain", out var dom) || string.IsNullOrWhiteSpace(dom.GetString()))
                issues.Add("Metadata: domain is required");
        }
        else
        {
            issues.Add("No metadata defined");
        }

        if (draft.TryGetProperty("stages", out var stages) && stages.ValueKind == JsonValueKind.Array)
        {
            if (stages.GetArrayLength() == 0)
                issues.Add("At least one stage is required");

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ValidateStageArray(stages, issues, seenIds, scopePrefix: null);
            ValidateForeachToolCoherence(stages, issues);
        }
        else
        {
            issues.Add("No stages defined");
        }

        var valid = issues.Count == 0;
        var resultText = valid ? "Draft is valid!" : $"Validation found {issues.Count} issue(s):\n" + string.Join("\n", issues.Select(i => $"- {i}"));

        return new CreatorToolResult(resultText, new { valid, issues });
    }

    /// <summary>
    /// Recursively validate a stage array (foreach sub-stages included). Enforces unique
    /// stage IDs across the whole tree, foreach loop configuration, scoped duplicate tools,
    /// and token/data-flow coherence.
    /// </summary>
    private static void ValidateStageArray(JsonElement stages, List<string> issues, HashSet<string> seenIds, string? scopePrefix)
    {
        ValidateStageArray(
            stages,
            issues,
            seenIds,
            scopePrefix,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<string> ValidateStageArray(
        JsonElement stages,
        List<string> issues,
        HashSet<string> seenIds,
        string? scopePrefix,
        HashSet<string> upstreamProducers,
        HashSet<string> aliasScope)
    {
        ValidateDuplicateToolStagesInScope(stages, issues, scopePrefix);

        var producedInScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var stage in stages.EnumerateArray())
        {
            var sid = stage.TryGetProperty("stageId", out var s) ? s.GetString() : null;
            var type = stage.TryGetProperty("type", out var t) ? t.GetString() : null;
            var stageLabel = string.IsNullOrWhiteSpace(sid) ? "(missing stageId)" : sid;

            if (string.IsNullOrWhiteSpace(sid))
                issues.Add("A stage is missing a stageId");
            else if (!seenIds.Add(sid))
                issues.Add($"Duplicate stage id '{sid}' (stage ids must be unique across the whole workflow, including foreach sub-stages)");
            if (string.IsNullOrWhiteSpace(type))
                issues.Add($"Stage '{sid}' is missing a type");

            ValidateStageTokenReferences(stage, stageLabel, issues, upstreamProducers, aliasScope);

            if (type == "tool" && !stage.TryGetProperty("toolId", out _) && !stage.TryGetProperty("prePrompt", out _))
                issues.Add($"Stage '{sid}' (tool): needs either toolId or prePrompt");

            if (type == "foreach")
            {
                var itemsFrom = stage.TryGetProperty("itemsFrom", out var ifEl) ? ifEl.GetString() : null;
                var itemAlias = stage.TryGetProperty("itemAlias", out var iaEl) ? iaEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(itemsFrom))
                {
                    issues.Add($"Stage '{sid}' (foreach): itemsFrom is required (the array field to iterate)");
                }
                else
                {
                    var itemsField = NormalizeTokenReference(itemsFrom);
                    var itemsRoot = GetTokenRoot(itemsField);
                    if (!upstreamProducers.Contains(itemsRoot) && !aliasScope.Contains(itemsRoot))
                    {
                        issues.Add($"Stage '{sid}' (foreach): itemsFrom \"{itemsField}\" has no upstream producer or in-scope foreach alias");
                    }
                }

                if (string.IsNullOrWhiteSpace(itemAlias))
                    issues.Add($"Stage '{sid}' (foreach): itemAlias is required (the per-item variable name)");

                if (stage.TryGetProperty("stages", out var subStages) && subStages.ValueKind == JsonValueKind.Array)
                {
                    if (subStages.GetArrayLength() == 0)
                        issues.Add($"Stage '{sid}' (foreach): needs at least one sub-stage");

                    var childUpstream = new HashSet<string>(upstreamProducers, StringComparer.OrdinalIgnoreCase);
                    var childAliases = new HashSet<string>(aliasScope, StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(itemAlias))
                        childAliases.Add(itemAlias);

                    var childProduced = ValidateStageArray(subStages, issues, seenIds, scopePrefix: sid, childUpstream, childAliases);
                    foreach (var producer in childProduced)
                    {
                        upstreamProducers.Add(producer);
                        producedInScope.Add(producer);
                    }
                }
                else
                {
                    issues.Add($"Stage '{sid}' (foreach): needs at least one sub-stage");
                }
            }

            foreach (var producer in GetProducedFields(stage))
            {
                upstreamProducers.Add(producer);
                producedInScope.Add(producer);
            }
        }

        return producedInScope;
    }

    private static void ValidateDuplicateToolStagesInScope(JsonElement stages, List<string> issues, string? scopePrefix)
    {
        var firstStageByTool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scopeName = string.IsNullOrWhiteSpace(scopePrefix) ? "top-level scope" : $"foreach '{scopePrefix}'";

        foreach (var stage in stages.EnumerateArray())
        {
            if (!IsStageType(stage, "tool"))
                continue;

            var toolId = GetStringProperty(stage, "toolId");
            if (string.IsNullOrWhiteSpace(toolId))
                continue;

            var stageId = GetStringProperty(stage, "stageId") ?? "(missing stageId)";
            if (firstStageByTool.TryGetValue(toolId, out var firstStageId))
                issues.Add($"Duplicate tool stage in {scopeName}: stages '{firstStageId}' and '{stageId}' both use toolId '{toolId}'");
            else
                firstStageByTool[toolId] = stageId;
        }
    }

    private static void ValidateStageTokenReferences(
        JsonElement stage,
        string stageId,
        List<string> issues,
        HashSet<string> upstreamProducers,
        HashSet<string> aliasScope)
    {
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CheckText(GetStringProperty(stage, "prompt"), "prompt");
        CheckText(GetStringProperty(stage, "selectPrompt"), "selectPrompt");

        if (stage.TryGetProperty("parameters", out var parameters))
        {
            foreach (var value in EnumerateStringValues(parameters))
                CheckText(value, "parameters");
        }

        void CheckText(string? text, string location)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            foreach (Match match in TokenRegex.Matches(text))
            {
                var token = NormalizeTokenReference(match.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                var root = GetTokenRoot(token);
                var issueKey = $"{location}:{token}";
                if (!upstreamProducers.Contains(root) && !aliasScope.Contains(root) && reported.Add(issueKey))
                    issues.Add($"Stage '{stageId}': token \"{{{{{token}}}}}\" in {location} has no upstream producer or in-scope foreach alias");
            }
        }
    }

    private static IEnumerable<string> EnumerateStringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null)
                    yield return value;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var child in EnumerateStringValues(property.Value))
                        yield return child;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in EnumerateStringValues(item))
                        yield return child;
                }
                break;
        }
    }

    private static IEnumerable<string> GetProducedFields(JsonElement stage)
    {
        var stageId = GetStringProperty(stage, "stageId");
        var type = GetStringProperty(stage, "type");

        if (string.Equals(type, "ai-extract", StringComparison.OrdinalIgnoreCase) &&
            stage.TryGetProperty("required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in required.EnumerateArray())
            {
                if (field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString()))
                    yield return GetTokenRoot(field.GetString()!);
            }
        }

        if (string.Equals(type, "tool", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(stageId))
        {
            yield return $"_result_{stageId}";
        }

        if (stage.TryGetProperty("outputMap", out var outputMap) &&
            outputMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in outputMap.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(property.Name))
                    yield return GetTokenRoot(property.Name);
            }
        }

        if (string.Equals(type, "ai-generate", StringComparison.OrdinalIgnoreCase))
            yield return "_generatedContent";
    }

    private static void ValidateForeachToolCoherence(JsonElement stages, List<string> issues)
    {
        var topLevelToolStages = new List<(string StageId, string ToolId)>();
        var foreachToolStages = new List<(string ForeachId, string StageId, string ToolId)>();

        foreach (var stage in stages.EnumerateArray())
        {
            var stageId = GetStringProperty(stage, "stageId") ?? "(missing stageId)";
            if (IsStageType(stage, "tool"))
            {
                var toolId = GetStringProperty(stage, "toolId");
                if (!string.IsNullOrWhiteSpace(toolId))
                    topLevelToolStages.Add((stageId, toolId));
            }

            if (IsStageType(stage, "foreach") &&
                stage.TryGetProperty("stages", out var subStages) &&
                subStages.ValueKind == JsonValueKind.Array)
            {
                foreach (var subStage in subStages.EnumerateArray())
                {
                    if (!IsStageType(subStage, "tool"))
                        continue;

                    var toolId = GetStringProperty(subStage, "toolId");
                    if (!string.IsNullOrWhiteSpace(toolId))
                    {
                        foreachToolStages.Add((
                            stageId,
                            GetStringProperty(subStage, "stageId") ?? "(missing stageId)",
                            toolId));
                    }
                }
            }
        }

        foreach (var topLevel in topLevelToolStages)
        {
            foreach (var nested in foreachToolStages.Where(s => string.Equals(s.ToolId, topLevel.ToolId, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add($"Coherence: top-level tool stage '{topLevel.StageId}' uses toolId '{topLevel.ToolId}', which also appears inside foreach '{nested.ForeachId}' sub-stage '{nested.StageId}'; remove the stranded duplicate or move it into the loop");
            }
        }
    }

    private static bool IsStageType(JsonElement stage, string type) =>
        string.Equals(GetStringProperty(stage, "type"), type, StringComparison.OrdinalIgnoreCase);

    private static string? GetStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string NormalizeTokenReference(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("{{", StringComparison.Ordinal) && normalized.EndsWith("}}", StringComparison.Ordinal))
            normalized = normalized[2..^2].Trim();
        return normalized;
    }

    private static string GetTokenRoot(string value)
    {
        var normalized = NormalizeTokenReference(value);
        var dotIndex = normalized.IndexOf('.');
        return dotIndex >= 0 ? normalized[..dotIndex] : normalized;
    }

    private CreatorToolResult HandleRegisterTool(JsonElement args)
    {
        // Returns the tool definition for user confirmation — actual save happens via save-flow
        var payload = JsonSerializer.Deserialize<object>(args.GetRawText(), _jsonOpts);
        var toolId = args.TryGetProperty("id", out var tid) ? tid.GetString() : "new-tool";

        return new CreatorToolResult(
            $"Tool '{toolId}' definition prepared. It will be registered when the workflow is saved.",
            payload ?? new { });
    }

    private async Task<CreatorToolResult> HandleListAvailableTools()
    {
        var allTools = await _tools.GetAllAsync();
        var toolList = allTools.Select(t => new
        {
            t.Id,
            t.Description,
            t.Service,
            t.Route,
            t.HttpMethod,
            parameters = t.Parameters.Select(p => new { p.Name, p.Type, p.Required }).ToList()
        }).ToList();

        return new CreatorToolResult(
            $"There are {toolList.Count} available tools.",
            new { tools = toolList });
    }

    // ── Creator Tool Definitions (OpenAI function calling format) ──────────────

    private static List<ChatTool> GetCreatorTools()
    {
        return new List<ChatTool>
        {
            ChatTool.CreateFunctionTool(
                "set_metadata",
                "Set or update workflow metadata fields. Call this to define the workflow's identity.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "id": { "type": "string", "description": "Workflow ID in kebab-case (e.g., 'ado-search-bugs')" },
                        "domain": { "type": "string", "description": "Primary domain (e.g., 'ado', 'study', 'ctms')" },
                        "domains": { "type": "array", "items": { "type": "string" }, "description": "All domains this workflow belongs to" },
                        "description": { "type": "string", "description": "Short description for intent matching" },
                        "samplePrompt": { "type": "string", "description": "Example prompt users would type" },
                        "contextTags": { "type": "array", "items": { "type": "string" }, "description": "Natural phrases that trigger this workflow (6-10 recommended)" },
                        "systemPrompt": { "type": "string", "description": "REQUIRED. Instructions the LLM follows throughout the workflow. Always provide this — workflows cannot be saved without it." },
                        "piiCheck": { "type": "boolean", "description": "Whether to scan for PII before sending to LLM/tools" }
                    },
                    "required": ["id"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "add_stage",
                "Add a new stage to the workflow. Stages execute sequentially. Use appropriate type for the purpose. To add a stage INSIDE a foreach loop, set parentStageId to the foreach stage's id.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "stageId": { "type": "string", "description": "Unique stage identifier — MUST be unique across the ENTIRE workflow, including foreach sub-stages (e.g., 'extract', 'search', 'createTask')" },
                        "type": { "type": "string", "enum": ["ai-extract", "tool", "ai-generate", "ai-analyze", "user-select", "confirmation", "foreach", "agentic"], "description": "Stage type" },
                        "prompt": { "type": "string", "description": "LLM prompt for AI stages (extract/generate/analyze). For confirmation stages, this is REQUIRED — it's the question shown to the user." },
                        "required": { "type": "array", "items": { "type": "string" }, "description": "Required field names for ai-extract stages" },
                        "toolId": { "type": "string", "description": "Tool ID for tool stages" },
                        "parameters": { "type": "object", "description": "Tool parameters with {{token}} placeholders" },
                        "outputMap": { "type": "object", "description": "Map target fields from JSON result properties" },
                        "prePrompt": { "type": "string", "description": "Pre-prompt for dynamic tool selection" },
                        "allowedToolIds": { "type": "array", "items": { "type": "string" }, "description": "Allowed tool IDs for dynamic tool stages" },
                        "selectPrompt": { "type": "string", "description": "Prompt for user-select stages" },
                        "optionsFrom": { "type": "string", "description": "Field name containing options for user-select" },
                        "templateRef": { "type": "string", "description": "Template ID for ai-generate stages — ONLY use if template exists" },
                        "itemsFrom": { "type": "string", "description": "foreach ONLY: the field name holding the array to iterate (e.g., a tool result outputMap field). Must reference an upstream array-producing field." },
                        "itemAlias": { "type": "string", "description": "foreach ONLY: per-item variable name; reference it inside sub-stages as {{alias}} or {{alias.field}}" },
                        "confirmEach": { "type": "boolean", "description": "foreach ONLY: pause for confirmation before each iteration" },
                        "stages": { "type": "array", "description": "foreach ONLY: nested sub-stages executed per item (one level deep — no foreach inside foreach). Each item uses the same stage props as a top-level stage.", "items": { "type": "object" } },
                        "parentStageId": { "type": "string", "description": "If set, add this stage INSIDE the foreach with this id (as a sub-stage) instead of at the top level." },
                        "afterStageId": { "type": "string", "description": "Insert this stage after the specified sibling stageId within the target scope. If omitted, appends to the end of the scope." }
                    },
                    "required": ["stageId", "type"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "update_stage",
                "Update properties of an existing stage (searched by id anywhere in the workflow, including inside foreach loops).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "stageId": { "type": "string", "description": "ID of the stage to update (may be a foreach sub-stage)" },
                        "changes": { "type": "object", "description": "Properties to update (same schema as add_stage minus stageId). For foreach, you may pass a nested 'stages' array to replace its sub-stages." }
                    },
                    "required": ["stageId", "changes"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "remove_stage",
                "Remove a stage from the workflow (searched by id anywhere, including inside foreach loops).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "stageId": { "type": "string", "description": "ID of the stage to remove (may be a foreach sub-stage)" }
                    },
                    "required": ["stageId"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "reorder_stages",
                "Change the execution order of stages within a scope. Omit parentStageId to reorder top-level stages, or set it to a foreach id to reorder that loop's sub-stages.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "stageIds": { "type": "array", "items": { "type": "string" }, "description": "Stage IDs in desired execution order, within the chosen scope" },
                        "parentStageId": { "type": "string", "description": "If set, reorder the sub-stages inside this foreach instead of the top-level stages." }
                    },
                    "required": ["stageIds"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "search_workflows",
                "Search for existing workflows similar to a description. Use this to find workflows that can be cloned or referenced.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "query": { "type": "string", "description": "Natural language description of the workflow to search for" }
                    },
                    "required": ["query"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "get_workflow_details",
                "Get the full definition of an existing workflow (stages, prompts, tool references).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "workflowId": { "type": "string", "description": "ID of the workflow to retrieve" }
                    },
                    "required": ["workflowId"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "clone_workflow",
                "Clone an existing workflow into the draft as a starting point.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "sourceId": { "type": "string", "description": "ID of the workflow to clone" }
                    },
                    "required": ["sourceId"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "set_data_flow",
                "Explicitly wire token references between stages for data flow visualization.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "connections": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "fromStageId": { "type": "string" },
                                    "toStageId": { "type": "string" },
                                    "fieldName": { "type": "string" },
                                    "tokenRef": { "type": "string" }
                                }
                            },
                            "description": "Data flow connections between stages"
                        }
                    },
                    "required": ["connections"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "validate_draft",
                "Check the current draft for issues (missing required fields, broken token references).",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "metadata": { "type": "object", "description": "Current metadata" },
                        "stages": { "type": "array", "items": { "type": "object" }, "description": "Current stages" }
                    },
                    "required": ["metadata", "stages"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "register_tool",
                "Create a new tool definition. Use when the workflow needs a tool that doesn't exist yet.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "id": { "type": "string", "description": "Tool ID in kebab-case" },
                        "service": { "type": "string", "description": "Service base URL or name" },
                        "route": { "type": "string", "description": "API route path" },
                        "httpMethod": { "type": "string", "enum": ["GET", "POST", "PUT", "PATCH", "DELETE"], "description": "HTTP method" },
                        "description": { "type": "string", "description": "Tool description" },
                        "parameters": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "name": { "type": "string" },
                                    "type": { "type": "string" },
                                    "required": { "type": "boolean" },
                                    "location": { "type": "string", "enum": ["query", "path", "body"] },
                                    "description": { "type": "string" }
                                }
                            },
                            "description": "Tool parameters"
                        }
                    },
                    "required": ["id", "service", "route", "httpMethod"]
                }
                """)),

            ChatTool.CreateFunctionTool(
                "list_available_tools",
                "List all registered tools with their parameters. Use to find the right tool for a stage.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {},
                    "required": []
                }
                """))
        };
    }
}

// ── Supporting types ─────────────────────────────────────────────────────────

public class CreateWorkflowRequest
{
    public string Message { get; set; } = string.Empty;
    public string UserId { get; set; } = "workflow-creator";
    public string? SessionId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public List<ConversationMessage>? ConversationHistory { get; set; }
    public WorkflowDraft? CurrentDraft { get; set; }
    public List<string>? EditBuffer { get; set; }

    /// <summary>"create" (default) or "edit" — edit mode operates on an existing workflow draft.</summary>
    public string? Mode { get; set; }

    /// <summary>When editing, the id of the workflow being edited.</summary>
    public string? EditingWorkflowId { get; set; }
}

public class WorkflowDraft
{
    public WorkflowDraftMetadata? Metadata { get; set; }
    public List<WorkflowDraftStage>? Stages { get; set; }
}

public class WorkflowDraftMetadata
{
    public string? Id { get; set; }
    public string? Domain { get; set; }
    public List<string>? Domains { get; set; }
    public string? Description { get; set; }
    public string? SamplePrompt { get; set; }
    public List<string>? ContextTags { get; set; }
    public string? SystemPrompt { get; set; }
    public bool PiiCheck { get; set; }
}

public class WorkflowDraftStage
{
    public string StageId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Prompt { get; set; }
    public string? ToolId { get; set; }
    public List<string>? Required { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
    public Dictionary<string, string>? OutputMap { get; set; }
    public string? PrePrompt { get; set; }
    public List<string>? AllowedToolIds { get; set; }
    public string? SelectPrompt { get; set; }
    public string? OptionsFrom { get; set; }
    public string? ConfirmPrompt { get; set; }
    public string? TemplateRef { get; set; }

    // foreach (loop) fields — supported one level deep (no foreach-in-foreach)
    public string? ItemsFrom { get; set; }
    public string? ItemAlias { get; set; }
    public bool? ConfirmEach { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("stages")]
    public List<WorkflowDraftStage>? Stages { get; set; }

    /// <summary>
    /// Deserialization alias. The workflow-builder frontend serializes foreach children
    /// under "subStages" (its CreationStageCard shape), while the engine/agent convention
    /// is "stages". Accept "subStages" on input and fold it into <see cref="Stages"/> so the
    /// agent's [CURRENT WORKFLOW DRAFT] includes loop sub-stages. Never emitted on output.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("subStages")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<WorkflowDraftStage>? SubStages
    {
        get => null;
        set { if (value is { Count: > 0 }) Stages = value; }
    }
}

internal record CreatorToolResult(string ResultText, object Payload);
