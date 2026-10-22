import {
  Component, EventEmitter, Input, Output, inject,
  ViewEncapsulation,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatIconModule } from '@angular/material/icon';
import { forkJoin, of } from 'rxjs';
import { catchError, tap } from 'rxjs/operators';
import { ChatService } from '../../services/chat';
import { SaveArtifact, SaveConfirmationPayload, PromptDefinitionData, DomainDefinitionData } from '../../models/chat.models';

export interface SaveResult {
  success: boolean;
  saved: SaveArtifact[];
  errors: { artifact: SaveArtifact; error: string }[];
}

/**
 * Inline confirmation panel shown in the chat after the user completes
 * the workflow builder form. Lists all artifacts that will be created/updated
 * and provides Save All / Cancel buttons.
 */
@Component({
  selector: 'app-save-confirmation-panel',
  standalone: true,
  imports: [
    CommonModule, MatCardModule, MatButtonModule,
    MatProgressBarModule, MatTooltipModule, MatIconModule,
  ],
  template: `
    <mat-card class="scp-card">
      <mat-card-header>
        <mat-card-title>
          <mat-icon style="color: #2e7d32;">check_circle</mat-icon>
          Ready to Save
        </mat-card-title>
      </mat-card-header>
      <mat-card-content>
        <p class="scp-summary">The following will be {{ totalCreates > 0 && totalUpdates > 0 ? 'created and updated' : totalCreates > 0 ? 'created' : 'updated' }}:</p>

        @for (artifact of payload.artifacts; track artifact.id) {
          <div class="scp-artifact" [class.scp-saved]="savedIds.has(artifact.id)" [class.scp-error]="errorIds.has(artifact.id)">
            <div class="scp-artifact-header">
              <mat-icon>{{getArtifactIcon(artifact.type)}}</mat-icon>
              <span class="scp-artifact-type">{{ artifact.type | titlecase }}</span>
              <span class="scp-artifact-id">{{ artifact.id }}</span>
              <span class="scp-artifact-action" [class.scp-create]="artifact.action === 'create'" [class.scp-update]="artifact.action === 'update'">
                {{ artifact.action }}
              </span>
              @if (savedIds.has(artifact.id)) {
                <mat-icon style="color: #2e7d32;">check</mat-icon>
              }
              @if (errorIds.has(artifact.id)) {
                <mat-icon style="color: #d32f2f;">warning</mat-icon>
              }
            </div>
          </div>
        }

        @if (saving) {
          <mat-progress-bar mode="indeterminate" class="scp-progress"></mat-progress-bar>
        }

        @if (errorMessage) {
          <div class="scp-error-message">
            <mat-icon>warning</mat-icon> {{ errorMessage }}
          </div>
        }

        @if (savedAll) {
          <div class="scp-success-message">
            <mat-icon>check_circle</mat-icon> All artifacts saved successfully!
          </div>
        }
      </mat-card-content>
      <mat-card-actions align="end">
        @if (!savedAll) {
          <button mat-button (click)="onCancel()" [disabled]="saving">Cancel</button>
          <button mat-raised-button color="primary" (click)="onSaveAll()" [disabled]="saving">
            <mat-icon>save</mat-icon> Save All
          </button>
        } @else {
          <button mat-raised-button color="primary" (click)="onDone()">
            <mat-icon>check</mat-icon> Done
          </button>
        }
      </mat-card-actions>
    </mat-card>
  `,
  styles: [`
    .scp-card {
      border-left: 4px solid #2e7d32;
    }
    mat-card-title {
      display: flex; align-items: center; gap: 8px; font-size: 15px;
    }
    .scp-summary { font-size: 13px; margin-bottom: 10px; opacity: 0.8; }
    .scp-artifact {
      padding: 8px 12px;
      border: 1px solid #e0e0e0;
      border-radius: 6px;
      margin-bottom: 6px;
      transition: background 0.2s;
      &.scp-saved { background: #e8f5e9; border-color: #a5d6a7; }
      &.scp-error { background: #ffebee; border-color: #ef9a9a; }
    }
    .scp-artifact-header {
      display: flex; align-items: center; gap: 8px; font-size: 13px;
    }
    .scp-artifact-type { font-weight: 500; }
    .scp-artifact-id { font-family: monospace; font-size: 12px; }
    .scp-artifact-action {
      padding: 1px 6px; border-radius: 3px; font-size: 10px; font-weight: 600; text-transform: uppercase;
      &.scp-create { background: #e3f2fd; color: #1565c0; }
      &.scp-update { background: #fff3e0; color: #e65100; }
    }
    .scp-progress { margin: 12px 0; }
    .scp-error-message {
      color: #d32f2f; font-size: 13px; padding: 8px; margin-top: 8px;
      background: #ffebee; border-radius: 4px;
    }
    .scp-success-message {
      color: #2e7d32; font-size: 13px; padding: 8px; margin-top: 8px;
      background: #e8f5e9; border-radius: 4px;
    }
  `],
  encapsulation: ViewEncapsulation.None,
})
export class SaveConfirmationPanel {
  @Input() payload!: SaveConfirmationPayload;
  @Output() saved = new EventEmitter<SaveResult>();
  @Output() cancelled = new EventEmitter<void>();

  private chatService = inject(ChatService);

  saving = false;
  savedAll = false;
  savedIds = new Set<string>();
  errorIds = new Set<string>();
  errorMessage = '';

  get totalCreates(): number {
    return this.payload.artifacts.filter(a => a.action === 'create').length;
  }

  get totalUpdates(): number {
    return this.payload.artifacts.filter(a => a.action === 'update').length;
  }

  getArtifactIcon(type: string): string {
    switch (type) {
      case 'prompt': return 'account_tree';
      case 'template': return 'description';
      case 'domain': return 'grid_view';
      default: return 'insert_drive_file';
    }
  }

  onSaveAll(): void {
    this.saving = true;
    this.errorMessage = '';
    this.savedIds.clear();
    this.errorIds.clear();

    const operations = this.payload.artifacts.map(artifact => {
      const obs = this.createSaveOperation(artifact);
      return obs.pipe(
        tap(() => this.savedIds.add(artifact.id)),
        catchError(err => {
          this.errorIds.add(artifact.id);
          return of({ error: err.error?.error || err.message || 'Unknown error', artifact });
        }),
      );
    });

    forkJoin(operations).subscribe({
      next: (results) => {
        this.saving = false;
        const errors = results.filter((r: any) => r?.error).map((r: any) => ({
          artifact: r.artifact,
          error: r.error,
        }));

        if (errors.length > 0) {
          this.errorMessage = `${errors.length} artifact(s) failed to save: ${errors.map(e => e.artifact.id).join(', ')}`;
          this.saved.emit({ success: false, saved: this.payload.artifacts.filter(a => this.savedIds.has(a.id)), errors });
        } else {
          this.savedAll = true;
          this.saved.emit({ success: true, saved: this.payload.artifacts, errors: [] });
        }
      },
      error: () => {
        this.saving = false;
        this.errorMessage = 'An unexpected error occurred while saving.';
      },
    });
  }

  private createSaveOperation(artifact: SaveArtifact) {
    switch (artifact.type) {
      case 'prompt':
        return artifact.action === 'create'
          ? this.chatService.createPrompt(artifact.data as PromptDefinitionData)
          : this.chatService.updatePrompt(artifact.id, artifact.data as PromptDefinitionData);
      case 'template':
        return artifact.action === 'create'
          ? this.chatService.createTemplate(artifact.data)
          : this.chatService.updateTemplate(artifact.id, artifact.data);
      case 'domain':
        return artifact.action === 'create'
          ? this.chatService.createDomain(artifact.data as DomainDefinitionData)
          : this.chatService.updateDomain(artifact.id, artifact.data as DomainDefinitionData);
      default:
        return of(null);
    }
  }

  onCancel(): void {
    this.cancelled.emit();
  }

  onDone(): void {
    this.saved.emit({ success: true, saved: this.payload.artifacts, errors: [] });
  }
}
