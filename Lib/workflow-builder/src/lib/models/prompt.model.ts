// ─── Workflow Stage Types ────────────────────────────────────────
// Mirrors the ava-api C# WorkflowStage model and prompts.json schema.
// Each stage type has a distinct configuration shape.

/** All possible stage types in a workflow */
export type StageType =
  | 'ai-extract'
  | 'tool'
  | 'ai-generate'
  | 'ai-analyze'
  | 'user-select'
  | 'confirmation'
  | 'foreach'
  | 'agentic';

/** Base fields shared by every stage */
export interface StageBase {
  stageId: string;
  type: StageType;
}

/** ai-extract: LLM extracts structured fields from user message */
export interface AiExtractStage extends StageBase {
  type: 'ai-extract';
  prompt: string;
  required?: string[];
  fields?: FieldDefinition[];
}

export interface FieldDefinition {
  name: string;
  type: 'string' | 'number' | 'boolean' | 'date';
  description?: string;
}

/** tool: Execute an API tool (fixed or dynamic selection) */
export interface ToolStage extends StageBase {
  type: 'tool';
  toolId?: string;
  parameters?: Record<string, string>;
  prePrompt?: PrePromptConfig;
  outputMap?: Record<string, string>;
}

export interface PrePromptConfig {
  prompt: string;
  allowedToolIds: string[];
  /** Fixed params (support {{token}}) merged on top of LLM-selected params. Static wins. */
  staticParams?: Record<string, string>;
}

/** ai-generate: LLM generates content using a template; pauses for confirmation */
export interface AiGenerateStage extends StageBase {
  type: 'ai-generate';
  prompt: string;
  templateRef?: string;
  rewindTo?: string;
}

/** ai-analyze: LLM analyzes tool results (non-pausing) */
export interface AiAnalyzeStage extends StageBase {
  type: 'ai-analyze';
  prompt: string;
  dataFrom?: string[];
}

/** user-select: Present options for user to choose from */
export interface UserSelectStage extends StageBase {
  type: 'user-select';
  prompt?: string;
  optionsFrom: string;
  labelField?: string;
}

/** confirmation: Ask yes/no question; pause for response */
export interface ConfirmationStage extends StageBase {
  type: 'confirmation';
  prompt: string;
  /** On correction, rewind to this stage id. Defaults to the previous stage. */
  rewindTo?: string;
}

/** foreach: Loop over array, executing sub-stages per item */
export interface ForEachStage extends StageBase {
  type: 'foreach';
  itemsFrom: string;
  itemAlias: string;
  confirmEach?: boolean;
  stages: WorkflowStage[];
}

/** agentic: Iterative LLM-driven tool calling with scoped MCP tools */
export interface AgenticStage extends StageBase {
  type: 'agentic';
  prompt: string;
  toolDomains?: string[];
  maxIterations?: number;
  outputField?: string;
}

/** Union of all stage types */
export type WorkflowStage =
  | AiExtractStage
  | ToolStage
  | AiGenerateStage
  | AiAnalyzeStage
  | UserSelectStage
  | ConfirmationStage
  | ForEachStage
  | AgenticStage;

// ─── Prompt Definition ──────────────────────────────────────────
// Mirrors prompts.json entries and ava-api PromptDefinition model.

export interface PromptDefinition {
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
  stages?: WorkflowStage[];

  // Foundry Toolbox-inspired metadata
  version?: string;
  createdBy?: string;
  updatedAt?: string;
  updatedBy?: string;
  changelog?: ChangelogEntry[];
}

export interface ChangelogEntry {
  version: string;
  timestamp: string;
  author: string;
  description: string;
}

/** Factory for creating an empty prompt definition */
export function createEmptyPrompt(domain: string = ''): PromptDefinition {
  return {
    id: '',
    domain,
    domains: domain ? [domain] : [],
    description: '',
    samplePrompt: '',
    contextTags: [],
    systemPrompt: '',
    piiCheck: false,
    stages: [],
  };
}

/** Factory for creating a stage with defaults */
export function createEmptyStage(type: StageType): WorkflowStage {
  const base = { stageId: `stage-${Date.now()}`, type };
  switch (type) {
    case 'ai-extract':
      return { ...base, type: 'ai-extract', prompt: '', required: [], fields: [] };
    case 'tool':
      return { ...base, type: 'tool' };
    case 'ai-generate':
      return { ...base, type: 'ai-generate', prompt: '' };
    case 'ai-analyze':
      return { ...base, type: 'ai-analyze', prompt: '' };
    case 'user-select':
      return { ...base, type: 'user-select', optionsFrom: '' };
    case 'confirmation':
      return { ...base, type: 'confirmation', prompt: '' };
    case 'foreach':
      return { ...base, type: 'foreach', itemsFrom: '', itemAlias: 'item', stages: [] };
    case 'agentic':
      return { ...base, type: 'agentic', prompt: '', toolDomains: [], maxIterations: 15, outputField: '' };
  }
}
