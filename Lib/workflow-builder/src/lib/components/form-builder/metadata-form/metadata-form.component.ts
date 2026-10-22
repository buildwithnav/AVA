import { Component, inject, output, input, effect, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule, ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatChipsModule } from '@angular/material/chips';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { WorkflowBuilderService } from '../../../services/workflow-builder.service';
import { WorkflowApiService } from '../../../services/workflow-api.service';
import { WorkflowValidatorService } from '../../../services/workflow-validator.service';
import { DomainInfo, PromptDefinition } from '../../../models';

/**
 * Workflow metadata form — edits the top-level prompt definition fields:
 * ID, domain, description, system prompt, context tags, PII toggle.
 */
@Component({
  selector: 'wfb-metadata-form',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ReactiveFormsModule,
    MatFormFieldModule, MatInputModule, MatSelectModule,
    MatChipsModule, MatSlideToggleModule, MatIconModule, MatButtonModule,
    MatSnackBarModule, MatTooltipModule, MatProgressSpinnerModule,
  ],
  template: `
    <form [formGroup]="form" class="wfb-metadata-form" (ngSubmit)="onSave()">
      <div class="wfb-metadata-row">
        <mat-form-field appearance="outline" class="wfb-field-id">
          <mat-label>Prompt ID</mat-label>
          <input matInput formControlName="id" placeholder="e.g. ado-create-work-item" />
          @if (form.get('id')?.hasError('required')) {
            <mat-error>ID is required</mat-error>
          }
        </mat-form-field>

        <mat-form-field appearance="outline" class="wfb-field-domain">
          <mat-label>Domain</mat-label>
          <mat-select formControlName="domain">
            @for (d of domains(); track d.id) {
              <mat-option [value]="d.id">{{ d.name }}</mat-option>
            }
          </mat-select>
          @if (form.get('domain')?.hasError('required')) {
            <mat-error>Domain is required</mat-error>
          }
        </mat-form-field>
      </div>

      <mat-form-field appearance="outline" class="wfb-field-full">
        <mat-label>Description</mat-label>
        <input matInput formControlName="description"
               placeholder="Brief description of what this workflow does" />
      </mat-form-field>

      <mat-form-field appearance="outline" class="wfb-field-full">
        <mat-label>System Prompt</mat-label>
        <textarea matInput formControlName="systemPrompt"
                  rows="6" placeholder="Instructions for the LLM..."></textarea>
        @if (form.get('systemPrompt')?.hasError('required')) {
          <mat-error>System prompt is required</mat-error>
        }
      </mat-form-field>

      <mat-form-field appearance="outline" class="wfb-field-full">
        <mat-label>Sample Prompt</mat-label>
        <input matInput formControlName="samplePrompt"
               placeholder="Example prompt shown on suggestion cards" />
        @if (generating()) {
          <mat-spinner matSuffix diameter="20"></mat-spinner>
        } @else {
          <button mat-icon-button matSuffix type="button"
                  matTooltip="Use AI to generate a sample prompt for this workflow"
                  [disabled]="generating()"
                  (click)="generateSamplePrompt()">
            <mat-icon>auto_awesome</mat-icon>
          </button>
        }
        <mat-hint>Shown when users browse workflow suggestions</mat-hint>
      </mat-form-field>

      <mat-form-field appearance="outline" class="wfb-field-full">
        <mat-label>Context Tags</mat-label>
        <mat-chip-grid #chipGrid formControlName="contextTags">
          @for (tag of contextTags; track tag) {
            <mat-chip-row (removed)="removeTag(tag)"
                          [class.wfb-tag-conflict]="tagConflicts().has(tag)"
                          [matTooltip]="tagConflicts().has(tag) ? 'Already used by workflow: ' + tagConflicts().get(tag) : ''">
              @if (tagConflicts().has(tag)) {
                <mat-icon matChipAvatar class="wfb-tag-conflict-icon">warning</mat-icon>
              }
              {{ tag }}
              <button matChipRemove>
                <mat-icon>cancel</mat-icon>
              </button>
            </mat-chip-row>
          }
        </mat-chip-grid>
        <input matInput placeholder="Add tag..."
               [matChipInputFor]="chipGrid"
               [matChipInputSeparatorKeyCodes]="separatorKeyCodes"
               (matChipInputTokenEnd)="addTag($event)" />
        <mat-hint>Press Enter or comma to add tags used for semantic matching</mat-hint>
      </mat-form-field>

      <mat-slide-toggle formControlName="piiCheck" color="primary">
        Enable PII detection on extracted values
      </mat-slide-toggle>
    </form>
  `,
  styles: [`
    .wfb-metadata-form {
      display: flex;
      flex-direction: column;
      gap: 12px;
      padding: 16px;
    }
    .wfb-metadata-row {
      display: grid;
      grid-template-columns: 2fr 1fr;
      gap: 12px;
    }
    .wfb-field-full { width: 100%; }
    mat-chip-row {
      --mat-chip-label-text-color: var(--mat-sys-on-surface-variant, #444) !important;
      --mdc-chip-label-text-color: var(--mat-sys-on-surface-variant, #444) !important;
    }
    mat-chip-row.wfb-tag-conflict {
      --mat-chip-label-text-color: #b45309 !important;
      --mdc-chip-label-text-color: #b45309 !important;
      border: 1px solid #f59e0b !important;
      background-color: #fffbeb !important;
    }
    .wfb-tag-conflict-icon {
      color: #f59e0b;
      font-size: 16px;
      width: 16px;
      height: 16px;
    }
  `],
})
export class MetadataFormComponent {
  private readonly fb = inject(FormBuilder);
  private readonly builder = inject(WorkflowBuilderService);
  private readonly api = inject(WorkflowApiService);
  private readonly validator = inject(WorkflowValidatorService);
  private readonly snackBar = inject(MatSnackBar);

  /** Available domain options */
  domains = input<DomainInfo[]>([]);

  /** Emitted when user saves */
  saved = output<void>();

  readonly separatorKeyCodes = [ENTER, COMMA] as const;
  contextTags: string[] = [];
  generating = signal(false);

  /**
   * Maps each conflicting context tag to the ID of the first other workflow
   * in the same domain that already uses it. Updated reactively whenever
   * the active prompt, its domain, or the full prompt library changes.
   */
  readonly tagConflicts = computed<Map<string, string>>(() => {
    const prompt = this.builder.activePrompt();
    const allPrompts = this.builder.allPrompts();
    const conflicts = new Map<string, string>();

    if (!prompt.contextTags.length || !allPrompts.length) return conflicts;

    const currentDomains = new Set<string>([
      ...(prompt.domains ?? []),
      ...(prompt.domain ? [prompt.domain] : []),
    ].map(d => d.toLowerCase()));

    // Exclude both the current (possibly renamed) id and the original saved id.
    const selfIds = new Set<string>(
      [prompt.id, this.builder.originalId()].filter(Boolean).map(id => id.toLowerCase())
    );

    const tagOwners = new Map<string, string>();
    for (const other of allPrompts) {
      if (selfIds.has(other.id.toLowerCase())) continue;

      const otherDomains = new Set<string>([
        ...(other.domains ?? []),
        ...(other.domain ? [other.domain] : []),
      ].map(d => d.toLowerCase()));

      const sharesADomain = [...currentDomains].some(d => otherDomains.has(d));
      if (!sharesADomain) continue;

      for (const tag of other.contextTags) {
        const key = tag.toLowerCase();
        if (!tagOwners.has(key)) tagOwners.set(key, other.id);
      }
    }

    for (const tag of prompt.contextTags) {
      const owner = tagOwners.get(tag.toLowerCase());
      if (owner) conflicts.set(tag, owner);
    }

    return conflicts;
  });

  form: FormGroup = this.fb.group({
    id: ['', Validators.required],
    domain: ['', Validators.required],
    description: [''],
    samplePrompt: [''],
    systemPrompt: ['', Validators.required],
    contextTags: [[] as string[]],
    piiCheck: [false],
  });

  constructor() {
    // Re-sync form whenever the active prompt changes (e.g. user selects a different workflow)
    effect(() => {
      const prompt = this.builder.activePrompt();
      this.patchFromPrompt(prompt);
    });

    // Mark fields touched when validation errors target metadata fields
    effect(() => {
      const errors = this.builder.validationErrors();
      if (errors.length > 0) {
        this.form.markAllAsTouched();
      }
    });

    // Keep builder in sync whenever user edits any form field
    this.form.valueChanges.subscribe(() => {
      this.applyMetadata();
    });
  }

  patchFromPrompt(prompt: PromptDefinition): void {
    this.contextTags = [...prompt.contextTags];
    const effectiveDomain = prompt.domains?.length ? prompt.domains[0] : (prompt.domain || '');
    this.form.patchValue({
      id: prompt.id,
      domain: effectiveDomain,
      description: prompt.description,
      samplePrompt: prompt.samplePrompt ?? '',
      systemPrompt: prompt.systemPrompt,
      contextTags: prompt.contextTags,
      piiCheck: prompt.piiCheck,
    }, { emitEvent: false });
  }

  addTag(event: { value: string; chipInput: { clear: () => void } }): void {
    const value = (event.value || '').trim();
    if (value && !this.contextTags.includes(value)) {
      this.contextTags.push(value);
      this.applyMetadata();
    }
    event.chipInput.clear();
  }

  removeTag(tag: string): void {
    this.contextTags = this.contextTags.filter(t => t !== tag);
    this.applyMetadata();
  }

  applyMetadata(): void {
    const val = this.form.getRawValue();
    const domain: string = val.domain ?? '';
    this.builder.updateMetadata({
      id: val.id,
      domain,
      domains: domain ? [domain] : [],
      description: val.description,
      samplePrompt: val.samplePrompt,
      systemPrompt: val.systemPrompt,
      contextTags: this.contextTags,
      piiCheck: val.piiCheck,
    });
  }

  onSave(): void {
    this.applyMetadata();
    this.saved.emit();
  }

  generateSamplePrompt(): void {
    // Sync latest form values into the builder first
    this.applyMetadata();
    const prompt = this.builder.toJSON();

    // Validate all required fields across all tabs
    const errors = this.validator.validatePrompt(
      prompt, this.builder.availableTools(), this.builder.availableTemplates()
    ).filter(e => e.severity === 'error');

    // Also require at least one stage
    if (!prompt.stages || prompt.stages.length === 0) {
      errors.push({ path: 'stages', message: 'At least one stage is required', severity: 'error' });
    }

    if (errors.length > 0) {
      this.snackBar.open(
        'All required fields must be filled in and at least one stage added before generating a sample prompt',
        'Dismiss', { duration: 5000 }
      );
      this.builder.setValidationErrors(
        this.validator.validatePrompt(
          prompt, this.builder.availableTools(), this.builder.availableTemplates()
        )
      );
      return;
    }

    this.generating.set(true);
    this.api.generateSamplePrompt(prompt).subscribe({
      next: (res) => {
        this.form.patchValue({ samplePrompt: res.samplePrompt });
        this.applyMetadata();
        this.snackBar.open('Sample prompt generated', '', { duration: 2000 });
        this.generating.set(false);
      },
      error: (err) => {
        this.snackBar.open(
          `Failed to generate: ${err.error?.error || err.message}`,
          'Dismiss', { duration: 5000 }
        );
        this.generating.set(false);
      },
    });
  }
}
