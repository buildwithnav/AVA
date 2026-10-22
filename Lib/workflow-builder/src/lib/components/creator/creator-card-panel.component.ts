import { Component, inject, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  WorkflowCreatorService,
  CreationStageCard,
  DataFlowConnection,
} from '../../services/workflow-creator.service';

@Component({
  selector: 'wfb-creator-card-panel',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatTooltipModule,
  ],
  template: `
    <div class="creator-card-panel">
      <!-- Similar workflows (shown when agent finds matches) -->
      @if (similarWorkflows().length > 0) {
        <div class="similar-section">
          <div class="similar-header">
            <mat-icon>search</mat-icon>
            <span>Similar existing workflows</span>
          </div>
          @for (match of similarWorkflows(); track match.id) {
            <div class="similar-item">
              <span class="similar-id">{{ match.id }}</span>
              <span class="similar-domain">{{ match.domain }}</span>
              <span class="similar-desc">{{ match.description }}</span>
            </div>
          }
        </div>
      }

      <!-- Empty state -->
      @if (!hasContent()) {
        <div class="empty-state">
          <mat-icon class="empty-icon">widgets</mat-icon>
          <p>Your workflow will appear here as you build it</p>
          <p class="empty-hint">Describe what you want in the chat on the left</p>
        </div>
      }

      @if (hasContent()) {
        <div class="cards-container">
          <!-- Metadata Card -->
          <div class="card metadata-card">
            <div class="card-header">
              <mat-icon>info</mat-icon>
              <span class="card-title">Metadata</span>
            </div>
            <div class="card-body">
              <div class="field-row">
                <mat-form-field appearance="outline" class="field-sm">
                  <mat-label>ID</mat-label>
                  <input matInput [ngModel]="metadata().id" (ngModelChange)="editMetadata('id', $event)" />
                </mat-form-field>
                <mat-form-field appearance="outline" class="field-sm">
                  <mat-label>Domain</mat-label>
                  <input matInput [ngModel]="metadata().domain" (ngModelChange)="editMetadata('domain', $event)" />
                </mat-form-field>
              </div>
              <mat-form-field appearance="outline" class="field-full">
                <mat-label>Description</mat-label>
                <textarea matInput rows="2" [ngModel]="metadata().description" (ngModelChange)="editMetadata('description', $event)"></textarea>
              </mat-form-field>
            </div>
          </div>

          <!-- Stage Cards -->
          @for (stage of stages(); track stage.stageId; let i = $index) {
            <!-- Data flow arrow -->
            @if (i > 0) {
              <div class="data-flow-arrow">
                <div class="arrow-line"></div>
                @if (getConnectionLabels(stages()[i - 1].stageId, stage.stageId).length > 0) {
                  <div class="arrow-labels">
                    @for (label of getConnectionLabels(stages()[i - 1].stageId, stage.stageId); track label) {
                      <span class="token-badge">{{ label }}</span>
                    }
                  </div>
                }
                <mat-icon class="arrow-icon">arrow_downward</mat-icon>
              </div>
            }

            <div
              class="card stage-card"
              [class.is-new]="stage.isNew"
              [class.is-updated]="stage.isUpdated"
            >
              <div class="card-header">
                <span class="stage-type-badge" [class]="'type-' + stage.type">
                  {{ getStageIcon(stage.type) }} {{ stage.type }}
                </span>
                <span class="stage-id">{{ stage.stageId }}</span>
                <button mat-icon-button class="delete-btn" (click)="removeStage(stage.stageId)" matTooltip="Remove stage">
                  <mat-icon>close</mat-icon>
                </button>
              </div>
              <div class="card-body">
                @switch (stage.type) {
                  @case ('ai-extract') {
                    @if (stage.required && stage.required.length) {
                      <div class="field-chips">
                        <span class="field-label">Required:</span>
                        @for (field of stage.required; track field) {
                          <span class="chip">{{ field }}</span>
                        }
                      </div>
                    }
                    @if (stage.prompt) {
                      <div class="prompt-preview">{{ truncate(stage.prompt, 100) }}</div>
                    }
                  }
                  @case ('tool') {
                    <div class="field-row-inline">
                      <span class="field-label">Tool:</span>
                      <code class="tool-id">{{ stage.toolId || '(none)' }}</code>
                    </div>
                    @if (stage.parameters && objectKeys(stage.parameters).length) {
                      <div class="field-row-inline">
                        <span class="field-label">Params:</span>
                        <span class="params-preview">
                          @for (key of objectKeys(stage.parameters!); track key) {
                            <code>{{ key }}={{ truncate(stage.parameters![key], 30) }}</code>
                          }
                        </span>
                      </div>
                    }
                  }
                  @case ('ai-generate') {
                    @if (stage.prompt) {
                      <div class="prompt-preview">{{ truncate(stage.prompt, 120) }}</div>
                    }
                  }
                  @case ('ai-analyze') {
                    @if (stage.prompt) {
                      <div class="prompt-preview">{{ truncate(stage.prompt, 120) }}</div>
                    }
                  }
                  @case ('user-select') {
                    <div class="field-row-inline">
                      <span class="field-label">Options from:</span>
                      <code>{{ stage.optionsFrom || '(none)' }}</code>
                    </div>
                    @if (stage.selectPrompt) {
                      <div class="prompt-preview">{{ truncate(stage.selectPrompt, 80) }}</div>
                    }
                  }
                  @case ('confirmation') {
                    @if (stage.prompt) {
                      <div class="prompt-preview">{{ truncate(stage.prompt, 100) }}</div>
                    }
                  }
                  @case ('agentic') {
                    @if (stage.prompt) {
                      <div class="prompt-preview">{{ truncate(stage.prompt, 120) }}</div>
                    }
                  }
                  @case ('foreach') {
                    <div class="field-row-inline">
                      <span class="field-label">For each:</span>
                      <code>{{ stage.itemsFrom || '(none)' }}</code>
                      <span class="field-label">as</span>
                      <code>{{ stage.itemAlias || 'item' }}</code>
                    </div>
                    <div class="foreach-body">
                      @if (!stage.subStages || stage.subStages.length === 0) {
                        <div class="foreach-empty">No sub-stages yet</div>
                      }
                      @for (sub of stage.subStages || []; track sub.stageId) {
                        <div
                          class="card sub-stage-card"
                          [class.is-new]="sub.isNew"
                          [class.is-updated]="sub.isUpdated"
                        >
                          <div class="card-header">
                            <span class="stage-type-badge" [class]="'type-' + sub.type">
                              {{ getStageIcon(sub.type) }} {{ sub.type }}
                            </span>
                            <span class="stage-id">{{ sub.stageId }}</span>
                            <button mat-icon-button class="delete-btn" (click)="removeStage(sub.stageId)" matTooltip="Remove sub-stage">
                              <mat-icon>close</mat-icon>
                            </button>
                          </div>
                          <div class="card-body">
                            @if (sub.type === 'tool') {
                              <div class="field-row-inline">
                                <span class="field-label">Tool:</span>
                                <code class="tool-id">{{ sub.toolId || '(none)' }}</code>
                              </div>
                            }
                            @if (sub.prompt) {
                              <div class="prompt-preview">{{ truncate(sub.prompt, 100) }}</div>
                            }
                          </div>
                        </div>
                      }
                    </div>
                  }
                  @default {
                    <div class="prompt-preview">{{ stage.type }} stage</div>
                  }
                }
              </div>
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
    }

    .creator-card-panel {
      height: 100%;
      overflow-y: auto;
      padding: 16px;
    }

    .empty-state {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      height: 100%;
      gap: 8px;
      color: var(--mat-sys-on-surface-variant, #888);
      text-align: center;
    }

    .empty-icon { font-size: 48px; width: 48px; height: 48px; opacity: 0.4; }
    .empty-hint { font-size: 12px; opacity: 0.7; }

    .cards-container {
      display: flex;
      flex-direction: column;
      gap: 0;
    }

    .card {
      border: 1px solid var(--mat-sys-outline-variant, #ddd);
      border-radius: 12px;
      overflow: hidden;
      background: var(--mat-sys-surface, #fff);
      transition: box-shadow 0.3s ease, border-color 0.3s ease;
    }

    .card.is-new {
      animation: slideIn 0.3s ease-out;
      border-color: var(--mat-sys-primary, #1976d2);
      box-shadow: 0 0 0 2px rgba(25, 118, 210, 0.2);
    }

    .card.is-updated {
      animation: pulseHighlight 0.8s ease-out;
      border-color: var(--mat-sys-tertiary, #7c4dff);
    }

    @keyframes slideIn {
      from { opacity: 0; transform: translateY(-10px); }
      to { opacity: 1; transform: translateY(0); }
    }

    @keyframes pulseHighlight {
      0% { box-shadow: 0 0 0 0 rgba(124, 77, 255, 0.4); }
      50% { box-shadow: 0 0 0 4px rgba(124, 77, 255, 0.2); }
      100% { box-shadow: 0 0 0 0 rgba(124, 77, 255, 0); }
    }

    .card-header {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 10px 14px;
      background: var(--mat-sys-surface-container-low, #f9f9f9);
      border-bottom: 1px solid var(--mat-sys-outline-variant, #eee);
    }

    .card-title { font-weight: 500; font-size: 13px; }
    .stage-id { font-size: 12px; color: var(--mat-sys-on-surface-variant, #666); margin-left: auto; }

    .stage-type-badge {
      font-size: 11px;
      font-weight: 600;
      padding: 2px 8px;
      border-radius: 4px;
      text-transform: uppercase;
      letter-spacing: 0.5px;
    }

    .type-ai-extract { background: #e3f2fd; color: #1565c0; }
    .type-tool { background: #fff3e0; color: #e65100; }
    .type-ai-generate { background: #f3e5f5; color: #7b1fa2; }
    .type-ai-analyze { background: #e8f5e9; color: #2e7d32; }
    .type-user-select { background: #fce4ec; color: #c62828; }
    .type-confirmation { background: #fff8e1; color: #f57f17; }
    .type-agentic { background: #ede7f6; color: #4527a0; }
    .type-foreach { background: #e0f7fa; color: #00695c; }

    .foreach-body {
      margin-top: 8px;
      padding-left: 12px;
      border-left: 2px dashed #00695c;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .foreach-empty { font-size: 12px; opacity: 0.6; font-style: italic; }
    .sub-stage-card {
      background: var(--mat-sys-surface-container-low, #f8f8f8);
      border-radius: 8px;
      padding: 8px;
    }

    .delete-btn {
      margin-left: auto;
      opacity: 0.5;
      transform: scale(0.8);
    }

    .delete-btn:hover { opacity: 1; }

    .card-body {
      padding: 12px 14px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }

    .field-row {
      display: flex;
      gap: 8px;
    }

    .field-sm { flex: 1; }
    .field-full { width: 100%; }

    .field-sm ::ng-deep .mat-mdc-form-field-subscript-wrapper,
    .field-full ::ng-deep .mat-mdc-form-field-subscript-wrapper {
      display: none;
    }

    .field-chips {
      display: flex;
      align-items: center;
      gap: 6px;
      flex-wrap: wrap;
    }

    .field-label {
      font-size: 11px;
      font-weight: 500;
      color: var(--mat-sys-on-surface-variant, #666);
    }

    .chip {
      font-size: 11px;
      padding: 2px 8px;
      border-radius: 10px;
      background: var(--mat-sys-secondary-container, #e8e8e8);
      color: var(--mat-sys-on-secondary-container, #333);
    }

    .field-row-inline {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 12px;
    }

    .tool-id {
      font-size: 12px;
      background: #f5f5f5;
      padding: 2px 6px;
      border-radius: 4px;
    }

    .params-preview {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }

    .params-preview code {
      font-size: 11px;
      background: #f5f5f5;
      padding: 1px 5px;
      border-radius: 3px;
    }

    .prompt-preview {
      font-size: 12px;
      color: var(--mat-sys-on-surface-variant, #555);
      background: var(--mat-sys-surface-container-lowest, #fafafa);
      padding: 8px;
      border-radius: 6px;
      white-space: pre-wrap;
      line-height: 1.4;
    }

    /* Data Flow Arrows */
    .data-flow-arrow {
      display: flex;
      flex-direction: column;
      align-items: center;
      padding: 4px 0;
      position: relative;
    }

    .arrow-line {
      width: 2px;
      height: 12px;
      background: var(--mat-sys-outline, #ccc);
    }

    .arrow-labels {
      display: flex;
      gap: 4px;
      flex-wrap: wrap;
      justify-content: center;
      padding: 2px 0;
    }

    .token-badge {
      font-size: 10px;
      font-family: monospace;
      padding: 1px 6px;
      border-radius: 8px;
      background: var(--mat-sys-primary-container, #bbdefb);
      color: var(--mat-sys-on-primary-container, #0d47a1);
    }

    .arrow-icon {
      font-size: 16px;
      width: 16px;
      height: 16px;
      color: var(--mat-sys-outline, #ccc);
    }

    /* Similar workflows section */
    .similar-section {
      margin-bottom: 16px;
      border: 1px solid var(--mat-sys-outline-variant, #ddd);
      border-radius: 8px;
      overflow: hidden;
    }

    .similar-header {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 12px;
      background: var(--mat-sys-surface-container-low, #f9f9f9);
      border-bottom: 1px solid var(--mat-sys-outline-variant, #eee);
      font-size: 12px;
      font-weight: 500;
    }

    .similar-item {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 12px;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #f0f0f0);
      font-size: 12px;
    }

    .similar-item:last-child { border-bottom: none; }
    .similar-id { font-weight: 500; color: var(--mat-sys-primary, #1976d2); }
    .similar-domain { font-size: 11px; padding: 1px 6px; background: #f0f0f0; border-radius: 4px; }
    .similar-desc { flex: 1; color: var(--mat-sys-on-surface-variant, #666); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  `],
})
export class CreatorCardPanelComponent {
  private readonly creator = inject(WorkflowCreatorService);

  readonly metadata = computed(() => this.creator.draft().metadata);
  readonly stages = computed(() => this.creator.draft().stages);
  readonly dataFlow = computed(() => this.creator.draft().dataFlow);
  readonly similarWorkflows = this.creator.similarWorkflows;
  readonly hasContent = this.creator.hasContent;

  editMetadata(field: string, value: any): void {
    this.creator.applyUserEdit(null, field, value);
  }

  removeStage(stageId: string): void {
    this.creator.removeStageById(stageId);
  }

  getConnectionLabels(fromId: string, toId: string): string[] {
    return this.dataFlow()
      .filter(c => c.fromStageId === fromId && c.toStageId === toId)
      .map(c => c.tokenRef);
  }

  getStageIcon(type: string): string {
    const icons: Record<string, string> = {
      'ai-extract': '🧠',
      'tool': '🔧',
      'ai-generate': '✨',
      'ai-analyze': '📊',
      'user-select': '👆',
      'confirmation': '✅',
      'foreach': '🔁',
      'agentic': '🤖',
    };
    return icons[type] || '📦';
  }

  truncate(text: string, max: number): string {
    return text.length > max ? text.slice(0, max) + '...' : text;
  }

  objectKeys(obj: Record<string, any>): string[] {
    return Object.keys(obj);
  }
}
