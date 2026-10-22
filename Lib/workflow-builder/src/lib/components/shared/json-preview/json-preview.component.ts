import { Component, inject, viewChild, ElementRef, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { WorkflowBuilderService } from '../../../services/workflow-builder.service';

/**
 * Live JSON preview of the current workflow definition.
 * Shows the prompt as it would appear in prompts.json.
 */
@Component({
  selector: 'wfb-json-preview',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatIconModule, MatSnackBarModule],
  host: { style: 'display:flex;flex-direction:column;flex:1;min-height:0;height:100%' },
  template: `
    <div class="wfb-json-preview">
      <div class="wfb-json-toolbar">
        <span class="wfb-json-title">prompts.json preview</span>
        <button mat-icon-button (click)="copyToClipboard()" matTooltip="Copy JSON">
          <mat-icon>content_copy</mat-icon>
        </button>
      </div>
      <pre class="wfb-json-content" #jsonContent>{{ jsonOutput() }}</pre>
    </div>
  `,
  styles: [`
    .wfb-json-preview {
      display: flex; flex-direction: column; flex: 1; min-height: 0;
      padding: 16px; padding-bottom: 8px;
    }
    .wfb-json-toolbar {
      display: flex; align-items: center; gap: 8px; margin-bottom: 8px; flex-shrink: 0;
      .wfb-json-title { flex: 1; font-weight: 500; }
    }
    .wfb-json-content {
      background: var(--mat-sys-surface-container, #f5f5f5);
      border-radius: 8px;
      padding: 16px;
      font-size: 12px;
      line-height: 1.5;
      overflow: auto;
      flex: 1;
      min-height: 0;
      margin-bottom: 8px;
      white-space: pre-wrap;
      word-break: break-word;
    }
  `],
})
export class JsonPreviewComponent {
  private readonly builder = inject(WorkflowBuilderService);
  private readonly snackBar = inject(MatSnackBar);

  private readonly jsonContentRef = viewChild<ElementRef<HTMLPreElement>>('jsonContent');

  constructor() {
    // Reset scroll when the active prompt changes (i.e. user selects a different workflow)
    effect(() => {
      this.builder.activePrompt(); // track signal
      const el = this.jsonContentRef()?.nativeElement;
      if (el) el.scrollTop = 0;
    });
  }

  jsonOutput = () => JSON.stringify(this.builder.toJSON(), null, 2);

  async copyToClipboard(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.jsonOutput());
      this.snackBar.open('Copied to clipboard', '', { duration: 1500 });
    } catch {
      this.snackBar.open('Failed to copy', 'Dismiss', { duration: 3000 });
    }
  }
}
