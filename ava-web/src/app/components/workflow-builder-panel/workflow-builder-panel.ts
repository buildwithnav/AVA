import {
  Component, EventEmitter, Input, OnInit, Output, inject,
  ChangeDetectorRef, ViewEncapsulation,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatChipsModule } from '@angular/material/chips';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { COMMA, ENTER } from '@angular/cdk/keycodes';
import {
  WorkflowBuilderPayload,
  PromptDefinitionData,
  SimilarPromptMatch,
  WorkflowStageData,
} from '../../models/chat.models';

type StageType = 'ai-extract' | 'tool' | 'ai-generate' | 'ai-analyze' | 'user-select' | 'confirmation' | 'foreach' | 'agentic';

const STAGE_META: Record<string, { icon: string; color: string; label: string }> = {
  'ai-extract':   { icon: 'psychology',    color: '#1565c0', label: 'AI Extract' },
  'tool':         { icon: 'build',         color: '#2e7d32', label: 'Tool Call' },
  'ai-generate':  { icon: 'auto_awesome',  color: '#6a1b9a', label: 'AI Generate' },
  'ai-analyze':   { icon: 'bar_chart',     color: '#e65100', label: 'AI Analyze' },
  'user-select':  { icon: 'touch_app',     color: '#00838f', label: 'User Select' },
  'confirmation': { icon: 'help',          color: '#ad1457', label: 'Confirmation' },
  'foreach':      { icon: 'loop',          color: '#4e342e', label: 'For Each' },
  'agentic':      { icon: 'smart_toy',     color: '#00695c', label: 'Agentic' },
};

/** Stage types hidden from "add stage" menus; existing stages of these types still render. */
const HIDDEN_STAGE_TYPES: StageType[] = ['agentic'];

const STAGE_TYPES: StageType[] = ([
  'ai-extract', 'tool', 'ai-generate', 'ai-analyze',
  'user-select', 'confirmation', 'foreach', 'agentic',
] as StageType[]).filter(t => !HIDDEN_STAGE_TYPES.includes(t));

/** Allowed stage types at depth-2 — foreach excluded to prevent a 3rd nesting level. */
const DEPTH2_STAGE_TYPES: StageType[] = STAGE_TYPES.filter(t => t !== 'foreach');

/**
 * Inline workflow builder panel rendered inside the chat message list.
 * Provides a compact, form-based interface for editing workflow definitions
 * without requiring the full orchestration UI.
 */
@Component({
  selector: 'app-workflow-builder-panel',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatCardModule, MatButtonModule, MatIconModule, MatTabsModule,
    MatFormFieldModule, MatInputModule, MatSelectModule,
    MatChipsModule, MatSlideToggleModule, MatTooltipModule,
  ],
  templateUrl: './workflow-builder-panel.html',
  styleUrl: './workflow-builder-panel.scss',
  encapsulation: ViewEncapsulation.None,
})
export class WorkflowBuilderPanel implements OnInit {
  @Input() payload!: WorkflowBuilderPayload;
  @Output() submitted = new EventEmitter<PromptDefinitionData>();
  @Output() cancelled = new EventEmitter<void>();
  @Output() cloneSelected = new EventEmitter<PromptDefinitionData>();

  private cdr = inject(ChangeDetectorRef);

  // Editable prompt data
  prompt: PromptDefinitionData = {
    id: '', domain: '', domains: [], description: '', contextTags: [],
    systemPrompt: '', piiCheck: false, stages: [],
  };

  availableDomains: string[] = ['ado', 'study', 'ctms', 'support', 'ava-prompts'];
  similarPrompts: SimilarPromptMatch[] = [];
  showSimilar = false;

  selectedTabIndex = 0;
  selectedStageIndex = -1;
  newTagValue = '';
  readonly separatorKeyCodes = [ENTER, COMMA] as const;
  readonly stageTypes = STAGE_TYPES;
  readonly depth2StageTypes = DEPTH2_STAGE_TYPES;

  // ── Foreach sub-stage selection state ───────────────────────
  // Map<parentIndex, childIndex> — -1 means none selected in that parent
  selectedSubStageIndex = new Map<number, number>();
  // Map<"parentIndex-subIndex", childIndex> — for depth-2
  selectedDeepStageIndex = new Map<string, number>();

  ngOnInit(): void {
    if (this.payload) {
      this.prompt = { ...this.payload.prompt, stages: [...(this.payload.prompt.stages || [])] };
      if (this.payload.availableDomains?.length) {
        this.availableDomains = this.payload.availableDomains;
      }
      if (this.payload.similarPrompts?.length) {
        this.similarPrompts = this.payload.similarPrompts;
        this.showSimilar = true;
      }
    }
    // Reset sub-stage selection maps on load
    this.selectedSubStageIndex = new Map();
    this.selectedDeepStageIndex = new Map();
  }

  getStageMeta(type: string) {
    return STAGE_META[type] ?? { icon: 'settings', color: '#666', label: type };
  }

  /** Splits a comma-separated string into a trimmed, non-empty string array. */
  parseCommaSeparated(value: string): string[] {
    return value.split(',').map(s => s.trim()).filter(s => !!s);
  }

  // ── Tag management ──────────────────────────────────────────────

  addTag(event: { value: string; chipInput?: { clear: () => void } }): void {
    const value = (event.value || '').trim();
    if (value && !this.prompt.contextTags.includes(value)) {
      this.prompt.contextTags = [...this.prompt.contextTags, value];
    }
    if (event.chipInput) event.chipInput.clear();
    this.newTagValue = '';
  }

  addTagFromInput(): void {
    const value = this.newTagValue.trim();
    if (value && !this.prompt.contextTags.includes(value)) {
      this.prompt.contextTags = [...this.prompt.contextTags, value];
    }
    this.newTagValue = '';
  }

  removeTag(tag: string): void {
    this.prompt.contextTags = this.prompt.contextTags.filter(t => t !== tag);
  }

  // ── Stage management ────────────────────────────────────────────

  selectStage(index: number): void {
    this.selectedStageIndex = index;
  }

  collapseStage(): void {
    this.selectedStageIndex = -1;
  }

  addStage(type: StageType): void {
    const newStage: WorkflowStageData = {
      stageId: `stage-${this.prompt.stages.length + 1}`,
      type,
      ...(type === 'ai-extract' ? { prompt: '', required: [] } : {}),
      ...(type === 'tool' ? { toolId: '', parameters: {} } : {}),
      ...(type === 'ai-generate' ? { prompt: '' } : {}),
      ...(type === 'ai-analyze' ? { prompt: '' } : {}),
      ...(type === 'user-select' ? { selectPrompt: '', optionsFrom: '' } : {}),
      ...(type === 'confirmation' ? { prompt: '' } : {}),
    };
    this.prompt.stages = [...this.prompt.stages, newStage];
    this.selectedStageIndex = this.prompt.stages.length - 1;
  }

  removeStage(index: number): void {
    this.prompt.stages = this.prompt.stages.filter((_, i) => i !== index);
    if (this.selectedStageIndex >= this.prompt.stages.length) {
      this.selectedStageIndex = this.prompt.stages.length - 1;
    }
  }

  moveStageUp(index: number): void {
    if (index <= 0) return;
    const stages = [...this.prompt.stages];
    [stages[index - 1], stages[index]] = [stages[index], stages[index - 1]];
    this.prompt.stages = stages;
    this.selectedStageIndex = index - 1;
  }

  moveStageDown(index: number): void {
    if (index >= this.prompt.stages.length - 1) return;
    const stages = [...this.prompt.stages];
    [stages[index], stages[index + 1]] = [stages[index + 1], stages[index]];
    this.prompt.stages = stages;
    this.selectedStageIndex = index + 1;
  }

  // ── Depth-1 sub-stage management (foreach) ──────────────────

  getSubStages(parentIndex: number): WorkflowStageData[] {
    return (this.prompt.stages[parentIndex] as WorkflowStageData)?.stages ?? [];
  }

  isSubSelected(parentIndex: number, childIndex: number): boolean {
    return this.selectedSubStageIndex.get(parentIndex) === childIndex;
  }

  selectSubStage(parentIndex: number, childIndex: number): void {
    const current = this.selectedSubStageIndex.get(parentIndex);
    // Reassign new Map for change detection
    const next = new Map(this.selectedSubStageIndex);
    if (current === childIndex) {
      next.delete(parentIndex);
    } else {
      next.set(parentIndex, childIndex);
    }
    this.selectedSubStageIndex = next;
    this.cdr.markForCheck();
  }

  addSubStage(parentIndex: number, type: StageType): void {
    const stages = [...this.prompt.stages];
    const parent = { ...stages[parentIndex] } as WorkflowStageData;
    const subStages = [...(parent.stages ?? [])];
    const newStage: WorkflowStageData = {
      stageId: `sub-stage-${subStages.length + 1}`,
      type,
      ...(type === 'foreach' ? { itemsFrom: '', itemAlias: 'item', stages: [] } : {}),
    };
    subStages.push(newStage);
    parent.stages = subStages;
    stages[parentIndex] = parent;
    this.prompt.stages = stages;
  }

  removeSubStage(parentIndex: number, childIndex: number): void {
    const stages = [...this.prompt.stages];
    const parent = { ...stages[parentIndex] } as WorkflowStageData;
    const subStages = (parent.stages ?? []).filter((_, i) => i !== childIndex);
    parent.stages = subStages;
    stages[parentIndex] = parent;
    this.prompt.stages = stages;
    // Clear selection if removed sub was selected; reassign for change detection
    const next = new Map(this.selectedSubStageIndex);
    if (next.get(parentIndex) === childIndex) next.delete(parentIndex);
    this.selectedSubStageIndex = next;
    // Also clear deep selection for this sub
    const deepNext = new Map(this.selectedDeepStageIndex);
    deepNext.delete(`${parentIndex}-${childIndex}`);
    this.selectedDeepStageIndex = deepNext;
    this.cdr.markForCheck();
  }

  moveSubStageUp(parentIndex: number, childIndex: number): void {
    if (childIndex <= 0) return;
    const stages = [...this.prompt.stages];
    const parent = { ...stages[parentIndex] } as WorkflowStageData;
    const subStages = [...(parent.stages ?? [])];
    [subStages[childIndex - 1], subStages[childIndex]] = [subStages[childIndex], subStages[childIndex - 1]];
    parent.stages = subStages;
    stages[parentIndex] = parent;
    this.prompt.stages = stages;
  }

  moveSubStageDown(parentIndex: number, childIndex: number): void {
    const subStages = this.getSubStages(parentIndex);
    if (childIndex >= subStages.length - 1) return;
    const stages = [...this.prompt.stages];
    const parent = { ...stages[parentIndex] } as WorkflowStageData;
    const subs = [...(parent.stages ?? [])];
    [subs[childIndex], subs[childIndex + 1]] = [subs[childIndex + 1], subs[childIndex]];
    parent.stages = subs;
    stages[parentIndex] = parent;
    this.prompt.stages = stages;
  }

  // ── Depth-2 sub-stage management (foreach within foreach) ───

  getDeepStages(parentIndex: number, subIndex: number): WorkflowStageData[] {
    return (this.getSubStages(parentIndex)[subIndex] as WorkflowStageData)?.stages ?? [];
  }

  isDeepSelected(parentIndex: number, subIndex: number, childIndex: number): boolean {
    return this.selectedDeepStageIndex.get(`${parentIndex}-${subIndex}`) === childIndex;
  }

  selectDeepStage(parentIndex: number, subIndex: number, childIndex: number): void {
    const key = `${parentIndex}-${subIndex}`;
    const current = this.selectedDeepStageIndex.get(key);
    const next = new Map(this.selectedDeepStageIndex);
    if (current === childIndex) {
      next.delete(key);
    } else {
      next.set(key, childIndex);
    }
    this.selectedDeepStageIndex = next;
    this.cdr.markForCheck();
  }

  addDeepStage(parentIndex: number, subIndex: number, type: StageType): void {
    const stages = [...this.prompt.stages];
    const parent = { ...stages[parentIndex] } as WorkflowStageData;
    const subStages = [...(parent.stages ?? [])];
    const inner = { ...subStages[subIndex] } as WorkflowStageData;
    const deepStages = [...(inner.stages ?? [])];
    deepStages.push({
      stageId: `deep-stage-${deepStages.length + 1}`,
      type,
    });
    inner.stages = deepStages;
    subStages[subIndex] = inner;
    parent.stages = subStages;
    stages[parentIndex] = parent;
    this.prompt.stages = stages;
  }

  removeDeepStage(parentIndex: number, subIndex: number, childIndex: number): void {
    const stages = [...this.prompt.stages];
    const parent = { ...stages[parentIndex] } as WorkflowStageData;
    const subStages = [...(parent.stages ?? [])];
    const inner = { ...subStages[subIndex] } as WorkflowStageData;
    inner.stages = (inner.stages ?? []).filter((_, i) => i !== childIndex);
    subStages[subIndex] = inner;
    parent.stages = subStages;
    stages[parentIndex] = parent;
    this.prompt.stages = stages;
    // Clear selection if removed
    const key = `${parentIndex}-${subIndex}`;
    const next = new Map(this.selectedDeepStageIndex);
    if (next.get(key) === childIndex) next.delete(key);
    this.selectedDeepStageIndex = next;
    this.cdr.markForCheck();
  }

  moveDeepStageUp(parentIndex: number, subIndex: number, childIndex: number): void {
    if (childIndex <= 0) return;
    const stages = [...this.prompt.stages];
    const parent = { ...stages[parentIndex] } as WorkflowStageData;
    const subStages = [...(parent.stages ?? [])];
    const inner = { ...subStages[subIndex] } as WorkflowStageData;
    const deepStages = [...(inner.stages ?? [])];
    [deepStages[childIndex - 1], deepStages[childIndex]] = [deepStages[childIndex], deepStages[childIndex - 1]];
    inner.stages = deepStages;
    subStages[subIndex] = inner;
    parent.stages = subStages;
    stages[parentIndex] = parent;
    this.prompt.stages = stages;
  }

  moveDeepStageDown(parentIndex: number, subIndex: number, childIndex: number): void {
    const deepStages = this.getDeepStages(parentIndex, subIndex);
    if (childIndex >= deepStages.length - 1) return;
    const stages = [...this.prompt.stages];
    const parent = { ...stages[parentIndex] } as WorkflowStageData;
    const subStages = [...(parent.stages ?? [])];
    const inner = { ...subStages[subIndex] } as WorkflowStageData;
    const deeps = [...(inner.stages ?? [])];
    [deeps[childIndex], deeps[childIndex + 1]] = [deeps[childIndex + 1], deeps[childIndex]];
    inner.stages = deeps;
    subStages[subIndex] = inner;
    parent.stages = subStages;
    stages[parentIndex] = parent;
    this.prompt.stages = stages;
  }

  // ── Similar prompt actions ──────────────────────────────────────

  clonePrompt(match: SimilarPromptMatch): void {
    this.prompt = {
      ...match.prompt,
      id: `${match.prompt.id}-copy`,
      stages: [...(match.prompt.stages || [])],
      contextTags: [...(match.prompt.contextTags || [])],
    };
    this.showSimilar = false;
    this.cloneSelected.emit(match.prompt);
  }

  dismissSimilar(): void {
    this.showSimilar = false;
  }

  // ── JSON preview ────────────────────────────────────────────────

  get jsonPreview(): string {
    return JSON.stringify(this.prompt, null, 2);
  }

  async copyJson(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.jsonPreview);
    } catch { /* ignore */ }
  }

  // ── Submit / Cancel ─────────────────────────────────────────────

  onSubmit(): void {
    // Keep domain in sync with domains[0] for backward compatibility
    this.prompt.domain = this.prompt.domains?.[0] ?? '';
    this.submitted.emit(this.prompt);
  }

  onCancel(): void {
    this.cancelled.emit();
  }
}
