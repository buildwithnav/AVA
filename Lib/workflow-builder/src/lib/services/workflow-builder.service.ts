import { Injectable, signal, computed } from '@angular/core';
import {
  PromptDefinition,
  WorkflowStage,
  ForEachStage,
  ConfirmationStage,
  ToolDefinition,
  TemplateDefinition,
  createEmptyPrompt,
  createEmptyStage,
  StageType,
} from '../models';
import { ValidationError } from './workflow-validator.service';

export type BuilderMode = 'form' | 'canvas';

interface UndoEntry {
  prompt: PromptDefinition;
  description: string;
}

/**
 * Central state management for the workflow builder.
 * Tracks the active workflow, undo/redo history, dirty state, and builder mode.
 */
@Injectable({ providedIn: 'root' })
export class WorkflowBuilderService {
  // ─── State Signals ────────────────────────────────────────────

  /** The workflow currently being edited */
  readonly activePrompt = signal<PromptDefinition>(createEmptyPrompt());

  /** Currently selected stage index (-1 = none) */
  readonly selectedStageIndex = signal<number>(-1);

  /** Selected depth-1 sub-stage: parent = top-level stage index, child = sub-stage index */
  readonly selectedSubStage = signal<{ parent: number; child: number } | null>(null);

  /** Selected depth-2 stage: parent = top-level index, sub = depth-1 index, child = depth-2 index */
  readonly selectedDeepStage = signal<{ parent: number; sub: number; child: number } | null>(null);

  /** Builder mode: form-based or visual canvas */
  readonly mode = signal<BuilderMode>('form');

  /** Active tab index in form mode (0=Metadata, 1=Stages, 2=Tools, 3=Test, 4=JSON) */
  readonly selectedTabIndex = signal<number>(0);

  /** Available tools loaded from API (for tool picker) */
  readonly availableTools = signal<ToolDefinition[]>([]);

  /** Available templates loaded from API (for template picker) */
  readonly availableTemplates = signal<TemplateDefinition[]>([]);

  /** Raw MCP tool names loaded from API (for agentic stage tool selector) */
  readonly availableMcpToolNames = signal<string[]>([]);

  /** All prompts in the library — used for cross-workflow context tag uniqueness checks */
  readonly allPrompts = signal<PromptDefinition[]>([]);

  /** Current validation errors (populated by validate action) */
  readonly validationErrors = signal<ValidationError[]>([]);

  /** Incremented each time a new prompt is loaded (for canvas fit-to-view) */
  readonly promptLoadVersion = signal(0);

  /** Snapshot of prompt when editing started (for dirty detection) */
  private readonly originalSnapshot = signal('');

  /** The ID the active prompt was loaded under (empty for new/unsaved workflows) */
  private readonly _originalId = signal('');
  readonly originalId = this._originalId.asReadonly();

  /** Undo stack */
  private undoStack: UndoEntry[] = [];

  /** Redo stack */
  private redoStack: UndoEntry[] = [];

  private readonly MAX_UNDO = 50;

  // ─── Computed ─────────────────────────────────────────────────

  /** Whether the current prompt has unsaved changes */
  readonly isDirty = computed(() =>
    JSON.stringify(this.activePrompt()) !== this.originalSnapshot()
  );

  /** Stages of the active prompt (convenience accessor) */
  readonly stages = computed(() => this.activePrompt().stages ?? []);

  /** The currently selected stage, or null */
  readonly selectedStage = computed(() => {
    const idx = this.selectedStageIndex();
    const stages = this.stages();
    return idx >= 0 && idx < stages.length ? stages[idx] : null;
  });

  readonly canUndo = computed(() => this.undoStack.length > 0);
  readonly canRedo = computed(() => this.redoStack.length > 0);

  // ─── Prompt Lifecycle ─────────────────────────────────────────

  /** Load a prompt for editing (resets undo history) */
  loadPrompt(prompt: PromptDefinition): void {
    this.activePrompt.set(structuredClone(prompt));
    this.originalSnapshot.set(JSON.stringify(prompt));
    this._originalId.set(prompt.id);
    this.selectedStageIndex.set(-1);
    this.selectedSubStage.set(null);
    this.selectedDeepStage.set(null);
    this.undoStack = [];
    this.redoStack = [];
    this.promptLoadVersion.update(v => v + 1);
  }

  /** Start a new blank prompt */
  newPrompt(domain: string = ''): void {
    this._originalId.set('');
    this.loadPrompt(createEmptyPrompt(domain));
  }

  /** Mark the current state as saved (resets dirty tracking and original ID) */
  markSaved(): void {
    this.originalSnapshot.set(JSON.stringify(this.activePrompt()));
    this._originalId.set(this.activePrompt().id);
  }

  /** Get the current prompt as a plain JSON object */
  toJSON(): PromptDefinition {
    return structuredClone(this.activePrompt());
  }

  // ─── Undo / Redo ──────────────────────────────────────────────

  private pushUndo(description: string): void {
    this.undoStack.push({
      prompt: structuredClone(this.activePrompt()),
      description,
    });
    if (this.undoStack.length > this.MAX_UNDO) {
      this.undoStack.shift();
    }
    this.redoStack = [];
  }

  undo(): void {
    const entry = this.undoStack.pop();
    if (!entry) return;
    this.redoStack.push({
      prompt: structuredClone(this.activePrompt()),
      description: entry.description,
    });
    this.activePrompt.set(entry.prompt);
  }

  redo(): void {
    const entry = this.redoStack.pop();
    if (!entry) return;
    this.undoStack.push({
      prompt: structuredClone(this.activePrompt()),
      description: entry.description,
    });
    this.activePrompt.set(entry.prompt);
  }

  // ─── Prompt Metadata Updates ──────────────────────────────────

  updateMetadata(partial: Partial<Pick<PromptDefinition,
    'id' | 'domain' | 'domains' | 'description' | 'samplePrompt' | 'systemPrompt' | 'piiCheck' | 'contextTags'
  >>): void {
    this.pushUndo('Update metadata');
    this.activePrompt.update((p) => ({ ...p, ...partial }));
    this.clearValidationErrors();
  }

  // ─── Stage Operations ─────────────────────────────────────────

  addStage(type: StageType, insertIndex?: number): void {
    this.pushUndo(`Add ${type} stage`);
    const stage = createEmptyStage(type);
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const idx = insertIndex ?? stages.length;
      // Default a confirmation's rewindTo to the immediately preceding stage so
      // user corrections rewind one step back (editable in the UI).
      if (type === 'confirmation' && idx > 0) {
        const prev = stages[idx - 1];
        if (prev?.stageId) {
          (stage as ConfirmationStage).rewindTo = prev.stageId;
        }
      }
      stages.splice(idx, 0, stage);
      return { ...p, stages };
    });
    this.selectedStageIndex.set(insertIndex ?? (this.stages().length - 1));
  }

  removeStage(index: number): void {
    this.pushUndo('Remove stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      stages.splice(index, 1);
      return { ...p, stages };
    });
    if (this.selectedStageIndex() >= this.stages().length) {
      this.selectedStageIndex.set(this.stages().length - 1);
    }
    // Clear sub/deep selection if their parent was removed or index shifted
    const sub = this.selectedSubStage();
    if (sub !== null && (sub.parent === index || sub.parent >= this.stages().length)) {
      this.selectedSubStage.set(null);
      this.selectedDeepStage.set(null);
    }
  }

  updateStage(index: number, stage: WorkflowStage): void {
    this.pushUndo('Update stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      stages[index] = stage;
      return { ...p, stages };
    });
    this.clearValidationErrors();
  }

  moveStage(fromIndex: number, toIndex: number): void {
    if (fromIndex === toIndex) return;
    this.pushUndo('Move stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const [moved] = stages.splice(fromIndex, 1);
      stages.splice(toIndex, 0, moved);
      return { ...p, stages };
    });
    this.selectedStageIndex.set(toIndex);
    // Sub/deep selection indices may be invalidated after a reorder — clear them
    this.selectedSubStage.set(null);
    this.selectedDeepStage.set(null);
  }

  duplicateStage(index: number): void {
    this.pushUndo('Duplicate stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const clone = structuredClone(stages[index]);
      clone.stageId = `${clone.stageId}-copy-${Date.now()}`;
      stages.splice(index + 1, 0, clone);
      return { ...p, stages };
    });
    this.selectedStageIndex.set(index + 1);
  }

  selectStage(index: number): void {
    this.selectedStageIndex.set(this.selectedStageIndex() === index ? -1 : index);
  }

  // ─── Nested Stage Operations (ForEach sub-stages) ────────────

  /** Get sub-stages for a top-level for-each stage */
  getSubStages(parentIndex: number): WorkflowStage[] {
    const stage = this.stages()[parentIndex];
    if (stage?.type === 'foreach') {
      return (stage as ForEachStage).stages ?? [];
    }
    return [];
  }

  /** Add a sub-stage to a for-each stage */
  addSubStage(parentIndex: number, type: StageType, insertIndex?: number): void {
    this.pushUndo(`Add ${type} sub-stage`);
    const subStage = createEmptyStage(type);
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const parent = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(parent.stages ?? [])];
      const idx = insertIndex ?? subStages.length;
      // Default confirmation rewindTo to preceding sibling (mirrors addStage behaviour)
      if (type === 'confirmation' && idx > 0) {
        const prev = subStages[idx - 1];
        if (prev?.stageId) {
          (subStage as ConfirmationStage).rewindTo = prev.stageId;
        }
      }
      subStages.splice(idx, 0, subStage);
      parent.stages = subStages;
      stages[parentIndex] = parent;
      return { ...p, stages };
    });
  }

  /** Remove a sub-stage from a for-each stage */
  removeSubStage(parentIndex: number, childIndex: number): void {
    this.pushUndo('Remove sub-stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const parent = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(parent.stages ?? [])];
      subStages.splice(childIndex, 1);
      parent.stages = subStages;
      stages[parentIndex] = parent;
      return { ...p, stages };
    });
    // Clear deep selection if its parent sub-stage was removed
    const deep = this.selectedDeepStage();
    if (deep !== null && deep.parent === parentIndex && deep.sub === childIndex) {
      this.selectedDeepStage.set(null);
    }
  }

  /** Update a sub-stage within a for-each stage */
  updateSubStage(parentIndex: number, childIndex: number, stage: WorkflowStage): void {
    this.pushUndo('Update sub-stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const parent = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(parent.stages ?? [])];
      subStages[childIndex] = stage;
      parent.stages = subStages;
      stages[parentIndex] = parent;
      return { ...p, stages };
    });
    this.clearValidationErrors();
  }

  /** Move a sub-stage within a for-each stage */
  moveSubStage(parentIndex: number, fromIndex: number, toIndex: number): void {
    if (fromIndex === toIndex) return;
    this.pushUndo('Move sub-stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const parent = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(parent.stages ?? [])];
      const [moved] = subStages.splice(fromIndex, 1);
      subStages.splice(toIndex, 0, moved);
      parent.stages = subStages;
      stages[parentIndex] = parent;
      return { ...p, stages };
    });
    // Deep selection indices within this sub-stage list may be invalidated — clear
    this.selectedDeepStage.set(null);
  }

  /** Duplicate a sub-stage within a for-each stage */
  duplicateSubStage(parentIndex: number, childIndex: number): void {
    this.pushUndo('Duplicate sub-stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const parent = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(parent.stages ?? [])];
      const clone = structuredClone(subStages[childIndex]);
      clone.stageId = `${clone.stageId}-copy-${Date.now()}`;
      subStages.splice(childIndex + 1, 0, clone);
      parent.stages = subStages;
      stages[parentIndex] = parent;
      return { ...p, stages };
    });
  }

  // ─── Depth-2 Stage Operations (ForEach inside ForEach) ───────
  addDeepStage(parentIndex: number, subIndex: number, type: StageType, insertIndex?: number): void {
    this.pushUndo(`Add ${type} deep stage`);
    const newStage = createEmptyStage(type);
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const outer = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(outer.stages ?? [])];
      const inner = { ...subStages[subIndex] } as ForEachStage;
      const deepStages = [...(inner.stages ?? [])];
      const idx = insertIndex ?? deepStages.length;
      // Default confirmation rewindTo to preceding deep sibling
      if (type === 'confirmation' && idx > 0) {
        const prev = deepStages[idx - 1];
        if (prev?.stageId) {
          (newStage as ConfirmationStage).rewindTo = prev.stageId;
        }
      }
      deepStages.splice(idx, 0, newStage);
      inner.stages = deepStages;
      subStages[subIndex] = inner;
      outer.stages = subStages;
      stages[parentIndex] = outer;
      return { ...p, stages };
    });
  }

  /** Remove a depth-2 stage */
  removeDeepStage(parentIndex: number, subIndex: number, childIndex: number): void {
    this.pushUndo('Remove deep stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const outer = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(outer.stages ?? [])];
      const inner = { ...subStages[subIndex] } as ForEachStage;
      const deepStages = [...(inner.stages ?? [])];
      deepStages.splice(childIndex, 1);
      inner.stages = deepStages;
      subStages[subIndex] = inner;
      outer.stages = subStages;
      stages[parentIndex] = outer;
      return { ...p, stages };
    });
    const deep = this.selectedDeepStage();
    if (deep !== null && deep.parent === parentIndex && deep.sub === subIndex && deep.child === childIndex) {
      this.selectedDeepStage.set(null);
    }
  }

  /** Update a depth-2 stage */
  updateDeepStage(parentIndex: number, subIndex: number, childIndex: number, stage: WorkflowStage): void {
    this.pushUndo('Update deep stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const outer = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(outer.stages ?? [])];
      const inner = { ...subStages[subIndex] } as ForEachStage;
      const deepStages = [...(inner.stages ?? [])];
      deepStages[childIndex] = stage;
      inner.stages = deepStages;
      subStages[subIndex] = inner;
      outer.stages = subStages;
      stages[parentIndex] = outer;
      return { ...p, stages };
    });
    this.clearValidationErrors();
  }

  /** Reorder depth-2 stages within a depth-1 foreach sub-stage */
  moveDeepStage(parentIndex: number, subIndex: number, fromIndex: number, toIndex: number): void {
    if (fromIndex === toIndex) return;
    this.pushUndo('Move deep stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const outer = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(outer.stages ?? [])];
      const inner = { ...subStages[subIndex] } as ForEachStage;
      const deepStages = [...(inner.stages ?? [])];
      const [moved] = deepStages.splice(fromIndex, 1);
      deepStages.splice(toIndex, 0, moved);
      inner.stages = deepStages;
      subStages[subIndex] = inner;
      outer.stages = subStages;
      stages[parentIndex] = outer;
      return { ...p, stages };
    });
    this.selectedDeepStage.set(null);
  }

  /** Duplicate a depth-2 stage */
  duplicateDeepStage(parentIndex: number, subIndex: number, childIndex: number): void {
    this.pushUndo('Duplicate deep stage');
    this.activePrompt.update((p) => {
      const stages = [...(p.stages ?? [])];
      const outer = { ...stages[parentIndex] } as ForEachStage;
      const subStages = [...(outer.stages ?? [])];
      const inner = { ...subStages[subIndex] } as ForEachStage;
      const deepStages = [...(inner.stages ?? [])];
      const clone = structuredClone(deepStages[childIndex]);
      clone.stageId = `${clone.stageId}-copy-${Date.now()}`;
      deepStages.splice(childIndex + 1, 0, clone);
      inner.stages = deepStages;
      subStages[subIndex] = inner;
      outer.stages = subStages;
      stages[parentIndex] = outer;
      return { ...p, stages };
    });
  }

  // ─── Mode Toggle ──────────────────────────────────────────────

  setMode(mode: BuilderMode): void {
    this.mode.set(mode);
  }

  toggleMode(): void {
    this.mode.update((m) => (m === 'form' ? 'canvas' : 'form'));
  }

  // ─── Catalog Data ─────────────────────────────────────────────

  setAvailableTools(tools: ToolDefinition[]): void {
    this.availableTools.set(tools);
  }

  setAvailableTemplates(templates: TemplateDefinition[]): void {
    this.availableTemplates.set(templates);
  }

  setAvailableMcpToolNames(names: string[]): void {
    this.availableMcpToolNames.set(names);
  }

  setAllPrompts(prompts: PromptDefinition[]): void {
    this.allPrompts.set(prompts);
  }

  // ─── Validation ───────────────────────────────────────────────

  setValidationErrors(errors: ValidationError[]): void {
    this.validationErrors.set(errors);
  }

  clearValidationErrors(): void {
    this.validationErrors.set([]);
  }

  /** Get the first validation error for a given path */
  getFieldError(path: string): ValidationError | undefined {
    return this.validationErrors().find(e => e.path === path);
  }
}
