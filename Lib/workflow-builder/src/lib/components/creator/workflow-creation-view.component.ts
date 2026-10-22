import { Component, inject, output, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { WorkflowCreatorService } from '../../services/workflow-creator.service';
import { WorkflowBuilderService } from '../../services/workflow-builder.service';
import { WorkflowApiService } from '../../services/workflow-api.service';
import { WorkflowValidatorService } from '../../services/workflow-validator.service';
import { PromptDefinition, ChangelogEntry } from '../../models/prompt.model';
import { CreatorChatPanelComponent } from './creator-chat-panel.component';
import { CreatorCardPanelComponent } from './creator-card-panel.component';

@Component({
  selector: 'wfb-workflow-creation-view',
  standalone: true,
  imports: [
    CommonModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    CreatorChatPanelComponent,
    CreatorCardPanelComponent,
  ],
  template: `
    <div class="creation-view">
      <div class="creation-header">
        <button mat-icon-button (click)="exit()" matTooltip="Back to workflow list">
          <mat-icon>arrow_back</mat-icon>
        </button>
        <h2>{{ isEditing() ? 'Edit Workflow: ' + (editingId() || '') : 'Create New Workflow' }}</h2>
        <div class="header-actions">
          @if (!isEditing()) {
            <button
              mat-stroked-button
              (click)="skipToBlank()"
              matTooltip="Open blank editor without AI assistance"
            >
              Skip to Editor
            </button>
          }
          <button
            mat-flat-button
            color="primary"
            [disabled]="!hasContent()"
            (click)="saveAndOpen()"
          >
            <mat-icon>save</mat-icon>
            {{ isEditing() ? 'Save Changes' : 'Save & Open' }}
          </button>
        </div>
      </div>

      <div class="creation-body">
        <div class="panel chat-panel">
          <wfb-creator-chat-panel />
        </div>
        <div class="panel-divider"></div>
        <div class="panel card-panel">
          <wfb-creator-card-panel />
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      height: 100%;
    }

    .creation-view {
      display: flex;
      flex-direction: column;
      height: 100%;
      background: var(--mat-sys-surface, #fff);
    }

    .creation-header {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 8px 16px;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #ddd);
      flex-shrink: 0;
    }

    .creation-header h2 {
      margin: 0;
      font-size: 16px;
      font-weight: 500;
    }

    .header-actions {
      margin-left: auto;
      display: flex;
      gap: 8px;
    }

    .creation-body {
      flex: 1;
      display: flex;
      overflow: hidden;
    }

    .panel {
      overflow: hidden;
    }

    .chat-panel {
      flex: 0 0 45%;
      max-width: 500px;
      min-width: 320px;
      border-right: 1px solid var(--mat-sys-outline-variant, #ddd);
    }

    .panel-divider {
      width: 4px;
      cursor: col-resize;
      background: transparent;
      transition: background 0.15s;
    }

    .panel-divider:hover {
      background: var(--mat-sys-primary, #1976d2);
      opacity: 0.3;
    }

    .card-panel {
      flex: 1;
      min-width: 320px;
    }
  `],
})
export class WorkflowCreationViewComponent {
  private readonly creator = inject(WorkflowCreatorService);
  private readonly builder = inject(WorkflowBuilderService);
  private readonly api = inject(WorkflowApiService);
  private readonly validator = inject(WorkflowValidatorService);

  readonly exitCreation = output<void>();
  readonly openBlankEditor = output<void>();

  readonly hasContent = this.creator.hasContent;
  readonly isEditing = computed(() => this.creator.mode() === 'edit');
  readonly editingId = this.creator.editingId;

  exit(): void {
    this.creator.endSession();
    this.exitCreation.emit();
  }

  skipToBlank(): void {
    this.creator.endSession();
    this.openBlankEditor.emit();
  }

  async saveAndOpen(): Promise<void> {
    if (!this.creator.canSave()) {
      this.creator.notifyMissingSystemPrompt();
      return;
    }
    const saved = this.isEditing()
      ? await this.saveEdit(this.creator.asPromptDefinition())
      : await this.saveWithUniqueId(this.creator.asPromptDefinition());
    if (saved) {
      this.builder.loadPrompt(saved);
      // Navigate to the Test tab so user can immediately try the workflow
      this.builder.selectedTabIndex.set(3);
      this.creator.endSession();
      this.exitCreation.emit();
    }
  }

  /**
   * Save an edit in place: validate the final converted prompt, lock the id,
   * perform an optimistic version check, append a changelog entry, then update.
   */
  private async saveEdit(prompt: PromptDefinition): Promise<PromptDefinition | null> {
    const editingId = this.creator.editingId();
    if (!editingId) {
      console.error('saveEdit called without an editingId');
      return null;
    }

    // Identity lock — never let the agent change the workflow id on an edit
    const candidate: PromptDefinition = { ...prompt, id: editingId };

    // Validate the EXACT object being saved (post-conversion, recurses foreach)
    const errors = this.validator.validatePrompt(
      candidate, this.builder.availableTools(), this.builder.availableTemplates()
    );
    if (errors.some(e => e.severity === 'error')) {
      this.creator.postAssistantMessage(
        `I can't save yet — the workflow has ${errors.filter(e => e.severity === 'error').length} validation error(s):\n` +
        errors.filter(e => e.severity === 'error').map(e => `- ${e.message}`).join('\n')
      );
      return null;
    }

    // Optimistic concurrency: fetch the latest server copy. If this fails we must NOT
    // proceed — overwriting blind would skip the version check and wipe the changelog history.
    let latest: PromptDefinition | undefined;
    try {
      latest = await this.api.getPrompt(editingId).toPromise();
    } catch {
      this.creator.postAssistantMessage(
        `I couldn't load the latest copy of '${editingId}' to save safely (the server may be unavailable). ` +
        `Your changes are still here — please try saving again in a moment.`
      );
      return null;
    }

    const startVersion = this.creator.originalVersion();
    if (latest && latest.version !== startVersion) {
      const proceed = confirm(
        `'${editingId}' was modified elsewhere since you started editing (server version "${latest.version ?? 'none'}", ` +
        `your base "${startVersion ?? 'none'}"). Overwrite with your AI edits anyway?`
      );
      if (!proceed) return null;
    }

    // Append a changelog entry and bump the version (relative to the latest server state)
    const baseVersion = latest?.version ?? startVersion;
    const newVersion = nextVersion(baseVersion);
    const changelog: ChangelogEntry[] = [
      ...(latest?.changelog ?? []),
      {
        version: newVersion,
        timestamp: new Date().toISOString(),
        author: 'workflow-creator',
        description: 'AI-assisted edit',
      },
    ];
    const toSave: PromptDefinition = {
      ...candidate,
      version: newVersion,
      changelog,
      createdBy: latest?.createdBy ?? candidate.createdBy,
    };

    try {
      const result = await this.api.updatePrompt(editingId, toSave).toPromise();
      return result ?? toSave;
    } catch (err) {
      console.error('Failed to save edited workflow:', err);
      this.creator.postAssistantMessage(`Save failed: ${err instanceof Error ? err.message : 'unknown error'}`);
      return null;
    }
  }

  /** Attempt to save, auto-appending a numeric suffix if the ID already exists */
  private async saveWithUniqueId(prompt: PromptDefinition, maxRetries = 10): Promise<PromptDefinition | null> {
    const baseId = prompt.id;
    let candidate = { ...prompt };

    for (let attempt = 0; attempt <= maxRetries; attempt++) {
      if (attempt > 0) {
        candidate = { ...prompt, id: `${baseId}-${attempt + 1}` };
      }
      try {
        const result = await this.api.createPrompt(candidate).toPromise();
        return result ?? candidate;
      } catch (err) {
        if (err instanceof HttpErrorResponse && err.status === 409) {
          // ID conflict — try next suffix
          continue;
        }
        console.error('Failed to save workflow:', err);
        return null;
      }
    }
    console.error(`Failed to save workflow: all IDs from '${baseId}' to '${baseId}-${maxRetries + 1}' are taken.`);
    return null;
  }
}

/** Compute the next semver-ish patch version (e.g., "1.2" -> "1.3", undefined -> "1.1"). */
export function nextVersion(current?: string): string {
  if (!current) return '1.1';
  const parts = current.split('.');
  const last = parseInt(parts[parts.length - 1], 10);
  if (Number.isNaN(last)) return `${current}.1`;
  parts[parts.length - 1] = String(last + 1);
  return parts.join('.');
}
