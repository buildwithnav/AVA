namespace Ava.Api.Models;

// ── Inbound request ──────────────────────────────────────────────────────────

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public UserContext Context { get; set; } = new();
    /// <summary>Set when resuming a paused workflow run (clarify or select/confirm flows).</summary>
    public string? ResumeRunId { get; set; }
    /// <summary>
    /// Session ID issued to the browser — used to look up the user's GitHub token,
    /// ADO PAT, and selected model from the server-side session store.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Prior conversation turns carried into a workflow when the matched workflow has
    /// PreserveContext = true. Populated by AgentService just before handing off to the
    /// WorkflowEngine; null for normal chat requests. Lets workflow AI stages reference
    /// earlier discussion (e.g. pre-populate a support ticket from prior messages).
    /// </summary>
    public List<ConversationMessage>? ConversationHistory { get; set; }
}

public class UserContext
{
    public string Domain { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public string UserId { get; set; } = string.Empty;
    public string? SubDomain { get; set; }
    /// <summary>Pylon username (unique_name claim). Resolved server-side from the JWT; ignored if sent by the client.</summary>
    public string Username { get; set; } = string.Empty;
}

// ── Parameterized prompt execution (Aperture-compatible) ─────────────────────

public class PromptExecuteRequest
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string? Domain { get; set; }
    /// <summary>Session ID — used to resolve GitHub token / model for this request.</summary>
    public string? SessionId { get; set; }
}

public class PromptExecuteResponse
{
    public string PromptId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int TokensUsed { get; set; }
}

// ── Agent run model ──────────────────────────────────────────────────────────

public class AgentRun
{
    public string AgentRunId { get; set; } = Guid.NewGuid().ToString();
    public string ConversationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    /// <summary>Pylon username (unique_name claim) resolved from the request JWT.</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>The browser session ID this run originated from. Trust-based (client-supplied) until auth is added.</summary>
    public string? SessionId { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public List<StepExecution> Steps { get; set; } = new();
    public string? FinalResponse { get; set; }
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Running;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int TokensUsed { get; set; }
    /// <summary>Input (prompt) tokens used across all LLM calls in this run.</summary>
    public int InputTokensUsed { get; set; }
    /// <summary>Output (completion) tokens used across all LLM calls in this run.</summary>
    public int OutputTokensUsed { get; set; }
    /// <summary>The resolved model ID used for LLM calls in this run (e.g. "gpt-4.1", "gpt-4o-mini").</summary>
    public string? ModelId { get; set; }
    /// <summary>Estimated cost in USD computed from ModelPricing config at run completion.</summary>
    public decimal EstimatedCostUsd { get; set; }
    /// <summary>The prompt definition ID that was matched for this run (null for conversational replies).</summary>
    public string? MatchedPromptId { get; set; }
    /// <summary>Populated when the run is paused inside a staged workflow.</summary>
    public WorkflowContext? WorkflowContext { get; set; }
}

public enum AgentRunStatus
{
    Running,
    WaitingForInput,   // stage engine paused — clarify / user-select / ai-generate / confirmation
    Completed,
    Failed,
    Cancelled
}

public class StepExecution
{
    public string StepId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int DurationMs { get; set; }
    public int TokensUsed { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}

// ── Workflow stage definitions ────────────────────────────────────────────────

/// <summary>
/// A single stage in a staged workflow. Stages are executed in order.
/// Stage types: ai-extract | tool | user-select | ai-generate | ai-analyze | confirmation | foreach
/// </summary>
public class WorkflowStage
{
    public string StageId { get; set; } = string.Empty;

    /// <summary>ai-extract | tool | user-select | ai-generate | ai-analyze | confirmation | foreach</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>For tool stages: the tool ID from tools.json to execute.</summary>
    public string? ToolId { get; set; }

    /// <summary>For ai-generate stages: references a template in templates.json.</summary>
    public string? TemplateRef { get; set; }

    /// <summary>
    /// For ai-extract: fields that must be present to continue.
    /// If missing, engine streams a clarifying question and pauses.
    /// </summary>
    public List<string>? Required { get; set; }

    /// <summary>
    /// Instruction text for AI stages (ai-extract, ai-analyze, ai-generate).
    /// Supports {{token}} substitution from ctx.Fields.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// For tool stages with dynamic tool selection — AI picks the tool from AllowedToolIds.
    /// </summary>
    public StagePrePrompt? PrePrompt { get; set; }

    /// <summary>
    /// Maps output field names from this stage into ctx.Fields.
    /// Key = ctx field name, Value = property name in the tool response JSON root.
    /// </summary>
    public Dictionary<string, string>? OutputMap { get; set; }

    /// <summary>
    /// For tool stages: parameters passed to the tool.
    /// Supports {{token}} substitution from ctx.Fields.
    /// </summary>
    public Dictionary<string, string>? Parameters { get; set; }

    /// <summary>
    /// For user-select: the prompt shown to the user above the options.
    /// </summary>
    public string? SelectPrompt { get; set; }

    /// <summary>
    /// For user-select: the ctx.Fields key whose value contains the list to present.
    /// Supports dot-path notation (e.g., "_result_initFilters.studies") for nested arrays.
    /// </summary>
    public string? OptionsFrom { get; set; }

    /// <summary>
    /// For user-select: which property in each option object to use for the display label.
    /// Defaults to "text" or "label" if not specified.
    /// </summary>
    public string? LabelField { get; set; }

    /// <summary>
    /// For ai-generate and confirmation stages: the stageId to rewind to when the user
    /// requests edits/corrections instead of confirming. If not set, defaults to the
    /// first ai-extract stage.
    /// </summary>
    public string? RewindTo { get; set; }

    /// <summary>
    /// For ai-extract: custom message shown when required fields are missing.
    /// If not set, a generic message listing the missing field names is used.
    /// </summary>
    public string? ClarifyPrompt { get; set; }

    /// <summary>
    /// For ai-analyze/ai-extract stages: limit which tool-stage results are included in the AI context.
    /// If set, only steps whose StepId (stageId) matches one of these IDs are included.
    /// If not set, all tool results are included (backward-compatible).
    /// </summary>
    public List<string>? DataFrom { get; set; }

    /// <summary>
    /// For ai-analyze stages: which ctx.Fields to include as "User selections" in the AI prompt.
    /// Each entry maps a field key to a human-readable label.
    /// If null/empty, falls back to showing all non-null, non-internal fields.
    /// </summary>
    public List<SelectionFieldMapping>? SelectionFields { get; set; }

    // ── Foreach stage properties ──────────────────────────────────────────────

    /// <summary>
    /// For foreach stages: the ctx.Fields key whose value contains the array to iterate over.
    /// Supports dot-path notation (e.g., "_result_queryRows.rows") for nested arrays.
    /// </summary>
    public string? ItemsFrom { get; set; }

    /// <summary>
    /// For foreach stages: alias name for the current iteration item.
    /// Sub-stages can reference item properties via {{itemAlias.propertyName}}.
    /// </summary>
    public string? ItemAlias { get; set; }

    /// <summary>
    /// For foreach stages: when true, pauses for user confirmation before each iteration.
    /// When false, runs all iterations without pausing. Defaults to false.
    /// </summary>
    public bool ConfirmEach { get; set; } = false;

    /// <summary>
    /// For foreach stages: the list of sub-stages to execute per iteration.
    /// Each sub-stage follows the same WorkflowStage schema and supports nesting.
    /// </summary>
    public List<WorkflowStage>? Stages { get; set; }

    // ── Agentic stage properties ─────────────────────────────────────────────

    /// <summary>
    /// For agentic stages: configuration controlling the autonomous tool loop.
    /// Specifies service key, tool domains, iteration limits, token budget, and output field.
    /// </summary>
    public AgenticStageDefinition? AgenticConfig { get; set; }
}

/// <summary>
/// Maps a workflow context field to a human-readable label for selection context display.
/// </summary>
public class SelectionFieldMapping
{
    public string Field { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// When a tool stage uses a prePrompt, the AI picks the tool ID and parameters
/// from the allowed set rather than the stage having a fixed toolId.
/// </summary>
public class StagePrePrompt
{
    public string Prompt { get; set; } = string.Empty;
    public List<string> AllowedToolIds { get; set; } = new();

    /// <summary>
    /// Fixed parameters (supports {{token}} substitution) that are always merged into
    /// the LLM-selected tool's params. Static values take precedence over LLM-chosen values,
    /// allowing authors to lock in context fields like project or iterationPath.
    /// </summary>
    public Dictionary<string, string>? StaticParams { get; set; }
}

/// <summary>
/// Configuration for an "agentic" workflow stage that runs an autonomous tool loop.
/// </summary>
public class AgenticStageDefinition
{
    /// <summary>
    /// The MCP service key to use for this stage (must match a key in Ava:Services with IsMcp=true).
    /// Required — the stage will emit an error if not set.
    /// </summary>
    public string? ServiceKey { get; set; }

    /// <summary>Which MCP tool domains to expose (e.g., ["git", "work-items"]). Null = all tools.</summary>
    public List<string>? ToolDomains { get; set; }

    /// <summary>Maximum tool call iterations before forcing a summary. Default: 15.</summary>
    public int? MaxIterations { get; set; }

    /// <summary>Maximum token budget for this stage. Default: 50000.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>System prompt for the LLM (supports {{token}} substitution from workflow context).</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Which workflow context field to store the final output in.</summary>
    public string? OutputField { get; set; }
}

// ── Workflow execution context ────────────────────────────────────────────────

/// <summary>
/// Live execution state for a staged workflow run.
/// Stored on AgentRun.WorkflowContext and persisted across pause/resume cycles.
/// </summary>
public class WorkflowContext
{
    /// <summary>Index of the next stage to execute (0-based into PromptDefinition.Stages).</summary>
    public int CurrentStageIndex { get; set; } = 0;

    /// <summary>All extracted + user-provided field values accumulated across stages.</summary>
    public Dictionary<string, object?> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's original message — preserved for clarify reruns.</summary>
    public string OriginalMessage { get; set; } = string.Empty;

    /// <summary>
    /// Prior conversation turns carried into this workflow when the prompt has
    /// PreserveContext = true. AI stage handlers (extract/analyze/generate) prepend
    /// these to their LLM user message so the workflow can reference earlier discussion.
    /// Null when context preservation is disabled.
    /// </summary>
    public List<ConversationMessage>? ConversationHistory { get; set; }

    /// <summary>
    /// Set when the workflow is paused.
    /// Values: "clarify" | "user-select" | "ai-generate" | "confirmation"
    /// </summary>
    public string? PauseType { get; set; }

    /// <summary>Serialized payload the client needs to resume (options list, preview data, etc.).</summary>
    public object? PausePayload { get; set; }

    /// <summary>The prompt definition ID this context belongs to.</summary>
    public string PromptId { get; set; } = string.Empty;

    /// <summary>
    /// Journal of field writes per stage, enabling rollback with value restoration.
    /// Each entry records the stageId, field name, and the previous value (if any).
    /// </summary>
    public List<FieldWriteEntry> FieldJournal { get; set; } = new();

    /// <summary>Incremented on each rewind to version frontend messages.</summary>
    public int Revision { get; set; } = 0;

    /// <summary>
    /// Signal passed from HandleResume to nested stage handlers (e.g. foreach).
    /// Values: "confirmed" | "correction" | null.
    /// Set by HandleResume when the paused stage is inside a container stage;
    /// consumed and cleared by the container handler on re-entry.
    /// </summary>
    public string? ResumeSignal { get; set; }

    /// <summary>
    /// Per-workflow model override. Set from PromptDefinition.ModelId when a workflow starts.
    /// Stage handlers pass this to ChatClientFactory.CreateAsync so the whole workflow
    /// uses a consistent (possibly cheaper) model regardless of user's session selection.
    /// Null = use the session/default model.
    /// </summary>
    public string? ModelOverride { get; set; }

    /// <summary>
    /// Sets a field with journaling — records the previous value for rollback support.
    /// </summary>
    public void SetField(string stageId, string fieldName, object? value)
    {
        bool hadPrev = Fields.TryGetValue(fieldName, out var prev);
        FieldJournal.Add(new FieldWriteEntry
        {
            StageId = stageId,
            FieldName = fieldName,
            PreviousValue = hadPrev ? prev : null,
            HadPreviousValue = hadPrev
        });
        Fields[fieldName] = value;
    }
}

// ── Select / resume request ───────────────────────────────────────────────────

public class SelectRequest
{
    public string AgentRunId { get; set; } = string.Empty;

    /// <summary>select | confirm | dismiss</summary>
    public string InputType { get; set; } = string.Empty;

    /// <summary>For user-select: zero-based index of the chosen option.</summary>
    public int? SelectedIndex { get; set; }

    /// <summary>For ai-generate confirm: the confirmed field values.</summary>
    public Dictionary<string, object?>? ConfirmedData { get; set; }
}

// ── Rewind request ───────────────────────────────────────────────────────────

public class RewindRequest
{
    public string AgentRunId { get; set; } = string.Empty;
    public string TargetStageId { get; set; } = string.Empty;
}

public class DetectDomainRequest
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Request to test a workflow definition directly without saving it first.
/// Bypasses the agent loop and semantic search — executes the provided
/// workflow stages against the test message immediately.
/// </summary>
public class TestWorkflowRequest
{
    public string Message { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string UserId { get; set; } = "workflow-builder-test";
    public PromptDefinition WorkflowDefinition { get; set; } = new();
    public string? SessionId { get; set; }
    /// <summary>Used when resuming a paused test-workflow run.</summary>
    public string? AgentRunId { get; set; }
}

// ── Field journaling ─────────────────────────────────────────────────────────

/// <summary>
/// Records a single field write for rollback support.
/// </summary>
public class FieldWriteEntry
{
    public string StageId { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public object? PreviousValue { get; set; }
    public bool HadPreviousValue { get; set; }
}

/// <summary>
/// Describes a stage that can be rewound to in the UI.
/// </summary>
public class RewindableStageInfo
{
    public string StageId { get; set; } = string.Empty;
    public int StageIndex { get; set; }
    public string Label { get; set; } = string.Empty;
}

// ── Conversation ─────────────────────────────────────────────────────────────

public class ConversationMessage
{
    public string Role { get; set; } = string.Empty;   // "user" | "assistant"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// ── Tool definitions ─────────────────────────────────────────────────────────

/// <summary>
/// A downloadable CSV export tied to a single successful kb_query executed during a turn.
/// Emitted in the "done" SSE event so the UI can render a "Download CSV" link. The link is
/// backed by the SQL that produced the rows, so the CSV always reproduces the results
/// (unlike the LLM-authored in-chat table, which may show only a subset).
/// </summary>
public class TableExport
{
    public string QueryId { get; set; } = string.Empty;
    /// <summary>
    /// The SQL that backs the CSV. Any trailing LIMIT/OFFSET the model added (often defensively,
    /// to cap the in-chat table) is stripped so the export returns the full result set. This is
    /// also the SQL shown in the UI, so "Show SQL" matches exactly what the CSV contains.
    /// </summary>
    public string Sql { get; set; } = string.Empty;
    public string[] DatasetIds { get; set; } = Array.Empty<string>();
    public List<string> Columns { get; set; } = new();
    /// <summary>Row count from the (possibly LIMIT-capped) in-chat query. Not the CSV row count when <see cref="LimitRemoved"/> is true.</summary>
    public int RowCount { get; set; }
    /// <summary>True when the in-chat query hit the 1000-row cap; the CSV export is still full.</summary>
    public bool Truncated { get; set; }
    /// <summary>True when a trailing LIMIT/OFFSET was stripped for the export — the CSV has more rows than RowCount.</summary>
    public bool LimitRemoved { get; set; }
    /// <summary>Relative API path the client fetches to download the full CSV.</summary>
    public string DownloadUrl { get; set; } = string.Empty;
}

public class ToolDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = "GET";
    public string? Description { get; set; }
    public List<ToolParameter> Parameters { get; set; } = new();
    public bool RequiresConfirmation { get; set; }
    /// <summary>True for internal tools (kb_schema, kb_query, kb_search, start_workflow).</summary>
    public bool Internal { get; set; }
    /// <summary>Format of external API response: "json", "csv", "xlsx".</summary>
    public string? ResponseFormat { get; set; }
    /// <summary>Auto-store tool output as a queryable KB.</summary>
    public StoreAsKBConfig? StoreAsKB { get; set; }
    /// <summary>Request body encoding: "json" (default) or "form" (FormUrlEncodedContent).</summary>
    public string ContentType { get; set; } = "json";
}

public class ToolParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string Location { get; set; } = "query";   // "query" | "path" | "body" | "form" | "formData"
    public string? Description { get; set; }
    /// <summary>Default value used when the parameter is not supplied at runtime.</summary>
    public string? DefaultValue { get; set; }
}

// ── Prompt definitions ───────────────────────────────────────────────────────

public class PromptDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Example prompt displayed in the chat input when a suggestion card is clicked.
    /// Falls back to Description on the frontend if empty.
    /// </summary>
    public string? SamplePrompt { get; set; }

    public List<string> ContextTags { get; set; } = new();
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Staged workflow stages. All prompts must define stages.
    /// </summary>
    public List<WorkflowStage>? Stages { get; set; }

    /// <summary>
    /// Optional model override for this workflow. When set, all LLM stage calls within this
    /// workflow use this model instead of the user's session-selected model.
    /// For GitHub Copilot path: any model ID from AvailableModels (e.g. "gpt-4o-mini").
    /// For Azure AOAI path: must map to an entry in AzureOpenAi:ModelMappings.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// When true, all field values and tool parameters are scanned for PII before
    /// being sent to the LLM or any tool. Blocked if PII is detected.
    /// </summary>
    public bool PiiCheck { get; set; } = false;

    /// <summary>
    /// When true, the prior conversation history is carried into the workflow so AI stages
    /// (extract/analyze/generate) can reference earlier discussion. Useful for workflows like
    /// support-request creation where the ticket should be pre-populated from the conversation.
    /// Defaults to false to keep token usage low for context-independent workflows.
    /// </summary>
    public bool PreserveContext { get; set; } = false;
}

// ── Template definitions ─────────────────────────────────────────────────────

public class TemplateDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<TemplateField> Fields { get; set; } = new();
}

public class TemplateField
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>text | multiline | select | number | date</summary>
    public string Type { get; set; } = "text";
    public bool Required { get; set; }
    public List<string>? Options { get; set; }
}

// ── Domain definitions ────────────────────────────────────────────────────────

public class DomainDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> KnowledgeBaseIds { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();
    public string? WelcomeMessage { get; set; }

    /// <summary>
    /// Domain-level behavioral instruction injected into the agent's system prompt for every
    /// message in this domain (regular chat and before workflows trigger). Use it to shape how
    /// AVA behaves in the domain — e.g. "When a user describes an issue, offer to create a
    /// support ticket" or "Always ask for the study ID before querying". Null = no extra instruction.
    /// </summary>
    public string? AgenticInstruction { get; set; }

    /// <summary>
    /// Controls how this domain's knowledge bases are exposed in the system prompt:
    ///   "inline"  (default) — full column list (with definitions) is injected for every dataset KB.
    ///   "catalog"           — only table names + row counts are listed; the model calls kb_schema
    ///                         on demand to fetch a table's columns. Use for domains with many/wide
    ///                         datasets where an inline dump would bloat the prompt. Null = "inline".
    /// </summary>
    public string? KbPromptMode { get; set; }

    /// <summary>
    /// CDO Gateway module key gating this domain. Null means unmapped — access is then
    /// governed by Ava:Security:UnmappedDomainPolicy ("open" during rollout, "denied" after).
    /// Ignored for child domains (<see cref="ParentId"/> set) — those are gated by
    /// <see cref="DomainGrant"/> rows on the parent instead, since CDS can't scale to a
    /// module key per child (e.g. per study).
    /// </summary>
    public string? ModuleKey { get; set; }

    /// <summary>
    /// Id of the parent domain, or null for a top-level domain. Nesting is capped at 2
    /// tiers — a domain with a ParentId must not itself have children.
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>Identifier in the source system for a child domain — e.g. the study ID. Null for top-level domains.</summary>
    public string? ExternalRef { get; set; }
}

// ── Service config ───────────────────────────────────────────────────────────

public class ServiceEndpoint
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

// ── Streaming events ─────────────────────────────────────────────────────────

public class StreamEvent
{
    public string Type { get; set; } = string.Empty;
    // types: run_started | stage_start | step_start | step_done |
    //        token | user_input_required | confirmation_required | done | error |
    //        welcome | status | undo | tool_call | tool_result | tool_error
    public object? Data { get; set; }
}

// ── Usage analytics ──────────────────────────────────────────────────────────

/// <summary>
/// Persisted to the usage_events Postgres table on every completed agent run or
/// PromptController.Execute call. Used for cost tracking and audit.
/// </summary>
public record UsageEvent(
    string AgentRunId,
    string? ConversationId,
    string? SessionId,
    string UserId,
    string Username,
    string Domain,
    string? ModelId,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    decimal EstimatedCostUsd,
    string? PromptId,
    DateTime CreatedAt);

/// <summary>Summary returned by GET /api/usage.</summary>
public class UsageSummary
{
    public int TotalRuns { get; set; }
    public int TotalTokens { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public decimal TotalCostUsd { get; set; }
    public Dictionary<string, ModelUsageSummary> ByModel { get; set; } = new();
    public Dictionary<string, DomainUsageSummary> ByDomain { get; set; } = new();
}

public class ModelUsageSummary
{
    public int Runs { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal CostUsd { get; set; }
}

public class DomainUsageSummary
{
    public int Runs { get; set; }
    public int TotalTokens { get; set; }
    public decimal CostUsd { get; set; }
}

// ── Conversation archive models ───────────────────────────────────────────────

/// <summary>Postgres-backed conversation record for traceability / audit.</summary>
public record ConversationRecord(
    string Id,
    string? SessionId,
    string UserId,
    string Username,
    string Domain,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Postgres-backed message record — one row per user or assistant turn.</summary>
public record ConversationMessageRecord(
    long Id,
    string ConversationId,
    string? AgentRunId,
    string Role,
    string Content,
    DateTime CreatedAt);

// ── Knowledge Base models ────────────────────────────────────────────────────

public class KnowledgeBase
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>"dataset" or "document"</summary>
    public string Type { get; set; } = "dataset";
    /// <summary>"domain" or "session"</summary>
    public string Scope { get; set; } = "domain";
    public string? DomainId { get; set; }
    public string? SessionId { get; set; }
    public string? ConversationId { get; set; }
    public KBSource Source { get; set; } = new();
    public int? RowCount { get; set; }
    public int? ChunkCount { get; set; }
    /// <summary>Column definitions (datasets only). Stored at creation time from upload response.</summary>
    public List<KBColumnInfo>? Schema { get; set; }
    /// <summary>2-3 sample rows (datasets only). Stored at creation time from upload response.</summary>
    public List<Dictionary<string, object?>>? SampleRows { get; set; }
    /// <summary>Rich column metadata for system prompt injection (label, description, dataType per column).</summary>
    public Dictionary<string, KBColumnDefinition>? ColumnDefinitions { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    /// <summary>When the KB data was last fetched from its source endpoint.</summary>
    public DateTime? LastRefreshedAt { get; set; }
    /// <summary>Parquet file path (datasets) or FTS DB path (documents).</summary>
    public string? DataPath { get; set; }
}

public class KBSource
{
    /// <summary>"static" | "api" | "upload"</summary>
    public string Kind { get; set; } = "static";
    public string? Endpoint { get; set; }
    /// <summary>Tool ID to re-execute when refreshing this KB from its source.</summary>
    public string? ToolId { get; set; }
}

public class KBColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

/// <summary>Rich semantic metadata for a KB column — injected into the system prompt.</summary>
public class KBColumnDefinition
{
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
}

public class StoreAsKBConfig
{
    public bool Enabled { get; set; }
    public string KbName { get; set; } = string.Empty;
    public string Scope { get; set; } = "session";
}

// ── Conversation snapshots ───────────────────────────────────────────────────

public class ConversationSnapshot
{
    public List<ConversationMessage> Messages { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// ── Vector cache models ──────────────────────────────────────────────────────

public class VectorCacheStats
{
    public bool Ready { get; set; }
    public bool EmbeddingsDisabled { get; set; }
    public int IndexSize { get; set; }
    public int CachedEmbeddings { get; set; }
    public int OrphanedEmbeddings { get; set; }
    public int PromptCount { get; set; }
}

public class VectorRebuildResult
{
    public string Status { get; set; } = string.Empty;
    public int PreviousCount { get; set; }
    public int NewCount { get; set; }
    public int DurationMs { get; set; }
}

// ── Domain context files ─────────────────────────────────────────────────────

public class DomainFile
{
    public string Id { get; set; } = string.Empty;
    public string DomainId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public string ContentType { get; set; } = "general";
    public long FileSizeBytes { get; set; }
    public int ChunkCount { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public bool Enabled { get; set; } = true;
}

public class DomainFileChunk
{
    public string Id { get; set; } = string.Empty;
    public string ChunkId { get => Id; set => Id = value; }
    public string Content { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public float Score { get; set; }
    public string? ParentId { get; set; }
    public string? ChunkLevel { get; set; }
}

public class SearchDocsToolDefinition
{
    public string DomainId { get; set; } = string.Empty;
    public int MaxIterations { get; set; } = 5;
    public int TokenBudget { get; set; } = 8000;
}

// ── Module access (mirrors the CDO Gateway module contract) ──────────────────

/// <summary>
/// A permission grant returned by the CDO Gateway (<c>user/app/{appKey}/modules</c>).
/// Property names match the gateway's PascalCase JSON payload.
/// </summary>
public class Module
{
    public string ModuleKey { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Browse { get; set; }
    public bool Update { get; set; }
    public bool Insert { get; set; }
    public bool Delete { get; set; }

    public bool Has(ModuleAccess access) => access switch
    {
        ModuleAccess.Browse => Browse,
        ModuleAccess.Update => Update,
        ModuleAccess.Insert => Insert,
        ModuleAccess.Delete => Delete,
        _ => false
    };
}

public enum ModuleAccess
{
    Browse,
    Update,
    Insert,
    Delete
}

/// <summary>
/// App-level module keys registered against AVA's appKey in CDS. Domain and sub-domain
/// keys are deliberately NOT here — domains are database rows and studies are dynamic,
/// so those keys are resolved at runtime from <c>domains.module_key</c>.
/// </summary>
public static class ModuleKeys
{
    public const string SuperUser       = "MresjWO";
    public const string DomainAdmin     = "MoU6LMc";
    public const string WorkflowBuilder = "MwdTPIB";
    public const string ToolManager     = "MO3OxHd";
    public const string DataTemplates   = "MCT5pUl";
    public const string UsageAnalytics  = "Mqr9XUr";
}

/// <summary>Identity of the caller, resolved from the CAS session (or the local dev fallback).</summary>
public class CurrentUser
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }

    /// <summary>True when the identity came from a local-dev fallback rather than a real CAS token.</summary>
    public bool IsLocal { get; set; }
}

/// <summary>
/// AVA-native access grant for a child domain. Children are too numerous (50+ studies per
/// domain) to register individually with the CDO Gateway, so grants live here and are
/// AND-ed with the parent domain's gateway module.
/// </summary>
public class DomainGrant
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The top-level domain this grant applies under.</summary>
    public string ParentId { get; set; } = string.Empty;

    /// <summary>The specific child domain this grant covers, or null for a wildcard (every child of ParentId).</summary>
    public string? DomainId { get; set; }

    /// <summary>"user" or "group".</summary>
    public string PrincipalType { get; set; } = "user";
    public string PrincipalId { get; set; } = string.Empty;

    public bool Browse { get; set; } = true;
    public bool Update { get; set; }
    public bool Insert { get; set; }
    public bool Delete { get; set; }

    public const string PrincipalUser = "user";
    public const string PrincipalGroup = "group";

    public bool Has(ModuleAccess access) => access switch
    {
        ModuleAccess.Browse => Browse,
        ModuleAccess.Update => Update,
        ModuleAccess.Insert => Insert,
        ModuleAccess.Delete => Delete,
        _ => false
    };
}

/// <summary>Payload for GET /api/security/context — everything the Angular apps need to gate UI.</summary>
public class SecurityContext
{
    public CurrentUser User { get; set; } = new();
    public List<Module> Modules { get; set; } = new();
    public bool IsSuperUser { get; set; }

    /// <summary>Domain IDs the user can browse, with the module key each resolved through.</summary>
    public List<DomainAccessInfo> Domains { get; set; } = new();
}

public class DomainAccessInfo
{
    public string DomainId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ModuleKey { get; set; }
    public bool Browse { get; set; }
    public bool Update { get; set; }
    public bool Insert { get; set; }
    public bool Delete { get; set; }

    /// <summary>Child domain IDs the user can browse. Empty when the domain has no children.</summary>
    public List<string> ChildDomainIds { get; set; } = new();

    /// <summary>True when a wildcard grant covers every child of this domain.</summary>
    public bool AllChildDomains { get; set; }
}
