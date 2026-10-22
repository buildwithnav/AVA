import { Component, inject, input, output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { WorkflowBuilderService, BuilderMode } from '../../services/workflow-builder.service';
import { WorkflowApiService } from '../../services/workflow-api.service';
import { WorkflowValidatorService, ValidationError } from '../../services/workflow-validator.service';
import { DomainInfo } from '../../models';
import { MetadataFormComponent } from '../form-builder/metadata-form/metadata-form.component';
import { StageListComponent } from '../form-builder/stage-list/stage-list.component';
import { ToolBindingPanelComponent } from '../form-builder/tool-binding-panel/tool-binding-panel.component';
import { JsonPreviewComponent } from '../shared/json-preview/json-preview.component';
import { WorkflowCanvasComponent } from '../canvas/workflow-canvas.component';
import { LivePreviewComponent } from '../shared/live-preview/live-preview.component';

/**
 * Top-level workflow editor — hosts both form mode and canvas mode.
 * Contains the toolbar with save/undo/redo/mode-toggle and tab layout.
 */
@Component({
  selector: 'wfb-workflow-editor',
  standalone: true,
  imports: [
    CommonModule, MatCardModule, MatButtonModule, MatIconModule,
    MatTabsModule, MatToolbarModule, MatTooltipModule, MatSnackBarModule,
    MetadataFormComponent, StageListComponent,
    ToolBindingPanelComponent,
    JsonPreviewComponent,
    WorkflowCanvasComponent,
    LivePreviewComponent,
  ],
  host: { style: 'display:flex;flex-direction:column;height:100%;min-height:0' },
  template: `
    <div class="wfb-editor">
      <!-- Toolbar -->
      <mat-toolbar class="wfb-toolbar" color="primary">
        <span class="wfb-title">
          <mat-icon>schema</mat-icon>
          Workflow Builder
        </span>
        <span class="wfb-spacer"></span>

        @if (builder.isDirty()) {
          <mat-icon class="wfb-dirty-indicator" matTooltip="Unsaved changes">
            fiber_manual_record
          </mat-icon>
        }

        <button mat-icon-button matTooltip="Undo" [disabled]="!builder.canUndo()"
                (click)="builder.undo()">
          <mat-icon>undo</mat-icon>
        </button>
        <button mat-icon-button matTooltip="Redo" [disabled]="!builder.canRedo()"
                (click)="builder.redo()">
          <mat-icon>redo</mat-icon>
        </button>

        <button mat-icon-button
                [matTooltip]="builder.mode() === 'form' ? 'Switch to Canvas' : 'Switch to Form'"
                (click)="builder.toggleMode()">
          <mat-icon>{{ builder.mode() === 'form' ? 'account_tree' : 'list_alt' }}</mat-icon>
        </button>

        <button mat-icon-button matTooltip="Archive workflow" (click)="archive()">
          <mat-icon>inventory_2</mat-icon>
        </button>

        @if (aiEditEnabled()) {
          <button mat-flat-button class="wfb-ai-btn" matTooltip="Edit this workflow with AI"
                  (click)="editWithAi.emit()">
            <mat-icon>auto_awesome</mat-icon> Edit with AI
          </button>
        }

        <button mat-flat-button (click)="validate()">
          <mat-icon>check_circle</mat-icon> Validate
        </button>
        <button mat-flat-button (click)="save()">
          <mat-icon>save</mat-icon> Save
        </button>
      </mat-toolbar>

      <!-- Form Mode -->
      @if (builder.mode() === 'form') {
        <div class="wfb-form-layout">
          <mat-tab-group [selectedIndex]="builder.selectedTabIndex()"
                         (selectedIndexChange)="builder.selectedTabIndex.set($event)">
            <mat-tab label="Metadata">
              <wfb-metadata-form [domains]="domains()" (saved)="save()" />
            </mat-tab>
            <mat-tab label="Stages">
              <wfb-stage-list />
            </mat-tab>
            <mat-tab label="Tools">
              <wfb-tool-binding-panel />
            </mat-tab>
            <mat-tab label="Test">
              <wfb-live-preview />
            </mat-tab>
            <mat-tab label="JSON">
              <wfb-json-preview />
            </mat-tab>
          </mat-tab-group>
        </div>
      }

      <!-- Canvas Mode -->
      @if (builder.mode() === 'canvas') {
        <wfb-canvas />
      }
    </div>
  `,
  styles: [`
    .wfb-editor { display: flex; flex-direction: column; height: 100%; overflow: hidden; }
    .wfb-toolbar {
      gap: 4px; flex-shrink: 0;
      .wfb-title {
        display: flex; align-items: center; gap: 8px; font-size: 16px; font-weight: 500;
      }
    }
    .wfb-spacer { flex: 1; }
    .wfb-dirty-indicator { color: #f4b400; font-size: 12px; }
    .wfb-ai-btn {
      background: #7c4dff;
      color: #fff;
    }
    .wfb-ai-btn:hover { background: #6a3df0; }
    .wfb-form-layout {
      flex: 1; min-height: 0; display: flex; flex-direction: column; overflow: hidden;
      ::ng-deep {
        .mat-mdc-tab-group { display: flex; flex-direction: column; flex: 1; min-height: 0; }
        .mat-mdc-tab-body-wrapper { flex: 1; min-height: 0; }
        .mat-mdc-tab-body-content { display: flex; flex-direction: column; height: 100%; }
        .wfb-validation-snackbar .mdc-snackbar__label { white-space: pre-line; }
      }
    }
  `],
})
export class WorkflowEditorComponent {
  readonly builder = inject(WorkflowBuilderService);
  private readonly api = inject(WorkflowApiService);
  private readonly validator = inject(WorkflowValidatorService);
  private readonly snackBar = inject(MatSnackBar);

  /** Available domains for the metadata form */
  domains = input<DomainInfo[]>([]);

  /** Whether to show the "Edit with AI" button (host wires the AI edit view). */
  aiEditEnabled = input<boolean>(true);

  /** Emitted after a successful save or create */
  saved = output<void>();

  /** Emitted when user clicks Archive — parent handles API call */
  archived = output<string>();

  /** Emitted when user clicks "Edit with AI" — parent opens the AI edit view */
  editWithAi = output<void>();

  archive(): void {
    const id = this.builder.activePrompt()?.id;
    if (!id) return;
    if (!confirm(`Archive workflow '${id}'? It won't appear in suggestions or auto-routing, but can be restored later.`)) return;
    this.archived.emit(id);
  }

  async save(): Promise<void> {
    const prompt = this.builder.toJSON();
    const errors = [
      ...this.validator.validatePrompt(prompt, this.builder.availableTools(), this.builder.availableTemplates()),
      ...this.validator.validateContextTagUniqueness(prompt, this.builder.allPrompts(), this.builder.originalId()),
    ];
    const hasErrors = errors.some(e => e.severity === 'error');

    if (hasErrors) {
      this.snackBar.open(
        `Cannot save: ${errors.filter(e => e.severity === 'error').length} validation error(s)`,
        'Dismiss', { duration: 5000 }
      );
      return;
    }

    const originalId = this.builder.originalId();

    try {
      if (originalId) {
        // Editing an existing workflow — PUT using the original ID so a rename
        // doesn't create a duplicate. The backend also updates the id column.
        this.api.updatePrompt(originalId, prompt).subscribe({
          next: () => {
            this.builder.markSaved();
            this.saved.emit();
            this.snackBar.open('Workflow saved', '', { duration: 2000 });
          },
          error: (err) => {
            this.snackBar.open(`Save failed: ${err.error?.error || err.message}`, 'Dismiss', { duration: 5000 });
          },
        });
      } else {
        // New workflow — create
        this.api.createPrompt(prompt).subscribe({
          next: () => {
            this.builder.markSaved();
            this.saved.emit();
            this.snackBar.open('Workflow created', '', { duration: 2000 });
          },
          error: (err) => {
            this.snackBar.open(`Save failed: ${err.error?.error || err.message}`, 'Dismiss', { duration: 5000 });
          },
        });
      }
    } catch (err) {
      this.snackBar.open('Save failed', 'Dismiss', { duration: 5000 });
    }
  }

  validate(): void {
    const prompt = this.builder.toJSON();
    const errors = [
      ...this.validator.validatePrompt(prompt, this.builder.availableTools(), this.builder.availableTemplates()),
      ...this.validator.validateContextTagUniqueness(prompt, this.builder.allPrompts(), this.builder.originalId()),
    ];

    this.builder.setValidationErrors(errors);

    if (errors.length === 0) {
      this.snackBar.open('✓ No validation issues', '', { duration: 2000 });
      return;
    }

    // Navigate to the first error
    this.navigateToError(errors[0]);

    // Build descriptive message showing all issues
    const messages = errors.map(e => `${e.severity === 'error' ? '❌' : '⚠️'} ${e.message}`);
    const summary = messages.slice(0, 5).join('\n');
    const extra = errors.length > 5 ? `\n...and ${errors.length - 5} more` : '';

    this.snackBar.open(summary + extra, 'Dismiss', {
      duration: 10000,
      panelClass: 'wfb-validation-snackbar',
    });
  }

  /** Navigate to the tab and stage that contains the given error */
  private navigateToError(error: ValidationError): void {
    const path = error.path;

    if (path.startsWith('stages[')) {
      this.builder.selectedTabIndex.set(1);

      // Most specific first: depth-2 path e.g. stages[2].stages[1].stages[0].field
      const deepMatch = path.match(/^stages\[(\d+)\]\.stages\[(\d+)\]\.stages\[(\d+)\]/);
      if (deepMatch) {
        const pi = parseInt(deepMatch[1], 10);
        const si = parseInt(deepMatch[2], 10);
        const ci = parseInt(deepMatch[3], 10);
        this.builder.selectStage(pi);
        this.builder.selectedSubStage.set({ parent: pi, child: si });
        this.builder.selectedDeepStage.set({ parent: pi, sub: si, child: ci });
        return;
      }

      // Depth-1: stages[2].stages[1].field
      const subMatch = path.match(/^stages\[(\d+)\]\.stages\[(\d+)\]/);
      if (subMatch) {
        const pi = parseInt(subMatch[1], 10);
        const ci = parseInt(subMatch[2], 10);
        this.builder.selectStage(pi);
        this.builder.selectedSubStage.set({ parent: pi, child: ci });
        this.builder.selectedDeepStage.set(null);
        return;
      }

      // Top-level: stages[2].field
      const topMatch = path.match(/^stages\[(\d+)\]/);
      if (topMatch) {
        this.builder.selectStage(parseInt(topMatch[1], 10));
        this.builder.selectedSubStage.set(null);
        this.builder.selectedDeepStage.set(null);
      }
    } else {
      // Metadata fields — switch to Metadata tab (index 0)
      this.builder.selectedTabIndex.set(0);
    }
  }
}
