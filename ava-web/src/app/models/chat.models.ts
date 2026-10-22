// ── Request / response models matching ava-api ────────────────────────────────

export interface UserContext {
  domain: string;
  roles: string[];
  userId: string;
  /** Sub-domain (e.g. study) scope within the domain. Omitted when the whole domain is in scope. */
  subDomain?: string;
}

// ── Module access ───────────────────────────────────────────────────────────
// Mirrors ava-orchestration-ui's equivalent models — the two apps share no library yet, so
// the definitions are duplicated deliberately (see docs/module-access.md).

export interface Module {
  moduleKey: string;
  description: string;
  browse: boolean;
  update: boolean;
  insert: boolean;
  delete: boolean;
}

export enum ModuleAccess {
  BROWSE = 'browse',
  UPDATE = 'update',
  INSERT = 'insert',
  DELETE = 'delete',
}

export interface DomainAccessInfo {
  domainId: string;
  name: string;
  moduleKey?: string | null;
  browse: boolean;
  update: boolean;
  insert: boolean;
  delete: boolean;
  childDomainIds: string[];
  allChildDomains: boolean;
}

export interface CurrentUser {
  userId: string;
  displayName: string;
  email?: string | null;
  isLocal: boolean;
}

/** Payload of GET /api/security/context. */
export interface SecurityContext {
  user: CurrentUser;
  modules: any[];
  isSuperUser: boolean;
  domains: DomainAccessInfo[];
}

export interface DomainDefinition {
  id: string;
  name: string;
  description: string;
  owner: string;
  enabled: boolean;
  /** Id of the parent domain, or null/undefined for a top-level domain. Nesting is capped at 2 tiers. */
  parentId?: string | null;
  /** Identifier in the source system for a child domain — e.g. the study ID. */
  externalRef?: string | null;
  knowledgeBaseIds?: string[];
  suggestions?: string[];
  welcomeMessage?: string | null;
  agenticInstruction?: string | null;
}

export interface ChatRequest {
  message: string;
  conversationId: string;
  context: UserContext;
  /** Set when resuming a paused staged workflow. */
  resumeRunId?: string;
  /** Session ID for GitHub Copilot / ADO PAT lookup. */
  sessionId?: string;
}

export interface SelectRequest {
  agentRunId: string;
  /** select | confirm | dismiss */
  inputType: string;
  selectedIndex?: number;
  confirmedData?: Record<string, unknown>;
}

// ── Streaming event types ─────────────────────────────────────────────────────

export interface StreamEvent {
  type: 'run_started' | 'stage_start' | 'plan' | 'step_start' | 'step_done' |
        'user_input_required' | 'confirmation_required' | 'token' | 'done' | 'error' |
        'welcome' | 'status' | 'undo' | 'tool_call' | 'tool_result' | 'tool_error' |
        'rewindable_stages' | 'selection_summary' | 'workflow_builder' | 'save_confirmation';
  data: unknown;
}

export interface PlanStep {
  stepId: string;
  toolName: string;
  parameters: Record<string, unknown>;
  rationale: string;
  requiresConfirmation: boolean;
}

export interface StepExecution {
  stepId: string;
  toolName: string;
  success: boolean;
  result?: string;
  error?: string;
  durationMs: number;
}

// ── UI-only chat message model ────────────────────────────────────────────────

export type MessageRole = 'user' | 'assistant' | 'system';

export interface SelectionOption {
  label: string;
  value: string;
}

export interface SelectionPayload {
  prompt: string;
  options: SelectionOption[];
  stageId: string;
}

export interface ChatMessage {
  id: string;
  role: MessageRole;
  content: string;
  timestamp: Date;
  agentRunId?: string;
  plan?: PlanStep[];
  steps?: StepExecution[];
  isStreaming?: boolean;
  /** For user_input_required with type=select */
  selectionPayload?: SelectionPayload;
  inputType?: string;
  /** Tool calls and results displayed inline */
  toolCalls?: ToolCallEvent[];
  /** Welcome card data */
  welcome?: WelcomeData;
  /** Current status indicator text */
  statusText?: string;
  /** Revision counter — messages from an older revision are superseded after rewind */
  revision?: number;
  /** When true, this message has been superseded by a rewind and should be grayed out */
  superseded?: boolean;
  /** When true, the selection in this message has already been made */
  selectionConsumed?: boolean;
  /** For UI: search filter text for selection options */
  selectionFilter?: string;
  /** For UI: whether to show all options (expand) */
  selectionExpanded?: boolean;
  /** Workflow builder panel data (for ava-prompts interactive builder) */
  workflowBuilderPayload?: WorkflowBuilderPayload;
  /** Save confirmation panel data */
  saveConfirmationPayload?: SaveConfirmationPayload;
  /** When true, the save confirmation has been acted upon */
  saveConsumed?: boolean;
  /** Diagnostics footer showing tools, workflows, and domain files used */
  diagnostics?: RunDiagnostics;
  /** Reproducible CSV exports (SQL-backed) offered as download links */
  tableExports?: TableExport[];
}

// ── New event data types ──────────────────────────────────────────────────────

export interface WelcomeData {
  domain: string;
  domainName: string;
  message: string;
  suggestions: string[];
  knowledgeBases: { id: string; name: string; type: string; rowCount?: number; chunkCount?: number }[];
}

export interface ToolCallEvent {
  tool: string;
  params?: string;
  result?: string;
  error?: string;
  isError?: boolean;
}

/** A reproducible CSV export tied to a successful kb_query (SQL-backed). */
export interface TableExport {
  queryId: string;
  sql: string;
  datasetIds: string[];
  columns: string[];
  rowCount: number;
  /** True when the in-chat query hit the 1000-row cap; the CSV export is still full. */
  truncated: boolean;
  /** True when a trailing LIMIT/OFFSET was stripped — the CSV has more rows than rowCount. */
  limitRemoved: boolean;
  /** Relative API path (appended to apiUrl) to download the full CSV. */
  downloadUrl: string;
}

export interface StatusData {
  state: string;
  detail?: string;
}

// ── Stage rewind / breadcrumb models ──────────────────────────────────────────

export interface SelectionBreadcrumb {
  stageId: string;
  label: string;
  selectedValue: string;
  rewindable: boolean;
  revision: number;
}

export interface RewindableStage {
  stageId: string;
  label: string;
}

// ── Workflow builder panel models ─────────────────────────────────────────────

export interface WorkflowBuilderPayload {
  /** Pre-filled prompt definition data from the AI design stage */
  prompt: PromptDefinitionData;
  /** Existing prompts that matched the user's goal (for reuse/cloning) */
  similarPrompts?: SimilarPromptMatch[];
  /** Available domains for the domain picker */
  availableDomains?: string[];
  /** Available templates that can be referenced */
  availableTemplates?: TemplateRef[];
}

export interface PromptDefinitionData {
  id: string;
  /** @deprecated Use `domains` instead. */
  domain: string;
  /** List of domains this prompt applies to. Takes precedence over `domain`. */
  domains?: string[];
  description: string;
  /** Example prompt shown on suggestion cards. Falls back to description if empty. */
  samplePrompt?: string;
  contextTags: string[];
  systemPrompt: string;
  piiCheck: boolean;
  stages: WorkflowStageData[];
}

/** Lightweight suggestion card shown after domain selection. */
export interface WorkflowSuggestion {
  id: string;
  description: string;
  starterPrompt: string;
  icon: string;
}

export interface WorkflowStageData {
  stageId: string;
  type: string;
  prompt?: string;
  toolId?: string;
  templateRef?: string;
  required?: string[];
  parameters?: Record<string, string>;
  outputMap?: Record<string, string>;
  selectPrompt?: string;
  optionsFrom?: string;
  confirmPrompt?: string;
  /** Sub-stages for foreach type */
  stages?: WorkflowStageData[];
  /** foreach: dot-path to the array to iterate */
  itemsFrom?: string;
  /** foreach: alias for the current item (e.g. "feature") */
  itemAlias?: string;
  /** foreach: whether to pause for confirmation each iteration */
  confirmEach?: boolean;
  [key: string]: unknown;
}

export interface SimilarPromptMatch {
  prompt: PromptDefinitionData;
  score: number;
}

export interface TemplateRef {
  id: string;
  description: string;
}

export interface SaveConfirmationPayload {
  /** The artifacts to be saved */
  artifacts: SaveArtifact[];
  /** The agent run ID for resuming after confirmation */
  agentRunId: string;
}

export interface SaveArtifact {
  type: 'prompt' | 'template' | 'domain';
  id: string;
  action: 'create' | 'update';
  data: unknown;
}

export interface DomainDefinitionData {
  id: string;
  name: string;
  description: string;
  owner: string;
  enabled: boolean;
  knowledgeBaseIds?: string[];
  suggestions?: string[];
  welcomeMessage?: string;
}

// ── Domain Knowledge Base (shown in chat KB status bar) ───────────────────────

export interface DomainKb {
  id: string;
  name: string;
  type: string;
  rowCount?: number;
  lastRefreshedAt?: string;
  source: { kind: string; toolId?: string };
}

// ── Diagnostics footer (debug visibility) ─────────────────────────────────────

export interface RunDiagnostics {
  toolsCalled: string[];
  workflowsExecuted: string[];
  domainFilesUsed: string[];
  retrievalStrategy?: string;
  routerUsedLlm?: boolean;
  routerConfidence?: number;
  searchIterations?: number;
}
