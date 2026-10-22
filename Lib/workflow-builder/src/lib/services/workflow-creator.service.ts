import { Injectable, inject, signal, computed } from '@angular/core';
import { WorkflowApiService } from './workflow-api.service';
import { PromptDefinition, WorkflowStage, StageType, FieldDefinition } from '../models';

// ─── Creator Draft Models ─────────────────────────────────────────

export interface CreationDraft {
  metadata: CreationMetadata;
  stages: CreationStageCard[];
  dataFlow: DataFlowConnection[];
}

export interface CreationMetadata {
  id: string;
  domain: string;
  domains: string[];
  description: string;
  samplePrompt: string;
  contextTags: string[];
  systemPrompt: string;
  piiCheck: boolean;
}

export interface CreationStageCard {
  stageId: string;
  type: StageType;
  prompt?: string;
  toolId?: string;
  required?: string[];
  fields?: FieldDefinition[];
  parameters?: Record<string, string>;
  outputMap?: Record<string, string>;
  prePrompt?: { prompt: string; allowedToolIds: string[] };
  selectPrompt?: string;
  optionsFrom?: string;
  labelField?: string;
  templateRef?: string;
  rewindTo?: string;
  dataFrom?: string[];
  // agentic config
  toolDomains?: string[];
  maxIterations?: number;
  outputField?: string;
  // foreach (loop) fields — supported one level deep (no foreach-in-foreach)
  itemsFrom?: string;
  itemAlias?: string;
  confirmEach?: boolean;
  subStages?: CreationStageCard[];
  isNew?: boolean;
  isUpdated?: boolean;
}

export interface DataFlowConnection {
  fromStageId: string;
  toStageId: string;
  fieldName: string;
  tokenRef: string;
}

export interface CreatorMessage {
  role: 'user' | 'assistant';
  content: string;
  actions?: CreatorAction[];
  timestamp: Date;
}

export interface CreatorAction {
  action: string;
  payload: any;
}

export interface CoherenceWarning {
  stageId: string;
  type: 'unresolved_token' | 'orphaned_tool';
  message: string;
  tokenRef?: string;
}

export interface SimilarWorkflowMatch {
  id: string;
  domain: string;
  description: string;
  score: number;
  stageCount: number;
  stages?: { stageId: string; type: string }[];
}

// ─── Starter Suggestions ──────────────────────────────────────────

export interface StarterSuggestion {
  icon: string;
  text: string;
}

export const DEFAULT_SUGGESTIONS: StarterSuggestion[] = [
  {
    icon: '🔍',
    text: 'Search for work items by area path and show results in a table',
  },
  {
    icon: '✏️',
    text: 'Create a new PBI with title, description, and acceptance criteria',
  },
  {
    icon: '🔄',
    text: 'Query an external API, let the user pick from the results, then take action',
  },
  {
    icon: '🤖',
    text: 'Something like the epic creator but for a different work item type',
  },
  {
    icon: '📊',
    text: 'Fetch data from an API and summarize it with AI',
  },
];

export const EDIT_SUGGESTIONS: StarterSuggestion[] = [
  {
    icon: '🔁',
    text: 'Add a foreach stage that creates an ADO task under each PBI in the current sprint',
  },
  {
    icon: '✅',
    text: 'Add a confirmation step before the write action',
  },
  {
    icon: '🧩',
    text: 'Add an outputMap field to the create stage so the next stage can use it',
  },
  {
    icon: '📊',
    text: 'Add a final ai-analyze stage that summarizes the results for the user',
  },
];

// ─── Service ──────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class WorkflowCreatorService {
  private readonly api = inject(WorkflowApiService);

  // ── State ───────────────────────────────────────────────────────
  readonly draft = signal<CreationDraft>(emptyDraft());
  readonly messages = signal<CreatorMessage[]>([]);
  readonly conversationId = signal<string>(crypto.randomUUID());
  readonly isAgentThinking = signal(false);
  readonly similarWorkflows = signal<SimilarWorkflowMatch[]>([]);
  readonly isActive = signal(false);
  readonly pendingToolRegistrations = signal<any[]>([]);

  /** Warnings about data-flow coherence (unresolved token references, etc.) */
  readonly coherenceWarnings = signal<CoherenceWarning[]>([]);

  /** Session mode: building a new workflow vs editing an existing one. */
  readonly mode = signal<'create' | 'edit'>('create');

  /** When editing, the id of the workflow being edited (identity is locked to this on save). */
  readonly editingId = signal<string | null>(null);

  /** Version captured when an edit session starts, for optimistic concurrency on save. */
  readonly originalVersion = signal<string | undefined>(undefined);

  // Edit buffer — accumulated between messages
  private editBuffer: string[] = [];

  // Abort controller for cancelling in-flight requests
  private abortController: AbortController | null = null;

  // ── Computed ────────────────────────────────────────────────────

  /** Convert the draft to a PromptDefinition for saving/testing */
  readonly asPromptDefinition = computed<PromptDefinition>(() => {
    const d = this.draft();
    return {
      id: d.metadata.id,
      domain: d.metadata.domain,
      domains: d.metadata.domains,
      description: d.metadata.description,
      samplePrompt: d.metadata.samplePrompt,
      contextTags: d.metadata.contextTags,
      systemPrompt: d.metadata.systemPrompt,
      piiCheck: d.metadata.piiCheck,
      stages: d.stages.map(s => this.stageCardToWorkflowStage(s)),
    };
  });

  readonly hasContent = computed(() => {
    const d = this.draft();
    return d.metadata.id !== '' || d.stages.length > 0;
  });

  /** Whether the draft has the required fields to be saved (systemPrompt is required). */
  readonly canSave = computed(() => !!this.draft().metadata.systemPrompt?.trim());

  // ── Public Methods ──────────────────────────────────────────────

  /** Start a new creation session */
  startSession(): void {
    this.draft.set(emptyDraft());
    this.messages.set([]);
    this.conversationId.set(crypto.randomUUID());
    this.similarWorkflows.set([]);
    this.pendingToolRegistrations.set([]);
    this.editBuffer = [];
    this.mode.set('create');
    this.editingId.set(null);
    this.originalVersion.set(undefined);
    this.isActive.set(true);
  }

  /**
   * Start an edit session seeded from an existing workflow. The draft is
   * populated from the prompt and the workflow identity/version is captured.
   */
  startEditSession(prompt: PromptDefinition): void {
    this.draft.set(this.promptToDraft(prompt));
    this.messages.set([]);
    this.conversationId.set(crypto.randomUUID());
    this.similarWorkflows.set([]);
    this.pendingToolRegistrations.set([]);
    this.editBuffer = [];
    this.mode.set('edit');
    this.editingId.set(prompt.id);
    this.originalVersion.set(prompt.version);
    this.isActive.set(true);
    this.recalculateDataFlow();
    this.validateStageCoherence();
  }

  /** End creation session */
  endSession(): void {
    this.cancelRequest();
    this.isActive.set(false);
  }

  /** Post an assistant chat message explaining why the workflow can't be saved yet. */
  notifyMissingSystemPrompt(): void {
    this.postAssistantMessage(
      'This workflow needs a system prompt before it can be saved — it tells the AI what instructions to follow throughout the workflow. Let me know what the workflow should do and I\'ll add one.'
    );
  }

  /** Append an assistant message to the chat transcript (used for save/validation feedback). */
  postAssistantMessage(content: string): void {
    this.messages.update(msgs => [
      ...msgs,
      { role: 'assistant', content, timestamp: new Date() },
    ]);
  }

  /** Send a message to the creator agent */
  async sendMessage(text: string): Promise<void> {
    const editing = this.mode() === 'edit';

    // Confirmation interception:
    // - Create mode: any confirmation once stages exist → point user at Save (re-sending would duplicate stages).
    // - Edit mode: only an EXPLICIT save intent is intercepted; generic "yes"/"looks good" flows to the
    //   agent so the user can keep iterating on edits without prematurely ending the conversation.
    const shouldIntercept = this.draft().stages.length > 0 &&
      (editing ? this.isExplicitSaveIntent(text) : this.isConfirmationMessage(text));
    if (shouldIntercept) {
      this.messages.update(msgs => [
        ...msgs,
        { role: 'user', content: text, timestamp: new Date() },
        {
          role: 'assistant',
          content: editing
            ? 'Your changes are ready! Click the "Save Changes" button in the top-right corner to overwrite the workflow.'
            : 'Your workflow is ready! When you\'re ready, click the "Save & Open" button in the top-right corner to save it.',
          timestamp: new Date(),
        },
      ]);
      return;
    }

    // Add user message to chat
    this.messages.update(msgs => [
      ...msgs,
      { role: 'user', content: text, timestamp: new Date() },
    ]);

    this.isAgentThinking.set(true);
    this.abortController = new AbortController();

    try {
      const response = await fetch(
        `${this.api.getBaseUrl()}/api/chat/create-workflow`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
          body: JSON.stringify({
            message: text,
            conversationId: this.conversationId(),
            userId: 'workflow-creator',
            mode: this.mode(),
            editingWorkflowId: this.editingId(),
            conversationHistory: this.messages()
              .filter(m => m.role === 'user' || m.role === 'assistant')
              .slice(0, -1) // exclude the current message (already in `message`)
              .map(m => ({ role: m.role, content: m.content })),
            currentDraft: this.hasContent() ? this.draft() : null,
            editBuffer: this.editBuffer.length > 0 ? this.editBuffer : null,
          }),
          signal: this.abortController.signal,
        }
      );

      // Clear edit buffer after sending
      this.editBuffer = [];

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      // Process SSE stream
      await this.processStream(response);
    } catch (err: any) {
      if (err.name !== 'AbortError') {
        this.messages.update(msgs => [
          ...msgs,
          { role: 'assistant', content: `Error: ${err.message}`, timestamp: new Date() },
        ]);
      }
    } finally {
      this.isAgentThinking.set(false);
      this.abortController = null;
    }
  }

  /** Record a user edit for context injection on next message */
  applyUserEdit(stageId: string | null, field: string, value: any): void {
    const target = stageId ? `stage "${stageId}".${field}` : `metadata.${field}`;
    const preview = typeof value === 'string' && value.length > 50
      ? value.slice(0, 50) + '...'
      : JSON.stringify(value);
    this.editBuffer.push(`Changed ${target} to ${preview}`);

    // Also apply directly to the draft (recursively, so nested foreach sub-stages are supported)
    if (stageId) {
      this.draft.update(d => ({
        ...d,
        stages: updateCardInTree(d.stages, stageId, { [field]: value, isUpdated: false } as Partial<CreationStageCard>),
      }));
    } else {
      this.draft.update(d => ({
        ...d,
        metadata: { ...d.metadata, [field]: value },
      }));
    }
  }

  /** Cancel any in-flight request */
  cancelRequest(): void {
    this.abortController?.abort();
    this.isAgentThinking.set(false);
  }

  /** Remove a stage (or foreach sub-stage) by id, recursively, and record the edit. */
  removeStageById(stageId: string): void {
    this.noteEdit(`Removed stage "${stageId}"`);
    this.draft.update(d => ({ ...d, stages: removeCardFromTree(d.stages, stageId) }));
    this.recalculateDataFlow();
    this.validateStageCoherence();
  }

  /** Append a human-readable note to the edit buffer (injected as context on the next message). */
  noteEdit(note: string): void {
    this.editBuffer.push(note);
  }

  /**
   * Detect if a user message is confirming the current draft (e.g., "yes create it").
   * Uses Levenshtein fuzzy matching to catch typos like "crate it" or "confrim".
   */
  private isConfirmationMessage(text: string): boolean {
    const normalized = text.trim().toLowerCase();

    // Exact regex for common confirmations (fast path)
    if (/^(yes|yep|yeah|yea|y|looks good|create it|save it|confirm|lgtm|do it|go ahead|proceed|approve|ship it|perfect|that's? (good|great|perfect|correct)|go for it)/i.test(normalized)) {
      return true;
    }

    // Fuzzy matching: score each word against confirmation keywords (catches typos)
    const confirmWords = [
      'yes', 'yep', 'yeah', 'create', 'save', 'confirm', 'approve',
      'proceed', 'perfect', 'lgtm', 'good', 'great', 'correct',
    ];
    const inputWords = normalized.split(/\s+/).slice(0, 6); // only check first 6 words

    let matchCount = 0;
    for (const word of inputWords) {
      if (word.length < 2) continue;
      for (const target of confirmWords) {
        if (this.levenshtein(word, target) <= 2) {
          matchCount++;
          break;
        }
      }
    }

    // If the message is short (≤6 words) and at least one word fuzzy-matches a confirmation keyword,
    // and the message doesn't contain negation or change-request signals, treat as confirmation
    const hasNegation = /\b(but|change|add|remove|update|instead|actually|wait|no|don't|not)\b/.test(normalized);
    return matchCount > 0 && inputWords.length <= 6 && !hasNegation;
  }

  /**
   * Detect an EXPLICIT save intent (edit mode only). Unlike isConfirmationMessage,
   * this does NOT match generic "yes"/"looks good" — only clear save/overwrite phrases —
   * so users can keep iterating on edits without prematurely triggering save guidance.
   */
  private isExplicitSaveIntent(text: string): boolean {
    const normalized = text.trim().toLowerCase();
    return /\b(save (it|this|changes|the workflow|now)|save and (open|close)|overwrite|apply (the )?changes|commit (the )?changes|persist)\b/.test(normalized)
      || /^save$/.test(normalized);
  }

  /** Compute Levenshtein (edit) distance between two strings */
  private levenshtein(a: string, b: string): number {
    const m = a.length, n = b.length;
    if (m === 0) return n;
    if (n === 0) return m;

    const dp: number[] = Array.from({ length: n + 1 }, (_, i) => i);

    for (let i = 1; i <= m; i++) {
      let prev = i - 1;
      dp[0] = i;
      for (let j = 1; j <= n; j++) {
        const temp = dp[j];
        dp[j] = a[i - 1] === b[j - 1]
          ? prev
          : 1 + Math.min(prev, dp[j], dp[j - 1]);
        prev = temp;
      }
    }
    return dp[n];
  }

  // ── Private: SSE Processing ─────────────────────────────────────

  private async processStream(response: Response): Promise<void> {
    const reader = response.body?.getReader();
    if (!reader) return;

    const decoder = new TextDecoder();
    let buffer = '';
    let assistantText = '';
    const actions: CreatorAction[] = [];

    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() || '';

        for (const line of lines) {
          if (!line.startsWith('data: ')) continue;
          const jsonStr = line.slice(6).trim();
          if (!jsonStr) continue;

          try {
            const event = JSON.parse(jsonStr);
            this.handleStreamEvent(event, actions, { assistantText: () => assistantText, setAssistantText: (t: string) => assistantText = t });
            assistantText = assistantText; // Keep in sync
          } catch {
            // Skip malformed JSON
          }
        }
      }
    } finally {
      reader.releaseLock();
    }

    // Add complete assistant message. When actions changed the draft without text,
    // summarize them so the chat does not render a blank assistant bubble.
    const content = assistantText.trim() || (actions.length > 0 ? this.summarizeActions(actions) : '');
    if (content.trim() || actions.length > 0) {
      this.messages.update(msgs => [
        ...msgs,
        { role: 'assistant', content, actions, timestamp: new Date() },
      ]);
    }

    const warnings = actions.length > 0
      ? this.coherenceWarnings().filter(w => w.type === 'unresolved_token')
      : [];
    if (warnings.length > 0) {
      const warningLines = warnings
        .slice(0, 5)
        .map(w => `- ${w.stageId}: ${w.message}`);
      const remainingCount = warnings.length - warningLines.length;
      const warningContent = [
        '⚠️ Some token references still need attention:',
        ...warningLines,
        ...(remainingCount > 0 ? [`- …and ${remainingCount} more`] : []),
      ].join('\n');

      this.messages.update(msgs => [
        ...msgs,
        { role: 'assistant', content: warningContent, timestamp: new Date() },
      ]);
    }
  }

  private summarizeActions(actions: CreatorAction[]): string {
    const counts = actions.reduce<Record<string, number>>((acc, action) => {
      acc[action.action] = (acc[action.action] || 0) + 1;
      return acc;
    }, {});

    const parts: string[] = [];
    const addCount = counts['add_stage'] || 0;
    const updateCount = counts['update_stage'] || 0;
    const removeCount = counts['remove_stage'] || 0;

    if (counts['clone_workflow']) {
      parts.push('cloned the workflow');
    }
    if (counts['set_metadata']) {
      parts.push('updated metadata');
    }
    if (addCount) {
      parts.push(`added ${addCount} stage${addCount === 1 ? '' : 's'}`);
    }
    if (updateCount) {
      parts.push(`updated ${updateCount} stage${updateCount === 1 ? '' : 's'}`);
    }
    if (removeCount) {
      parts.push(`removed ${removeCount} stage${removeCount === 1 ? '' : 's'}`);
    }
    if (counts['reorder_stages']) {
      parts.push('reordered stages');
    }
    if (counts['set_data_flow']) {
      parts.push('updated data flow');
    }
    if (counts['register_tool']) {
      const count = counts['register_tool'];
      parts.push(`queued ${count} tool registration${count === 1 ? '' : 's'}`);
    }
    if (counts['search_workflows']) {
      parts.push('searched similar workflows');
    }
    if (counts['validate_draft']) {
      parts.push('validated the draft');
    }

    if (parts.length === 0) {
      return actions.length === 1
        ? 'Applied 1 workflow change.'
        : `Applied ${actions.length} workflow changes.`;
    }

    return `Applied changes: ${parts.join(', ')}.`;
  }

  private handleStreamEvent(
    event: { type: string; data: any },
    actions: CreatorAction[],
    textRef: { assistantText: () => string; setAssistantText: (t: string) => void }
  ): void {
    switch (event.type) {
      case 'token':
        textRef.setAssistantText(textRef.assistantText() + (event.data || ''));
        break;

      case 'creator_action':
        this.handleCreatorAction(event.data.action, event.data.payload);
        actions.push({ action: event.data.action, payload: event.data.payload });
        break;

      case 'done':
        // Stream complete
        break;

      case 'error':
        textRef.setAssistantText(textRef.assistantText() + `\n\n⚠️ Error: ${event.data}`);
        break;
    }
  }

  private handleCreatorAction(action: string, payload: any): void {
    switch (action) {
      case 'set_metadata':
        this.draft.update(d => ({
          ...d,
          metadata: { ...d.metadata, ...payload },
        }));
        break;

      case 'add_stage': {
        const card = cardFromPayload(payload);
        const existing = findCardInTree(this.draft().stages, card.stageId);
        if (existing) {
          // Stage already exists anywhere in the tree — merge (treat as update)
          this.draft.update(d => ({
            ...d,
            stages: updateCardInTree(d.stages, card.stageId, { ...card, isUpdated: true }),
          }));
          setTimeout(() => this.clearUpdatedFlag(card.stageId), 1500);
        } else {
          const parentStageId: string | undefined = payload.parentStageId;
          const afterStageId: string | undefined = payload.afterStageId;
          // Duplicate-tool guard within the target scope only
          const scopeStages = parentStageId
            ? (findCardInTree(this.draft().stages, parentStageId)?.subStages ?? [])
            : this.draft().stages;
          if (card.type === 'tool' && card.toolId &&
              scopeStages.some(s => s.type === 'tool' && s.toolId === card.toolId)) {
            console.warn(`[Creator] Skipped duplicate tool stage: toolId="${card.toolId}" already exists in scope`);
          } else {
            this.draft.update(d => ({
              ...d,
              stages: insertCardInTree(d.stages, { ...card, isNew: true }, parentStageId, afterStageId),
            }));
            setTimeout(() => this.clearNewFlag(card.stageId), 1500);
          }
        }
        break;
      }

      case 'update_stage': {
        const { stageId, changes } = payload;
        const normalizedChanges = changes && Array.isArray(changes.stages)
          ? { ...changes, subStages: changes.stages.map(cardFromPayload), stages: undefined }
          : changes;
        this.draft.update(d => ({
          ...d,
          stages: updateCardInTree(d.stages, stageId, { ...normalizedChanges, isUpdated: true }),
        }));
        setTimeout(() => this.clearUpdatedFlag(stageId), 1500);
        break;
      }

      case 'remove_stage':
        this.draft.update(d => ({
          ...d,
          stages: removeCardFromTree(d.stages, payload.stageId),
        }));
        break;

      case 'reorder_stages': {
        const order: string[] = payload.stageIds;
        const parentStageId: string | undefined = payload.parentStageId;
        this.draft.update(d => ({
          ...d,
          stages: reorderCardsInScope(d.stages, order, parentStageId),
        }));
        break;
      }

      case 'search_workflows':
        if (payload.matches) {
          this.similarWorkflows.set(payload.matches);
        }
        break;

      case 'clone_workflow':
        if (payload.metadata) {
          this.draft.update(d => ({
            ...d,
            metadata: { ...d.metadata, ...payload.metadata },
            stages: (payload.stages || []).map((s: any) => ({ ...cardFromPayload(s), isNew: true })),
          }));
        }
        break;

      case 'set_data_flow':
        if (payload.connections) {
          this.draft.update(d => ({
            ...d,
            dataFlow: payload.connections,
          }));
        }
        break;

      case 'register_tool':
        this.pendingToolRegistrations.update(regs => [...regs, payload]);
        break;

      case 'validate_draft':
        // Validation results are communicated via text, no draft changes needed
        break;
    }

    this.recalculateDataFlow();
    this.validateStageCoherence();
  }

  private clearNewFlag(stageId: string): void {
    this.draft.update(d => ({
      ...d,
      stages: updateCardInTree(d.stages, stageId, { isNew: false }),
    }));
  }

  private clearUpdatedFlag(stageId: string): void {
    this.draft.update(d => ({
      ...d,
      stages: updateCardInTree(d.stages, stageId, { isUpdated: false }),
    }));
  }

  /** Auto-detect data flow from {{token}} references in stage prompts/parameters (foreach-scoped) */
  private recalculateDataFlow(): void {
    const connections: DataFlowConnection[] = [];
    const tokenPattern = /\{\{([^}]+)\}\}/g;

    // Global field -> producing stageId map (walks the whole tree)
    const fieldProducers = new Map<string, string>();
    const collect = (stages: CreationStageCard[]): void => {
      for (const stage of stages) {
        if (stage.type === 'ai-extract' && stage.required) {
          stage.required.forEach(f => { if (!fieldProducers.has(f)) fieldProducers.set(f, stage.stageId); });
        }
        if (stage.type === 'tool') {
          fieldProducers.set(`_result_${stage.stageId}`, stage.stageId);
        }
        if (stage.outputMap) {
          Object.keys(stage.outputMap).forEach(k => { if (!fieldProducers.has(k)) fieldProducers.set(k, stage.stageId); });
        }
        if (stage.type === 'ai-generate') {
          fieldProducers.set('_generatedContent', stage.stageId);
        }
        if (stage.type === 'foreach' && stage.subStages) {
          collect(stage.subStages);
        }
      }
    };
    collect(this.draft().stages);

    // aliasProducers maps an in-scope loop alias -> the foreach stage that produces it
    const scan = (stages: CreationStageCard[], aliasProducers: Map<string, string>): void => {
      for (const stage of stages) {
        const texts = [
          stage.prompt || '',
          ...(stage.parameters ? Object.values(stage.parameters) : []),
          stage.selectPrompt || '',
        ];
        for (const text of texts) {
          let match: RegExpExecArray | null;
          while ((match = tokenPattern.exec(text)) !== null) {
            const fieldName = match[1];
            const root = fieldName.split('.')[0];
            const producer = fieldProducers.get(fieldName) ?? aliasProducers.get(root);
            if (producer && producer !== stage.stageId) {
              connections.push({ fromStageId: producer, toStageId: stage.stageId, fieldName, tokenRef: `{{${fieldName}}}` });
            }
          }
        }
        if (stage.type === 'foreach') {
          const itemsField = (stage.itemsFrom || '').replace(/[{}]/g, '').trim();
          if (itemsField) {
            const producer = fieldProducers.get(itemsField);
            if (producer && producer !== stage.stageId) {
              connections.push({ fromStageId: producer, toStageId: stage.stageId, fieldName: itemsField, tokenRef: `{{${itemsField}}}` });
            }
          }
          const childAlias = new Map(aliasProducers);
          if (stage.itemAlias) childAlias.set(stage.itemAlias, stage.stageId);
          scan(stage.subStages || [], childAlias);
        }
      }
    };
    scan(this.draft().stages, new Map());

    this.draft.update(d => ({ ...d, dataFlow: connections }));
  }

  /**
   * Validate data-flow coherence after stage mutations (foreach-scoped).
   * Checks that all {{token}} references resolve to an upstream producer or an
   * in-scope loop alias. Loop aliases are only valid inside their foreach subtree.
   */
  private validateStageCoherence(): void {
    const warnings: CoherenceWarning[] = [];
    const tokenPattern = /\{\{([^}]+)\}\}/g;

    // Globally producible field names (alias names are intentionally excluded — they are scoped)
    const producibleFields = new Set<string>();
    const collect = (stages: CreationStageCard[]): void => {
      for (const stage of stages) {
        if (stage.type === 'ai-extract' && stage.required) {
          stage.required.forEach(f => producibleFields.add(f));
        }
        if (stage.type === 'tool') {
          producibleFields.add(`_result_${stage.stageId}`);
        }
        if (stage.outputMap) {
          Object.keys(stage.outputMap).forEach(k => producibleFields.add(k));
        }
        if (stage.type === 'ai-generate') {
          producibleFields.add('_generatedContent');
        }
        if (stage.type === 'foreach' && stage.subStages) {
          collect(stage.subStages);
        }
      }
    };
    collect(this.draft().stages);

    const check = (stages: CreationStageCard[], aliasScope: Set<string>): void => {
      for (const stage of stages) {
        const texts = [
          stage.prompt || '',
          ...(stage.parameters ? Object.values(stage.parameters) : []),
          stage.selectPrompt || '',
        ];
        for (const text of texts) {
          let match: RegExpExecArray | null;
          while ((match = tokenPattern.exec(text)) !== null) {
            const fieldName = match[1];
            const root = fieldName.split('.')[0];
            if (!producibleFields.has(fieldName) && !aliasScope.has(root)) {
              warnings.push({
                stageId: stage.stageId,
                type: 'unresolved_token',
                message: `Token "{{${fieldName}}}" has no upstream producer`,
                tokenRef: `{{${fieldName}}}`,
              });
            }
          }
        }
        if (stage.type === 'foreach') {
          const itemsField = (stage.itemsFrom || '').replace(/[{}]/g, '').trim();
          const itemsRoot = itemsField.split('.')[0];
          if (itemsField && !producibleFields.has(itemsField) && !aliasScope.has(itemsRoot)) {
            warnings.push({
              stageId: stage.stageId,
              type: 'unresolved_token',
              message: `ForEach itemsFrom "${itemsField}" has no upstream producer`,
              tokenRef: `{{${itemsField}}}`,
            });
          }
          const childScope = new Set(aliasScope);
          if (stage.itemAlias) childScope.add(stage.itemAlias);
          check(stage.subStages || [], childScope);
        }
      }
    };
    check(this.draft().stages, new Set());

    this.coherenceWarnings.set(warnings);
  }

  /** Convert a stage card to a WorkflowStage for the API */
  private stageCardToWorkflowStage(card: CreationStageCard): WorkflowStage {
    const base = { stageId: card.stageId, type: card.type };

    switch (card.type) {
      case 'ai-extract':
        return { ...base, type: 'ai-extract', prompt: card.prompt || '', required: card.required, fields: card.fields };
      case 'tool':
        return {
          ...base,
          type: 'tool',
          toolId: card.toolId,
          parameters: card.parameters,
          outputMap: card.outputMap,
          prePrompt: card.prePrompt,
        };
      case 'ai-generate':
        return { ...base, type: 'ai-generate', prompt: card.prompt || '', templateRef: card.templateRef, rewindTo: card.rewindTo };
      case 'ai-analyze':
        return { ...base, type: 'ai-analyze', prompt: card.prompt || '', dataFrom: card.dataFrom };
      case 'user-select':
        return { ...base, type: 'user-select', optionsFrom: card.optionsFrom || '', prompt: card.selectPrompt || card.prompt, labelField: card.labelField };
      case 'confirmation':
        return { ...base, type: 'confirmation', prompt: card.prompt || '', rewindTo: card.rewindTo };
      case 'foreach':
        return {
          ...base,
          type: 'foreach',
          itemsFrom: card.itemsFrom || '',
          itemAlias: card.itemAlias || 'item',
          confirmEach: card.confirmEach,
          stages: (card.subStages || []).map(s => this.stageCardToWorkflowStage(s)),
        };
      case 'agentic':
        return {
          ...base,
          type: 'agentic',
          prompt: card.prompt || '',
          toolDomains: card.toolDomains,
          maxIterations: card.maxIterations,
          outputField: card.outputField,
        };
      default:
        return { ...base, type: 'ai-analyze', prompt: '' } as any;
    }
  }

  /**
   * Convert a saved PromptDefinition into a CreationDraft for editing.
   * Recurses foreach sub-stages so the round-trip is lossless.
   */
  promptToDraft(prompt: PromptDefinition): CreationDraft {
    return {
      metadata: {
        id: prompt.id ?? '',
        domain: prompt.domain ?? '',
        domains: prompt.domains ?? (prompt.domain ? [prompt.domain] : []),
        description: prompt.description ?? '',
        samplePrompt: prompt.samplePrompt ?? '',
        contextTags: prompt.contextTags ?? [],
        systemPrompt: prompt.systemPrompt ?? '',
        piiCheck: prompt.piiCheck ?? false,
      },
      stages: (prompt.stages ?? []).map(s => this.workflowStageToCard(s)),
      dataFlow: [],
    };
  }

  /** Convert a WorkflowStage (API shape) into a CreationStageCard, recursing foreach. */
  private workflowStageToCard(stage: WorkflowStage): CreationStageCard {
    const card: CreationStageCard = { stageId: stage.stageId, type: stage.type };
    switch (stage.type) {
      case 'ai-extract':
        card.prompt = stage.prompt;
        card.required = stage.required;
        card.fields = stage.fields;
        break;
      case 'tool':
        card.toolId = stage.toolId;
        card.parameters = stage.parameters;
        card.outputMap = stage.outputMap;
        card.prePrompt = stage.prePrompt;
        break;
      case 'ai-generate':
        card.prompt = stage.prompt;
        card.templateRef = stage.templateRef;
        card.rewindTo = stage.rewindTo;
        break;
      case 'ai-analyze':
        card.prompt = stage.prompt;
        card.dataFrom = stage.dataFrom;
        break;
      case 'user-select':
        card.optionsFrom = stage.optionsFrom;
        card.selectPrompt = stage.prompt;
        card.labelField = stage.labelField;
        break;
      case 'confirmation':
        card.prompt = stage.prompt;
        card.rewindTo = stage.rewindTo;
        break;
      case 'foreach':
        card.itemsFrom = stage.itemsFrom;
        card.itemAlias = stage.itemAlias;
        card.confirmEach = stage.confirmEach;
        card.subStages = (stage.stages ?? []).map(s => this.workflowStageToCard(s));
        break;
      case 'agentic':
        card.prompt = stage.prompt;
        card.toolDomains = stage.toolDomains;
        card.maxIterations = stage.maxIterations;
        card.outputField = stage.outputField;
        break;
    }
    return card;
  }
}

// ─── Helpers ──────────────────────────────────────────────────────

function emptyDraft(): CreationDraft {
  return {
    metadata: {
      id: '',
      domain: '',
      domains: [],
      description: '',
      samplePrompt: '',
      contextTags: [],
      systemPrompt: '',
      piiCheck: false,
    },
    stages: [],
    dataFlow: [],
  };
}

// ─── Stage-tree helpers (foreach-aware, depth 1) ──────────────────

/** Known fields that may appear on a creator stage payload. */
const STAGE_CARD_FIELDS = [
  'stageId', 'type', 'prompt', 'toolId', 'required', 'fields', 'parameters', 'outputMap',
  'prePrompt', 'allowedToolIds', 'selectPrompt', 'optionsFrom', 'labelField', 'templateRef',
  'rewindTo', 'dataFrom', 'toolDomains', 'maxIterations', 'outputField',
  'itemsFrom', 'itemAlias', 'confirmEach', 'isNew', 'isUpdated',
] as const;

/**
 * Normalize an incoming creator-action payload into a CreationStageCard.
 * Maps a nested foreach `stages` array into `subStages` (recursively) and
 * strips routing-only keys (afterStageId/parentStageId/changes).
 */
export function cardFromPayload(payload: any): CreationStageCard {
  const card: any = {};
  for (const key of STAGE_CARD_FIELDS) {
    if (payload[key] !== undefined) card[key] = payload[key];
  }
  if (Array.isArray(payload.stages)) {
    card.subStages = payload.stages.map(cardFromPayload);
  } else if (Array.isArray(payload.subStages)) {
    card.subStages = payload.subStages.map(cardFromPayload);
  }
  return card as CreationStageCard;
}

/** Recursively find a card by stageId anywhere in the tree. */
export function findCardInTree(stages: CreationStageCard[], stageId: string): CreationStageCard | null {
  for (const stage of stages) {
    if (stage.stageId === stageId) return stage;
    if (stage.subStages) {
      const found = findCardInTree(stage.subStages, stageId);
      if (found) return found;
    }
  }
  return null;
}

/** Insert a card at top level or within a parent foreach's subStages, optionally after a sibling. */
export function insertCardInTree(
  stages: CreationStageCard[],
  card: CreationStageCard,
  parentStageId?: string,
  afterStageId?: string,
): CreationStageCard[] {
  if (!parentStageId) {
    return spliceAfter(stages, card, afterStageId);
  }
  // Fall back to a top-level append if the requested parent can't be found,
  // so an add_stage with a stale/hallucinated parentStageId is never silently dropped.
  if (!findCardInTree(stages, parentStageId)) {
    console.warn(`[Creator] parentStageId "${parentStageId}" not found — appending stage at top level`);
    return spliceAfter(stages, card, afterStageId);
  }
  return stages.map(stage => {
    if (stage.stageId === parentStageId) {
      const subStages = spliceAfter(stage.subStages ?? [], card, afterStageId);
      return { ...stage, subStages };
    }
    if (stage.subStages) {
      return { ...stage, subStages: insertCardInTree(stage.subStages, card, parentStageId, afterStageId) };
    }
    return stage;
  });
}

function spliceAfter(stages: CreationStageCard[], card: CreationStageCard, afterStageId?: string): CreationStageCard[] {
  const next = [...stages];
  if (afterStageId) {
    const idx = next.findIndex(s => s.stageId === afterStageId);
    if (idx >= 0) {
      next.splice(idx + 1, 0, card);
      return next;
    }
  }
  next.push(card);
  return next;
}

/** Recursively merge `changes` into the card matching stageId. */
export function updateCardInTree(stages: CreationStageCard[], stageId: string, changes: Partial<CreationStageCard>): CreationStageCard[] {
  return stages.map(stage => {
    if (stage.stageId === stageId) {
      return { ...stage, ...changes };
    }
    if (stage.subStages) {
      return { ...stage, subStages: updateCardInTree(stage.subStages, stageId, changes) };
    }
    return stage;
  });
}

/** Recursively remove the card matching stageId from anywhere in the tree. */
export function removeCardFromTree(stages: CreationStageCard[], stageId: string): CreationStageCard[] {
  return stages
    .filter(s => s.stageId !== stageId)
    .map(s => (s.subStages ? { ...s, subStages: removeCardFromTree(s.subStages, stageId) } : s));
}

/** Reorder stages within a scope (top level when parentStageId is omitted, else inside that foreach). */
export function reorderCardsInScope(stages: CreationStageCard[], order: string[], parentStageId?: string): CreationStageCard[] {
  if (!parentStageId) {
    return applyOrder(stages, order);
  }
  return stages.map(stage => {
    if (stage.stageId === parentStageId) {
      return { ...stage, subStages: applyOrder(stage.subStages ?? [], order) };
    }
    if (stage.subStages) {
      return { ...stage, subStages: reorderCardsInScope(stage.subStages, order, parentStageId) };
    }
    return stage;
  });
}

function applyOrder(stages: CreationStageCard[], order: string[]): CreationStageCard[] {
  const map = new Map(stages.map(s => [s.stageId, s]));
  const reordered = order.map(id => map.get(id)).filter((s): s is CreationStageCard => s !== undefined);
  // Append any stages not mentioned in `order` to avoid dropping them
  for (const s of stages) {
    if (!order.includes(s.stageId)) reordered.push(s);
  }
  return reordered;
}

/** Collect all stageIds that appear more than once anywhere in the tree. */
export function findDuplicateStageIds(stages: CreationStageCard[]): string[] {
  const counts = new Map<string, number>();
  const walk = (list: CreationStageCard[]): void => {
    for (const s of list) {
      counts.set(s.stageId, (counts.get(s.stageId) ?? 0) + 1);
      if (s.subStages) walk(s.subStages);
    }
  };
  walk(stages);
  return [...counts.entries()].filter(([, n]) => n > 1).map(([id]) => id);
}
