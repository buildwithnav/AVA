import { Component, inject, input, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatChipsModule } from '@angular/material/chips';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ErrorStateMatcher } from '@angular/material/core';
import { WorkflowBuilderService } from '../../../services/workflow-builder.service';
import {
  WorkflowStage, AiExtractStage, ToolStage, AiGenerateStage,
  AiAnalyzeStage, UserSelectStage, ConfirmationStage, ForEachStage, AgenticStage,
  ToolDefinition, TemplateDefinition,
} from '../../../models';
import { ValidationError } from '../../../services/workflow-validator.service';

/**
 * Type-specific configuration form for a single workflow stage.
 * Renders different fields based on the stage type.
 */
@Component({
  selector: 'wfb-stage-config',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatFormFieldModule, MatInputModule, MatSelectModule,
    MatChipsModule, MatSlideToggleModule, MatButtonModule, MatIconModule,
    MatAutocompleteModule, MatTooltipModule,
  ],
  template: `
    <div class="wfb-stage-config">
      <mat-form-field appearance="outline" class="wfb-field-full">
        <mat-label>Stage ID</mat-label>
        <input matInput [ngModel]="stage().stageId"
               (ngModelChange)="updateField('stageId', $event)"
               [errorStateMatcher]="errorMatcher('stageId')" />
        <mat-error>{{ fieldError('stageId')?.message }}</mat-error>
      </mat-form-field>

      <!-- ai-extract -->
      @if (stage().type === 'ai-extract') {
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Extraction Prompt</mat-label>
          <textarea matInput rows="4" [ngModel]="asExtract().prompt"
                    (ngModelChange)="updateField('prompt', $event)"
                    [errorStateMatcher]="errorMatcher('prompt')"
                    placeholder="Extract the following fields from the user's message..."></textarea>
          <mat-error>{{ fieldError('prompt')?.message }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Required Fields (comma-separated)</mat-label>
          <input matInput [ngModel]="(asExtract().required || []).join(', ')"
                 (ngModelChange)="updateField('required', parseCommaSeparated($event))" />
          <mat-hint>Fields that must be extracted; workflow pauses if missing</mat-hint>
        </mat-form-field>
      }

      <!-- tool -->
      @if (stage().type === 'tool') {
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Tool</mat-label>
          <mat-select [ngModel]="asTool().toolId ?? ''"
                      (ngModelChange)="onToolIdChange($event)"
                      (openedChange)="onToolSelectOpened($event)"
                      panelClass="wfb-tool-select-panel">
            <mat-select-trigger>{{ asTool().toolId || '— Dynamic (use prePrompt) —' }}</mat-select-trigger>
            <div class="wfb-select-search" (click)="$event.stopPropagation()">
              <input type="text" autocomplete="off"
                     placeholder="Search tools by name or description…"
                     [ngModel]="toolFilterText()"
                     (ngModelChange)="toolFilterText.set($event)"
                     (keydown)="$event.stopPropagation()" />
            </div>
            <mat-option [value]="''">— Dynamic (use prePrompt) —</mat-option>
            @for (tool of filteredToolOptions(); track tool.id) {
              <mat-option [value]="tool.id">{{ tool.id }} — {{ tool.description }}</mat-option>
            }
            @if (filteredToolOptions().length === 0) {
              <div class="wfb-select-empty">No tools match "{{ toolFilterText() }}"</div>
            }
          </mat-select>
        </mat-form-field>
        @if (asTool().toolId) {
          <!-- Parameters editor for selected tool -->
          @if (selectedToolParams().length > 0) {
            <div class="wfb-params-section">
              <label class="wfb-params-label">Parameters</label>
              <p class="wfb-params-hint">
                Use <code>{{'{{fieldName}}'}}</code> syntax to reference values from prior stages.
              </p>
              @for (param of selectedToolParams(); track param.name) {
                <mat-form-field appearance="outline" class="wfb-field-full">
                  <mat-label>{{ param.name }}{{ param.required ? ' *' : '' }} ({{ param.type }})</mat-label>
                  <input matInput
                         [ngModel]="getParameterValue(param.name)"
                         (ngModelChange)="updateParameter(param.name, $event)"
                         [placeholder]="param.description || ('{{' + param.name + '}}')" />
                  @if (param.description) {
                    <button matSuffix mat-icon-button type="button" class="wfb-param-info-btn"
                            [matTooltip]="param.description" matTooltipClass="wfb-param-tooltip"
                            [matTooltipShowDelay]="200" aria-label="Field description">
                      <mat-icon class="wfb-param-info-icon">info_outline</mat-icon>
                    </button>
                  }
                </mat-form-field>
              }
            </div>
          }
          <div class="wfb-output-row">
            <mat-form-field appearance="outline" class="wfb-field-grow">
              <mat-label>Output Field Name</mat-label>
              <input matInput [ngModel]="asTool().outputMap ? getFirstOutputMapKey() : ''"
                     (ngModelChange)="updateOutputMapKey($event)"
                     placeholder="e.g. epic" />
              <mat-hint>Context variable — use as {{ '{{' }}name{{ '}}' }} in later stages</mat-hint>
            </mat-form-field>
            <mat-form-field appearance="outline" class="wfb-field-grow">
              <mat-label>Source Property (optional)</mat-label>
              <input matInput [ngModel]="asTool().outputMap ? getFirstOutputMapValue() : ''"
                     (ngModelChange)="updateOutputMapValue($event)"
                     placeholder="e.g. id" />
              <mat-hint>Leave blank → flattens result: epic.id, epic.title, …</mat-hint>
            </mat-form-field>
          </div>
        }
        @if (!asTool().toolId) {
          <mat-form-field appearance="outline" class="wfb-field-full">
            <mat-label>PrePrompt — Tool Selection Prompt</mat-label>
            <textarea matInput rows="3"
                      [ngModel]="asTool().prePrompt?.prompt || ''"
                      (ngModelChange)="updatePrePromptField('prompt', $event)"
                      placeholder="Select the correct tool based on..."></textarea>
          </mat-form-field>
          <div class="wfb-allowed-tools">
            <label class="wfb-allowed-tools-label">Allowed Tool IDs</label>
            <mat-chip-set>
              @for (id of (asTool().prePrompt?.allowedToolIds || []); track id) {
                <mat-chip (removed)="removeAllowedTool(id)">
                  {{ id }}
                  <button matChipRemove><mat-icon>cancel</mat-icon></button>
                </mat-chip>
              }
            </mat-chip-set>
            <mat-form-field appearance="outline" class="wfb-field-full">
              <mat-label>Add tool…</mat-label>
              <input matInput
                     [ngModel]="toolSearchText()"
                     (ngModelChange)="toolSearchText.set($event)"
                     [matAutocomplete]="toolAc"
                     placeholder="Search available tools..." />
              <mat-autocomplete #toolAc="matAutocomplete"
                                (optionSelected)="addAllowedTool($event.option.value)">
                @for (tool of filteredAllowedTools(); track tool.id) {
                  <mat-option [value]="tool.id">{{ tool.id }} — {{ tool.description }}</mat-option>
                }
              </mat-autocomplete>
            </mat-form-field>
          </div>

          <!-- Static param overrides — merged on top of LLM-selected params at runtime -->
          <div class="wfb-static-params">
            <label class="wfb-allowed-tools-label">Static Param Overrides</label>
            <p class="wfb-params-hint">
              Always merged into the LLM-chosen tool's params (static wins).
              Supports <code>{{'{{fieldName}}'}}</code> token substitution.
            </p>
            @for (entry of staticParamEntries(); track entry.key) {
              <div class="wfb-static-param-row">
                <mat-form-field appearance="outline" class="wfb-static-param-key">
                  <mat-label>Param name</mat-label>
                  <input matInput [value]="entry.key" readonly />
                </mat-form-field>
                <mat-form-field appearance="outline" class="wfb-static-param-val">
                  <mat-label>Value</mat-label>
                  <input matInput [value]="entry.value"
                         (change)="updateStaticParam(entry.key, $any($event.target).value)"
                         [placeholder]="'{{' + entry.key + '}}'" />
                </mat-form-field>
                <button mat-icon-button color="warn" (click)="removeStaticParam(entry.key)"
                        matTooltip="Remove">
                  <mat-icon>remove_circle_outline</mat-icon>
                </button>
              </div>
            }
            <div class="wfb-static-param-row">
              <mat-form-field appearance="outline" class="wfb-static-param-key">
                <mat-label>New param name</mat-label>
                <input matInput [ngModel]="newStaticKey()"
                       (ngModelChange)="newStaticKey.set($event)"
                       placeholder="e.g. project" />
              </mat-form-field>
              <mat-form-field appearance="outline" class="wfb-static-param-val">
                <mat-label>Value</mat-label>
                <input matInput [ngModel]="newStaticVal()"
                       (ngModelChange)="newStaticVal.set($event)"
                       placeholder="e.g. {{'{{'}}project{{'}}'}}" />
              </mat-form-field>
              <button mat-icon-button color="primary"
                      [disabled]="!newStaticKey().trim()"
                      (click)="addStaticParam()"
                      matTooltip="Add">
                <mat-icon>add_circle_outline</mat-icon>
              </button>
            </div>
          </div>
        }
      }

      <!-- ai-generate -->
      @if (stage().type === 'ai-generate') {
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Generation Prompt</mat-label>
          <textarea matInput rows="4" [ngModel]="asGenerate().prompt"
                    (ngModelChange)="updateField('prompt', $event)"
                    [errorStateMatcher]="errorMatcher('prompt')"
                    placeholder="Generate a preview of the work item..."></textarea>
          <mat-error>{{ fieldError('prompt')?.message }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Template Reference</mat-label>
          <mat-select [ngModel]="asGenerate().templateRef"
                      (ngModelChange)="updateField('templateRef', $event)">
            <mat-option [value]="undefined">— None —</mat-option>
            @for (t of availableTemplates(); track t.id) {
              <mat-option [value]="t.id">{{ t.id }} — {{ t.description }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Rewind To (stage ID on correction)</mat-label>
          <input matInput [ngModel]="asGenerate().rewindTo"
                 (ngModelChange)="updateField('rewindTo', $event)" />
        </mat-form-field>
      }

      <!-- ai-analyze -->
      @if (stage().type === 'ai-analyze') {
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Analysis Prompt</mat-label>
          <textarea matInput rows="4" [ngModel]="asAnalyze().prompt"
                    (ngModelChange)="updateField('prompt', $event)"
                    [errorStateMatcher]="errorMatcher('prompt')"
                    placeholder="Analyze the results and summarize..."></textarea>
          <mat-error>{{ fieldError('prompt')?.message }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Data From (comma-separated stage IDs)</mat-label>
          <input matInput [ngModel]="(asAnalyze().dataFrom || []).join(', ')"
                 (ngModelChange)="updateField('dataFrom', parseCommaSeparated($event))" />
          <mat-hint>Limit which tool results are included in analysis</mat-hint>
        </mat-form-field>
      }

      <!-- user-select -->
      @if (stage().type === 'user-select') {
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Prompt (optional)</mat-label>
          <input matInput [ngModel]="asUserSelect().prompt"
                 (ngModelChange)="updateField('prompt', $event)" />
        </mat-form-field>
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Options From (dot-path)</mat-label>
          <input matInput [ngModel]="asUserSelect().optionsFrom"
                 (ngModelChange)="updateField('optionsFrom', $event)"
                 [errorStateMatcher]="errorMatcher('optionsFrom')"
                 placeholder="_result_query.rows" />
          <mat-error>{{ fieldError('optionsFrom')?.message }}</mat-error>
          <mat-hint>Dot-path to the array of options from a previous stage result</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Label Field</mat-label>
          <input matInput [ngModel]="asUserSelect().labelField"
                 (ngModelChange)="updateField('labelField', $event)"
                 placeholder="label" />
        </mat-form-field>
      }

      <!-- confirmation -->
      @if (stage().type === 'confirmation') {
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Confirmation Prompt</mat-label>
          <textarea matInput rows="2" [ngModel]="asConfirmation().prompt"
                    (ngModelChange)="updateField('prompt', $event)"
                    [errorStateMatcher]="errorMatcher('prompt')"
                    placeholder="Are you sure you want to proceed?"></textarea>
          <mat-error>{{ fieldError('prompt')?.message }}</mat-error>
        </mat-form-field>
        @if (earlierStages().length > 0) {
          <mat-form-field appearance="outline" class="wfb-field-full">
            <mat-label>Rewind To (on correction)</mat-label>
            <mat-select [ngModel]="confirmationRewindValue()"
                        (ngModelChange)="updateField('rewindTo', $event)">
              @for (s of earlierStages(); track s.stageId) {
                <mat-option [value]="s.stageId">{{ s.stageId }} ({{ s.type }})</mat-option>
              }
            </mat-select>
            <mat-hint>Defaults to the previous stage. If the user corrects instead of confirming, the workflow rewinds here.</mat-hint>
          </mat-form-field>
        }
      }

      <!-- foreach -->
      @if (stage().type === 'foreach') {
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Items From (dot-path)</mat-label>
          <input matInput [ngModel]="asForEach().itemsFrom"
                 (ngModelChange)="updateField('itemsFrom', $event)"
                 [errorStateMatcher]="errorMatcher('itemsFrom')"
                 placeholder="_result_query.rows" />
          <mat-error>{{ fieldError('itemsFrom')?.message }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Item Alias</mat-label>
          <input matInput [ngModel]="asForEach().itemAlias"
                 (ngModelChange)="updateField('itemAlias', $event)"
                 [errorStateMatcher]="errorMatcher('itemAlias')"
                 placeholder="item" />
          <mat-error>{{ fieldError('itemAlias')?.message }}</mat-error>
        </mat-form-field>
        <mat-slide-toggle [ngModel]="asForEach().confirmEach"
                          (ngModelChange)="updateField('confirmEach', $event)">
          Confirm each iteration
        </mat-slide-toggle>
        <p class="wfb-nested-hint">
          <mat-icon>info</mat-icon>
          Sub-stages are shown below this card in the stage list.
        </p>
      }

      <!-- agentic -->
      @if (stage().type === 'agentic') {
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>System Prompt</mat-label>
          <textarea matInput rows="5" [ngModel]="asAgentic().prompt"
                    (ngModelChange)="updateField('prompt', $event)"
                    [errorStateMatcher]="errorMatcher('prompt')"
                    placeholder="You are an autonomous agent. Use the available tools to..."></textarea>
          <mat-error>{{ fieldError('prompt')?.message }}</mat-error>
        </mat-form-field>
        @if (mcpDomainPrefixes().length > 0 || mcpToolNamesNormalized().length > 0) {
          <mat-form-field appearance="outline" class="wfb-field-full">
            <mat-label>Tool Domains / Tools</mat-label>
            <mat-select multiple
                        [ngModel]="asAgentic().toolDomains ?? []"
                        (ngModelChange)="updateField('toolDomains', $event)">
              @if (mcpDomainPrefixes().length > 0) {
                <mat-optgroup label="Domain Prefixes (all tools matching prefix)">
                  @for (prefix of mcpDomainPrefixes(); track prefix) {
                    <mat-option [value]="prefix">{{ prefix }}</mat-option>
                  }
                </mat-optgroup>
              }
              @if (mcpToolNamesNormalized().length > 0) {
                <mat-optgroup label="Specific Tools">
                  @for (name of mcpToolNamesNormalized(); track name) {
                    <mat-option [value]="name">{{ name }}</mat-option>
                  }
                </mat-optgroup>
              }
            </mat-select>
            <mat-hint>Select domain prefixes (e.g. "git") or specific tool names — leave empty to allow all tools</mat-hint>
          </mat-form-field>
        } @else {
          <mat-form-field appearance="outline" class="wfb-field-full">
            <mat-label>Tool Domains (comma-separated)</mat-label>
            <input matInput [ngModel]="(asAgentic().toolDomains || []).join(', ')"
                   (ngModelChange)="updateField('toolDomains', parseCommaSeparated($event))" />
            <mat-hint>e.g. git, work-items — leave empty for all domains (MCP tools not loaded)</mat-hint>
          </mat-form-field>
        }
        <mat-form-field appearance="outline">
          <mat-label>Max Iterations</mat-label>
          <input matInput type="number" [ngModel]="asAgentic().maxIterations ?? 15"
                 (ngModelChange)="updateField('maxIterations', +$event)" />
          <mat-hint>Maximum tool call loops (default: 15)</mat-hint>
        </mat-form-field>
        <mat-form-field appearance="outline" class="wfb-field-full">
          <mat-label>Output Field</mat-label>
          <input matInput [ngModel]="asAgentic().outputField ?? ''"
                 (ngModelChange)="updateField('outputField', $event)"
                 placeholder="agenticResult" />
          <mat-hint>Context field name to store the agent's final output</mat-hint>
        </mat-form-field>
      }
    </div>
  `,
  styles: [`
    .wfb-stage-config {
      display: flex; flex-direction: column; gap: 8px; padding: 8px 0;
    }
    .wfb-field-full { width: 100%; }
    .wfb-field-grow { flex: 1; min-width: 0; }
    .wfb-output-row { display: flex; gap: 8px; flex-wrap: wrap; }
    .wfb-nested-hint {
      display: flex; align-items: center; gap: 8px;
      font-size: 12px; opacity: 0.7;
    }
    .wfb-allowed-tools {
      display: flex; flex-direction: column; gap: 4px;
    }
    .wfb-allowed-tools-label {
      font-size: 12px; color: rgba(0,0,0,0.6); margin-bottom: 2px;
    }
    .wfb-static-params {
      display: flex; flex-direction: column; gap: 4px;
      margin-top: 12px; padding-top: 12px;
      border-top: 1px dashed rgba(0,0,0,0.12);
    }
    .wfb-static-param-row {
      display: flex; align-items: center; gap: 6px;
    }
    .wfb-static-param-key {
      flex: 0 0 160px;
    }
    .wfb-static-param-val {
      flex: 1 1 auto;
    }
    .wfb-params-section {
      display: flex; flex-direction: column; gap: 4px;
      padding: 8px 0;
    }
    .wfb-params-label {
      font-size: 12px; font-weight: 500; color: rgba(0,0,0,0.7);
    }
    .wfb-params-hint {
      font-size: 11px; color: rgba(0,0,0,0.5); margin: 0 0 4px 0;
    }
    .wfb-params-hint code {
      background: rgba(0,0,0,0.06); padding: 1px 4px; border-radius: 3px;
      font-family: monospace;
    }
    mat-chip {
      --mat-chip-label-text-color: var(--mat-sys-on-surface-variant, #444) !important;
      --mdc-chip-label-text-color: var(--mat-sys-on-surface-variant, #444) !important;
    }
    .wfb-select-search {
      position: sticky; top: 0; z-index: 1;
      padding: 8px; background: var(--mat-sys-surface-container, #fff);
      border-bottom: 1px solid rgba(0,0,0,0.12);
    }
    .wfb-select-search input {
      width: 100%; box-sizing: border-box;
      padding: 6px 8px; font: inherit; color: inherit;
      border: 1px solid rgba(0,0,0,0.3); border-radius: 4px;
      outline: none; background: transparent;
    }
    .wfb-select-empty {
      padding: 8px 16px; font-size: 12px; opacity: 0.6;
    }
    .wfb-param-info-btn {
      width: 28px !important;
      height: 28px !important;
      padding: 0 !important;
      --mdc-icon-button-state-layer-size: 28px;
    }
    .wfb-param-info-icon {
      font-size: 16px;
      width: 16px;
      height: 16px;
      color: var(--mat-sys-on-surface-variant, rgba(0, 0, 0, 0.45));
    }
  `],
})
export class StageConfigComponent {
  private readonly builder = inject(WorkflowBuilderService);

  stage = input.required<WorkflowStage>();
  index = input.required<number>();
  /** When set, this stage is a sub-stage inside a for-each at this parent index */
  parentIndex = input<number | undefined>(undefined);
  /** When set (along with parentIndex), this is a depth-2 stage inside a nested for-each */
  grandparentIndex = input<number | undefined>(undefined);

  availableTools = () => this.builder.availableTools();
  availableTemplates = () => this.builder.availableTemplates();

  /** The currently selected tool definition, if any */
  private selectedTool(): ToolDefinition | undefined {
    const toolId = this.asTool().toolId;
    if (!toolId) return undefined;
    return this.builder.availableTools().find(t => t.id === toolId);
  }

  // Autocomplete state for allowed tool IDs
  toolSearchText = signal('');

  // In-dropdown search for the Tool select
  toolFilterText = signal('');

  // New static param key/value input state
  newStaticKey = signal('');
  newStaticVal = signal('');

  /** Entries of prePrompt.staticParams as an array for iteration in the template. */
  staticParamEntries = computed<{ key: string; value: string }[]>(() => {
    const params = this.asTool().prePrompt?.staticParams ?? {};
    return Object.entries(params).map(([key, value]) => ({ key, value }));
  });

  /** Tools for the Tool dropdown: sorted alphabetically by id and filtered by the in-dropdown search. */
  filteredToolOptions = computed(() => {
    const search = this.toolFilterText().toLowerCase().trim();
    const tools = [...this.builder.availableTools()].sort((a, b) => a.id.localeCompare(b.id));
    if (!search) return tools;
    return tools.filter(t => t.id.toLowerCase().includes(search)
                          || (t.description?.toLowerCase().includes(search) ?? false));
  });

  /** Reset the in-dropdown search filter each time the Tool select opens. */
  onToolSelectOpened(opened: boolean): void {
    if (opened) this.toolFilterText.set('');
  }

  /** Get validation error for a field on this stage */
  fieldError(field: string): ValidationError | undefined {
    const i = this.index();
    const pi = this.parentIndex();
    const gp = this.grandparentIndex();
    let path: string;
    if (gp !== undefined && pi !== undefined) {
      path = `stages[${gp}].stages[${pi}].stages[${i}].${field}`;
    } else if (pi !== undefined) {
      path = `stages[${pi}].stages[${i}].${field}`;
    } else {
      path = `stages[${i}].${field}`;
    }
    return this.builder.getFieldError(path);
  }

  /** ErrorStateMatcher that returns true when a validation error exists for the field */
  errorMatcher(field: string): ErrorStateMatcher {
    return {
      isErrorState: (): boolean => !!this.fieldError(field),
    };
  }

  filteredAllowedTools = computed(() => {
    const search = this.toolSearchText().toLowerCase();
    const existing = this.asTool().prePrompt?.allowedToolIds || [];
    return this.builder.availableTools()
      .filter(t => !existing.includes(t.id))
      .filter(t => !search || t.id.toLowerCase().includes(search)
                            || (t.description?.toLowerCase().includes(search) ?? false))
      .sort((a, b) => a.id.localeCompare(b.id));
  });

  // Type narrowing helpers
  asExtract() { return this.stage() as AiExtractStage; }
  asTool() { return this.stage() as ToolStage; }
  asGenerate() { return this.stage() as AiGenerateStage; }
  asAnalyze() { return this.stage() as AiAnalyzeStage; }
  asUserSelect() { return this.stage() as UserSelectStage; }
  asConfirmation() { return this.stage() as ConfirmationStage; }
  asForEach() { return this.stage() as ForEachStage; }
  asAgentic() { return this.stage() as AgenticStage; }

  /** Stages in the same list as this stage (top-level, depth-1, or depth-2 sub-stages). */
  private siblingStages(): WorkflowStage[] {
    const pi = this.parentIndex();
    const gp = this.grandparentIndex();
    if (gp !== undefined && pi !== undefined) {
      const outer = this.builder.stages()[gp] as ForEachStage | undefined;
      const inner = outer?.stages?.[pi] as ForEachStage | undefined;
      return inner?.stages ?? [];
    }
    if (pi !== undefined) {
      const parent = this.builder.stages()[pi] as ForEachStage | undefined;
      return parent?.stages ?? [];
    }
    return this.builder.stages();
  }

  /** Stages that appear before this one — valid rewind targets. */
  earlierStages = computed<WorkflowStage[]>(() => {
    // Read the active prompt so this recomputes as stages change.
    this.builder.activePrompt();
    return this.siblingStages().slice(0, this.index()).filter(s => !!s.stageId);
  });

  /** Selected rewind target, defaulting to the immediately preceding stage. */
  confirmationRewindValue(): string | undefined {
    const explicit = this.asConfirmation().rewindTo;
    if (explicit) return explicit;
    const earlier = this.earlierStages();
    return earlier.length > 0 ? earlier[earlier.length - 1].stageId : undefined;
  }

  updateField(field: string, value: unknown): void {
    const updated = { ...this.stage(), [field]: value } as WorkflowStage;
    const pi = this.parentIndex();
    const gp = this.grandparentIndex();
    if (gp !== undefined && pi !== undefined) {
      this.builder.updateDeepStage(gp, pi, this.index(), updated);
    } else if (pi !== undefined) {
      this.builder.updateSubStage(pi, this.index(), updated);
    } else {
      this.builder.updateStage(this.index(), updated);
    }
  }

  /** Get the parameter definitions for the currently selected tool */
  selectedToolParams = computed(() => {
    const toolId = this.asTool().toolId;
    if (!toolId) return [];
    const tool = this.builder.availableTools().find(t => t.id === toolId);
    return tool?.parameters ?? [];
  });

  /** Get the current value of a parameter mapping */
  getParameterValue(paramName: string): string {
    const explicit = this.asTool().parameters?.[paramName];
    return explicit ?? '';
  }

  /** Update a single parameter value in the parameters map */
  updateParameter(paramName: string, value: string): void {
    const current = { ...(this.asTool().parameters || {}) };
    if (value) {
      current[paramName] = value;
    } else {
      delete current[paramName];
    }
    this.updateField('parameters', Object.keys(current).length > 0 ? current : undefined);
  }

  /** Get first key from outputMap (the context variable name) */
  getFirstOutputMapKey(): string {
    const map = this.asTool().outputMap;
    if (!map) return '';
    return Object.keys(map)[0] ?? '';
  }

  /** Get first value from outputMap (the source JSON property name) */
  getFirstOutputMapValue(): string {
    const map = this.asTool().outputMap;
    if (!map) return '';
    const firstKey = Object.keys(map)[0];
    return firstKey !== undefined ? (map[firstKey] ?? '') : '';
  }

  /** Update the context variable name (key) of the outputMap, preserving the source property */
  updateOutputMapKey(fieldName: string): void {
    if (fieldName) {
      const existingValue = this.getFirstOutputMapValue();
      this.updateField('outputMap', { [fieldName]: existingValue });
    } else {
      this.updateField('outputMap', undefined);
    }
  }

  /** Update the source JSON property (value) of the outputMap, preserving the field name */
  updateOutputMapValue(sourceProperty: string): void {
    const existingKey = this.getFirstOutputMapKey();
    if (existingKey) {
      this.updateField('outputMap', { [existingKey]: sourceProperty });
    }
  }

  /** @deprecated Use updateOutputMapKey instead */
  updateOutputMap(fieldName: string): void {
    this.updateOutputMapKey(fieldName);
  }

  /** When tool selection changes, reset parameters */
  onToolIdChange(toolId: string): void {
    const tool = this.stage() as ToolStage;
    const parameters = undefined;
    const updated: ToolStage = {
      ...tool,
      toolId: toolId || undefined,
      // Clear prePrompt when switching to a fixed tool; restore nothing when switching back
      // to dynamic mode (user will configure it manually).
      prePrompt: toolId ? undefined : tool.prePrompt,
      parameters,
      outputMap: tool.outputMap,
    };
    const pi = this.parentIndex();
    const gp = this.grandparentIndex();
    if (gp !== undefined && pi !== undefined) {
      this.builder.updateDeepStage(gp, pi, this.index(), updated as WorkflowStage);
    } else if (pi !== undefined) {
      this.builder.updateSubStage(pi, this.index(), updated as WorkflowStage);
    } else {
      this.builder.updateStage(this.index(), updated as WorkflowStage);
    }
  }

  updatePrePromptField(field: string, value: unknown): void {
    const tool = this.asTool();
    const prePrompt = { ...(tool.prePrompt ?? { prompt: '', allowedToolIds: [] }), [field]: value };
    this.updateField('prePrompt', prePrompt);
  }

  addAllowedTool(toolId: string): void {
    const current = [...(this.asTool().prePrompt?.allowedToolIds || [])];
    if (!current.includes(toolId)) {
      current.push(toolId);
      this.updatePrePromptField('allowedToolIds', current);
    }
    this.toolSearchText.set('');
  }

  removeAllowedTool(toolId: string): void {
    const current = (this.asTool().prePrompt?.allowedToolIds || []).filter((id: string) => id !== toolId);
    this.updatePrePromptField('allowedToolIds', current);
  }

  addStaticParam(): void {
    const key = this.newStaticKey().trim();
    if (!key) return;
    const current = { ...(this.asTool().prePrompt?.staticParams ?? {}) };
    current[key] = this.newStaticVal().trim();
    this.updatePrePromptField('staticParams', current);
    this.newStaticKey.set('');
    this.newStaticVal.set('');
  }

  updateStaticParam(key: string, value: string): void {
    const current = { ...(this.asTool().prePrompt?.staticParams ?? {}) };
    current[key] = value;
    this.updatePrePromptField('staticParams', current);
  }

  removeStaticParam(key: string): void {
    const current = { ...(this.asTool().prePrompt?.staticParams ?? {}) };
    delete current[key];
    this.updatePrePromptField('staticParams', Object.keys(current).length ? current : undefined);
  }

  parseCommaSeparated(value: string): string[] {
    return value.split(',').map(s => s.trim()).filter(s => s.length > 0);
  }

  /**
   * Derives inferred domain prefixes from the available MCP tool names
   * by extracting the first segment of each underscore-separated name.
   * E.g. ["git_status", "git_commit", "create_work_item"] → ["git", "create"]
   * Returned as a sorted, deduplicated list for the agentic tool domain selector.
   */
  mcpDomainPrefixes = computed<string[]>(() => {
    const names = this.builder.availableMcpToolNames();
    const prefixes = new Set<string>();
    for (const name of names) {
      // Normalize: strip gateway prefix, lowercase
      const normalized = name
        .replace(/^ado-mcp-server-/, '')
        .replace(/-/g, '_')
        .toLowerCase();
      const firstSeg = normalized.split('_')[0];
      if (firstSeg) prefixes.add(firstSeg);
    }
    return [...prefixes].sort();
  });

  /** All MCP tool names, normalized (gateway prefix stripped, hyphens → underscores). */
  mcpToolNamesNormalized = computed<string[]>(() =>
    this.builder.availableMcpToolNames()
      .map(n => n.replace(/^ado-mcp-server-/, '').replace(/-/g, '_').toLowerCase())
      .sort()
  );
}