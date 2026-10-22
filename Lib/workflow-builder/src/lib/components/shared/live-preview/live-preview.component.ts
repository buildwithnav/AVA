import { Component, inject, signal, input, OnDestroy, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatExpansionModule } from '@angular/material/expansion';
import { WorkflowBuilderService } from '../../../services/workflow-builder.service';
import { WorkflowApiService } from '../../../services/workflow-api.service';
import { getStageMeta } from '../../../shared/stage-meta';

interface ToolCallEntry {
  tool: string;
  params?: string;
  result?: string;
  error?: string;
  isError?: boolean;
  iteration?: number;
  tokensUsed?: number;
}

interface StageExecution {
  stageId: string;
  type: string;
  status: 'running' | 'done' | 'error' | 'paused';
  output: string;
  stepResult?: {
    toolName: string;
    success: boolean;
    result?: unknown;
    error?: string;
    durationMs?: number;
  };
  toolCalls?: ToolCallEntry[];
  error?: string;
  tokensUsed?: number;
}

interface PreviewRun {
  status: 'idle' | 'running' | 'done' | 'error' | 'paused';
  agentRunId?: string;
  resumeRunId?: string;
  stages: StageExecution[];
  error?: string;
  tokens?: number;
  /** Prompt/message shown to the user while the workflow is paused for input */
  pausePrompt?: string;
  /** Options for a paused user-select stage (rendered as choice buttons) */
  selectOptions?: { label: string; value: string }[];
}

/**
 * Live workflow preview — sends the current workflow definition + a test
 * message to the /api/chat/test-workflow endpoint and displays SSE event
 * results in real-time, showing each stage with the same icons/colors
 * used in the Stages tab. Supports pause/resume for interactive stages.
 */
@Component({
  selector: 'wfb-live-preview',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatButtonModule, MatIconModule, MatInputModule, MatFormFieldModule,
    MatProgressBarModule, MatExpansionModule,
  ],
  template: `
    <div class="preview-container">
      <h3 class="preview-title">
        <mat-icon>play_circle</mat-icon>
        Test Workflow
      </h3>

      <!-- Test message input -->
      <mat-form-field appearance="outline" class="preview-input">
        <mat-label>Test message</mat-label>
        <textarea matInput [(ngModel)]="testMessage" rows="2"
                  placeholder="Enter a test message that would trigger this workflow..."
                  [disabled]="run().status === 'running' || run().status === 'paused'"></textarea>
      </mat-form-field>

      <div class="preview-actions">
        <button mat-flat-button color="primary"
                [disabled]="!testMessage.trim() || run().status === 'running' || run().status === 'paused'"
                (click)="startTest()">
          <mat-icon>play_arrow</mat-icon>
          {{ run().status === 'running' ? 'Running...' : 'Run Test' }}
        </button>
        @if (run().status !== 'idle') {
          <button mat-stroked-button (click)="reset()">
            <mat-icon>refresh</mat-icon> Reset
          </button>
        }
      </div>

      @if (run().status === 'running') {
        <mat-progress-bar mode="indeterminate" class="preview-progress" />
      }

      <!-- Stage Results -->
      @if (run().stages.length > 0 || run().error) {
        <div class="preview-results">
          <h4>Execution Trace</h4>

          @for (stage of run().stages; track $index) {
            <mat-expansion-panel [expanded]="true">
              <mat-expansion-panel-header>
                <mat-panel-title class="stage-panel-title">
                  <mat-icon [style.color]="getMeta(stage.type).color">
                    {{ getMeta(stage.type).icon }}
                  </mat-icon>
                  <span class="stage-id">{{ stage.stageId }}</span>
                  <span class="stage-type-chip" [style.background]="getMeta(stage.type).color">
                    {{ getMeta(stage.type).label }}
                  </span>
                </mat-panel-title>
                <mat-panel-description>
                  @if (stage.status === 'running') {
                    <mat-icon class="stage-status-running">sync</mat-icon>
                  } @else if (stage.status === 'done') {
                    <mat-icon class="stage-status-done">check_circle</mat-icon>
                  } @else if (stage.status === 'error') {
                    <mat-icon class="stage-status-error">error</mat-icon>
                  } @else if (stage.status === 'paused') {
                    <mat-icon class="stage-status-paused">pause_circle</mat-icon>
                  }
                  @if (stage.tokensUsed != null) {
                    <span class="stage-tokens">{{ stage.tokensUsed.toLocaleString() }} tokens</span>
                  }
                </mat-panel-description>
              </mat-expansion-panel-header>

              <div class="stage-body">
                @if (stage.stepResult) {
                  <div class="stage-step-result">
                    <div class="step-header" [class]="stage.stepResult.success ? 'step-success' : 'step-failure'">
                      <mat-icon>{{ stage.stepResult.success ? 'check_circle' : 'cancel' }}</mat-icon>
                      <span>{{ stage.stepResult.toolName }}</span>
                      @if (stage.stepResult.durationMs) {
                        <span class="step-duration">{{ stage.stepResult.durationMs }}ms</span>
                      }
                    </div>
                    @if (stage.stepResult.error) {
                      <pre class="step-detail step-error-detail">{{ stage.stepResult.error }}</pre>
                    }
                    @if (stage.stepResult.result) {
                      <pre class="step-detail">{{ stage.stepResult.result | json }}</pre>
                    }
                  </div>
                }

                @if (stage.toolCalls && stage.toolCalls.length > 0) {
                  <div class="stage-tool-calls">
                    <div class="tool-calls-header">
                      <mat-icon>build</mat-icon>
                      Tool Calls ({{ stage.toolCalls.length }})
                    </div>
                    @for (call of stage.toolCalls; track $index) {
                      <div class="tool-call-entry" [class.tool-call-entry-error]="call.isError">
                        <div class="tool-call-head">
                          <mat-icon class="tool-call-icon" [class.tool-call-icon-error]="call.isError" [class.tool-call-icon-ok]="!call.isError && call.result != null">
                            {{ call.isError ? 'cancel' : call.result != null ? 'check_circle' : 'pending' }}
                          </mat-icon>
                          <span class="tool-call-name">{{ call.tool }}</span>
                          @if (call.iteration) {
                            <span class="tool-call-iter">#{{ call.iteration }}</span>
                          }
                         @if (call.tokensUsed != null) {
                           <span class="tool-call-tokens">{{ call.tokensUsed.toLocaleString() }} tokens</span>
                         }
                       </div>
                       @if (call.params) {
                         <pre class="tool-call-detail tool-call-params">{{ call.params }}</pre>
                        }
                       @if (call.result) {
                         <pre class="tool-call-detail tool-call-result">{{ call.result }}</pre>
                        }
                       @if (call.error) {
                         <pre class="tool-call-detail tool-call-error-text">{{ call.error }}</pre>
                       }
                     </div>
                    }
                  </div>
                }

                @if (stage.output) {
                  <div class="stage-output">
                    <pre class="stage-output-text">{{ stage.output }}</pre>
                  </div>
                }

                @if (stage.error) {
                  <div class="stage-error">
                    <mat-icon>error</mat-icon>
                    {{ stage.error }}
                  </div>
                }

                @if (!stage.output && !stage.stepResult && !stage.error && !(stage.toolCalls?.length) && stage.status === 'done') {
                  <div class="stage-empty">Completed (no output)</div>
                }
              </div>
            </mat-expansion-panel>
          }

          @if (run().error) {
            <div class="preview-error">
              <mat-icon>error</mat-icon>
              {{ run().error }}
            </div>
          }

          @if (run().tokens) {
            <div class="preview-meta">
              Tokens used: {{ run().tokens }}
            </div>
          }
        </div>
      }

      <!-- Resume input for paused workflows -->
      @if (run().status === 'paused') {
        <div class="resume-section">
          <div class="resume-label">
            <mat-icon>question_answer</mat-icon>
            The workflow is waiting for your response
          </div>
          @if (run().pausePrompt) {
            <div class="resume-prompt">{{ run().pausePrompt }}</div>
          }

          @if (run().selectOptions !== undefined) {
            <div class="resume-options">
              @if (run().selectOptions!.length > 0) {
                @for (opt of run().selectOptions; track $index) {
                  <button mat-stroked-button color="primary"
                          [disabled]="run().status === 'running'"
                          (click)="selectOption($index)">
                    {{ opt.label }}
                  </button>
                }
              } @else {
                <div class="resume-empty-options">
                  This selection stage produced no options to choose from.
                </div>
              }
            </div>
          } @else {
            <mat-form-field appearance="outline" class="preview-input">
              <mat-label>Your response</mat-label>
              <textarea matInput [(ngModel)]="resumeMessage" rows="2"
                        placeholder="Type your response (e.g. 'yes' to confirm, or describe changes)..."
                        (keydown.enter)="$event.preventDefault(); resumeMessage.trim() && resumeWorkflow()">
              </textarea>
            </mat-form-field>
            <div class="preview-actions">
              <button mat-flat-button color="primary"
                      [disabled]="!resumeMessage.trim()"
                      (click)="resumeWorkflow()">
                <mat-icon>send</mat-icon> Send Response
              </button>
              <button mat-stroked-button color="primary"
                      (click)="resumeMessage = 'yes'; resumeWorkflow()">
                <mat-icon>check</mat-icon> Confirm (yes)
              </button>
            </div>
          }
        </div>
      }

      @if (run().status === 'done' && run().stages.length === 0 && !run().error) {
        <div class="preview-empty">
          <mat-icon>info</mat-icon>
          Workflow completed but produced no stage output.
          Ensure the workflow has stages configured.
        </div>
      }
    </div>
  `,
  styles: [`
    .preview-container { padding: 16px; }
    .preview-title {
      display: flex; align-items: center; gap: 8px;
      margin: 0 0 16px; font-size: 16px;
    }
    .preview-input { width: 100%; }
    .preview-actions {
      display: flex; gap: 8px; margin-bottom: 16px;
    }
    .preview-progress { margin-bottom: 16px; }
    .preview-results { margin-top: 8px; display: flex; flex-direction: column; gap: 8px; }
    .preview-results h4 {
      font-size: 13px; font-weight: 600;
      text-transform: uppercase; color: #666;
      margin: 0 0 8px;
    }
    .stage-panel-title {
      display: flex; align-items: center; gap: 8px;
    }
    .stage-id {
      font-weight: 500; font-size: 13px;
    }
    .stage-type-chip {
      color: white;
      font-size: 11px;
      padding: 2px 8px;
      border-radius: 12px;
      font-weight: 500;
    }
    .stage-status-running { color: #1976d2; animation: spin 1.5s linear infinite; }
    .stage-status-done { color: #2e7d32; }
    .stage-status-error { color: #c62828; }
    .stage-status-paused { color: #f4b400; }
    .stage-tokens { margin-left: 8px; color: #1976d2; font-size: 12px; font-weight: 600; }
    @keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
    .stage-body { padding: 8px 0; }
    .stage-step-result { margin-bottom: 8px; }
    .step-header {
      display: flex; align-items: center; gap: 8px;
      padding: 8px; border-radius: 4px; font-size: 13px; font-weight: 500;
    }
    .step-success { background: #e8f5e9; color: #2e7d32; }
    .step-failure { background: #fbe9e7; color: #c62828; }
    .step-error-detail { background: #fbe9e7; color: #c62828; border-left: 3px solid #c62828; }
    .step-duration { margin-left: auto; font-weight: 400; opacity: 0.7; }
    .step-detail {
      background: #f5f5f5;
      padding: 8px;
      border-radius: 4px;
      font-size: 12px;
      max-height: 200px;
      overflow: auto;
      white-space: pre-wrap;
      word-break: break-word;
      margin-top: 4px;
    }
    .stage-output { margin-top: 4px; }
    .stage-output-text {
      background: #f5f5f5;
      padding: 8px;
      border-radius: 4px;
      font-size: 12px;
      max-height: 200px;
      overflow: auto;
      white-space: pre-wrap;
      word-break: break-word;
      margin: 0;
    }
    .stage-error {
      display: flex; align-items: center; gap: 8px;
      padding: 8px;
      background: #fbe9e7;
      border-radius: 4px;
      color: #c62828;
      font-size: 13px;
    }
    .stage-empty {
      font-size: 12px; color: #999; font-style: italic;
    }
    .stage-tool-calls {
      margin-bottom: 8px;
      border: 1px solid #e0e0e0;
      border-radius: 4px;
      overflow: hidden;
    }
    .tool-calls-header {
      display: flex; align-items: center; gap: 6px;
      padding: 6px 10px;
      background: #f5f5f5;
      font-size: 12px; font-weight: 600;
      color: #555;
      border-bottom: 1px solid #e0e0e0;
      mat-icon { font-size: 14px; width: 14px; height: 14px; }
    }
    .tool-call-entry {
      padding: 6px 10px;
      border-bottom: 1px solid #f0f0f0;
      &:last-child { border-bottom: none; }
    }
    .tool-call-entry-error { background: #fff8f8; }
    .tool-call-head {
      display: flex; align-items: center; gap: 6px;
      font-size: 12px; font-weight: 500;
    }
    .tool-call-icon { font-size: 14px; width: 14px; height: 14px; color: #999; }
    .tool-call-icon-ok { color: #2e7d32; }
    .tool-call-icon-error { color: #c62828; }
    .tool-call-name { font-family: monospace; }
    .tool-call-iter { margin-left: 8px; color: #999; font-size: 11px; font-weight: 400; }
    .tool-call-tokens { margin-left: auto; color: #1976d2; font-size: 11px; font-weight: 600; }
    .tool-call-detail {
      background: #f5f5f5;
      padding: 4px 8px;
      border-radius: 3px;
      font-size: 11px;
      max-height: 120px;
      overflow: auto;
      white-space: pre-wrap;
      word-break: break-word;
      margin: 4px 0 0;
    }
    .tool-call-error-text { background: #fbe9e7; color: #c62828; }
    .preview-error {
      display: flex; align-items: center; gap: 8px;
      padding: 12px;
      background: #fbe9e7;
      border-radius: 4px;
      color: #c62828;
      margin-top: 8px;
    }
    .preview-meta {
      margin-top: 8px;
      font-size: 12px;
      color: #999;
    }
    .preview-empty {
      display: flex; align-items: center; gap: 8px;
      padding: 12px;
      background: #fff3e0;
      border-radius: 4px;
      color: #e65100;
      margin-top: 8px;
      font-size: 13px;
    }
    .resume-section {
      margin-top: 16px;
      padding: 16px;
      background: #e3f2fd;
      border-radius: 8px;
      border: 1px solid #90caf9;
    }
    .resume-label {
      display: flex; align-items: center; gap: 8px;
      font-weight: 500; color: #1565c0;
      margin-bottom: 12px;
    }
    .resume-prompt {
      white-space: pre-wrap;
      background: #fff;
      border: 1px solid #90caf9;
      border-radius: 6px;
      padding: 10px 12px;
      margin-bottom: 12px;
      color: #1a1a1a;
      font-size: 14px;
      line-height: 1.4;
    }
    .resume-options {
      display: flex;
      flex-direction: column;
      gap: 8px;
      align-items: stretch;
    }
    .resume-options button {
      justify-content: flex-start;
      text-align: left;
    }
    .resume-empty-options {
      font-size: 13px; color: #c62828; font-style: italic;
    }
  `],
})
export class LivePreviewComponent implements OnDestroy {
  private readonly builder = inject(WorkflowBuilderService);
  private readonly apiService = inject(WorkflowApiService);

  /** Override for the API base URL (falls back to WorkflowApiService's configured URL) */
  apiUrl = input<string>('');

  testMessage = '';
  resumeMessage = '';
  run = signal<PreviewRun>({ stages: [], status: 'idle' });

  private abortController: AbortController | null = null;

  /** Last sample prompt value pushed into the test message, used to detect
   *  whether the user has manually overridden the test message. */
  private lastSyncedSample = '';
  /** ID of the workflow that was active when we last synced the test message. */
  private lastSyncedWorkflowId = '';

  constructor() {
    // Keep the test message in sync with the workflow's sample prompt and reset
    // the test run whenever the active workflow (or its sample prompt) changes.
    effect(() => {
      const prompt = this.builder.activePrompt();
      const sample = prompt.samplePrompt ?? '';
      const workflowId = prompt.id ?? '';
      // Always override when a different workflow is selected; otherwise only
      // update if the user hasn't typed their own test message (i.e. it still
      // matches the value we last synced in).
      const workflowChanged = workflowId !== this.lastSyncedWorkflowId;
      if (workflowChanged || this.testMessage === this.lastSyncedSample) {
        this.testMessage = sample;
      }
      this.lastSyncedSample = sample;
      this.lastSyncedWorkflowId = workflowId;
      this.reset();
    });
  }

  ngOnDestroy(): void {
    this.abortController?.abort();
  }

  getMeta(type: string): { icon: string; color: string; label: string } {
    return getStageMeta(type);
  }

  async startTest(): Promise<void> {
    if (!this.testMessage.trim()) return;

    this.abortController?.abort();
    this.abortController = new AbortController();

    this.run.set({ stages: [], status: 'running' });
    this.resumeMessage = '';

    const workflowDefinition = this.builder.toJSON();
    const baseUrl = this.apiUrl() || this.apiService.getBaseUrl();
    const sessionId = localStorage.getItem('ava-session-id') || undefined;

    await this.streamSSE(`${baseUrl}/api/chat/test-workflow`, {
      message: this.testMessage,
      domain: workflowDefinition.domain || 'ado',
      userId: 'workflow-builder-test',
      sessionId,
      workflowDefinition,
    });
  }

  async resumeWorkflow(): Promise<void> {
    const currentRun = this.run();
    if (!this.resumeMessage.trim() || !currentRun.resumeRunId) return;

    this.abortController?.abort();
    this.abortController = new AbortController();

    // Mark as running again (keep existing stages)
    this.run.update(r => ({ ...r, status: 'running', resumeRunId: undefined }));

    const workflowDefinition = this.builder.toJSON();
    const baseUrl = this.apiUrl() || this.apiService.getBaseUrl();
    const sessionId = localStorage.getItem('ava-session-id') || undefined;

    await this.streamSSE(`${baseUrl}/api/chat/test-workflow/resume`, {
      agentRunId: currentRun.resumeRunId,
      message: this.resumeMessage,
      domain: workflowDefinition.domain || 'ado',
      userId: 'workflow-builder-test',
      sessionId,
      workflowDefinition,
    });

    this.resumeMessage = '';
  }

  /**
   * Handle a user-select choice: merge the selection into the paused run via
   * POST /api/chat/select (which advances past the pause), then resume the
   * stream to continue executing the remaining stages.
   */
  async selectOption(index: number): Promise<void> {
    const currentRun = this.run();
    const runId = currentRun.resumeRunId;
    if (!runId) return;

    this.abortController?.abort();
    this.abortController = new AbortController();

    const baseUrl = this.apiUrl() || this.apiService.getBaseUrl();

    // Mark as running and clear the pause UI
    this.run.update(r => ({
      ...r,
      status: 'running',
      resumeRunId: undefined,
      pausePrompt: undefined,
      selectOptions: undefined,
    }));

    try {
      const res = await fetch(`${baseUrl}/api/chat/select`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ agentRunId: runId, inputType: 'select', selectedIndex: index }),
        signal: this.abortController.signal,
      });
      if (!res.ok) {
        const detail = await res.text().catch(() => '');
        this.run.update(r => ({
          ...r, status: 'error',
          error: detail || `HTTP ${res.status}: failed to record selection`,
        }));
        return;
      }
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const message = err instanceof Error ? err.message : 'Unknown error';
      this.run.update(r => ({ ...r, status: 'error', error: message }));
      return;
    }

    const workflowDefinition = this.builder.toJSON();
    const sessionId = localStorage.getItem('ava-session-id') || undefined;

    await this.streamSSE(`${baseUrl}/api/chat/test-workflow/resume`, {
      agentRunId: runId,
      message: '',
      domain: workflowDefinition.domain || 'ado',
      userId: 'workflow-builder-test',
      sessionId,
      workflowDefinition,
    });
  }

  private async streamSSE(url: string, body: unknown): Promise<void> {
    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'text/event-stream',
        },
        body: JSON.stringify(body),
        signal: this.abortController!.signal,
      });

      if (!response.ok || !response.body) {
        let errorDetail = `HTTP ${response.status}: ${response.statusText}`;
        try {
          const errBody = await response.text();
          if (errBody) errorDetail = errBody;
        } catch { /* use default */ }
        this.run.update(r => ({
          ...r, status: 'error', error: errorDetail,
        }));
        return;
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        const parts = buffer.split('\n\n');
        buffer = parts.pop() ?? '';

        for (const part of parts) {
          const line = part.trim();
          if (line.startsWith('data: ')) {
            try {
              const evt = JSON.parse(line.slice(6));
              this.handleEvent(evt);
            } catch (e) {
              console.warn('[LivePreview] Failed to parse SSE event:', line, e);
            }
          }
        }
      }

      // Mark any still-running stages as done (only if not paused)
      this.run.update(r => {
        if (r.status === 'paused') return r;
        return {
          ...r,
          status: r.status === 'running' ? 'done' : r.status,
          stages: r.stages.map(s => s.status === 'running' ? { ...s, status: 'done' as const } : s),
        };
      });
    } catch (err: unknown) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const message = err instanceof Error ? err.message : 'Unknown error';
      this.run.update(r => ({ ...r, status: 'error', error: message }));
    }
  }

  private handleEvent(evt: { type: string; data: unknown }): void {
    switch (evt.type) {
      case 'run_started':
        this.run.update(r => ({
          ...r,
          agentRunId: (evt.data as { agentRunId: string }).agentRunId,
        }));
        break;

      case 'stage_start': {
        const { stageId, type } = evt.data as { stageId: string; type: string };
        // Mark the previous stage as done (if still running)
        this.run.update(r => ({
          ...r,
          stages: [
            ...r.stages.map(s => s.status === 'running' ? { ...s, status: 'done' as const } : s),
            { stageId, type, status: 'running', output: '' },
          ],
        }));
        break;
      }

      case 'step_done': {
        const step = evt.data as {
          stageId?: string; stepId?: string; toolName?: string; success?: boolean;
          result?: string; error?: string; durationMs?: number;
          tokensUsed?: number; iterations?: number;
        };
        const id = step.stageId ?? step.stepId;
        this.run.update(r => ({
          ...r,
          stages: r.stages.map(s =>
            s.stageId === id
              ? {
                  ...s,
                  status: (step.success === false ? 'error' : 'done') as 'done' | 'error',
                  tokensUsed: step.tokensUsed,
                  stepResult: step.toolName ? {
                    toolName: step.toolName,
                    success: step.success ?? true,
                    result: tryParseJson(step.result),
                    error: step.error,
                    durationMs: step.durationMs,
                  } : s.stepResult,
                }
              : s
          ),
        }));
        break;
      }

      case 'token': {
        // Append token text to the current (last) stage's output
        const text = evt.data as string;
        this.run.update(r => {
          const stages = [...r.stages];
          if (stages.length > 0) {
            const last = { ...stages[stages.length - 1] };
            last.output = (last.output || '') + text;
            stages[stages.length - 1] = last;
          }
          return { ...r, stages };
        });
        break;
      }

      case 'tool_call': {
        const d = evt.data as { tool: string; params?: string; iteration?: number };
        this.run.update(r => {
          const stages = [...r.stages];
          if (stages.length > 0) {
            const last = { ...stages[stages.length - 1] };
            last.toolCalls = [...(last.toolCalls ?? []), { tool: d.tool, params: d.params, iteration: d.iteration }];
            stages[stages.length - 1] = last;
          }
          return { ...r, stages };
        });
        break;
      }

      case 'tool_result': {
        const d = evt.data as { tool: string; summary?: string; tokensUsed?: number };
        this.run.update(r => {
          const stages = [...r.stages];
          if (stages.length > 0) {
            const last = { ...stages[stages.length - 1] };
            const calls = [...(last.toolCalls ?? [])];
            // Find the last pending call for this tool (no result/error yet)
            const revIdx = [...calls].reverse().findIndex(c => c.tool === d.tool && c.result == null && !c.error);
            if (revIdx >= 0) calls[calls.length - 1 - revIdx] = { ...calls[calls.length - 1 - revIdx], result: d.summary ?? '', tokensUsed: d.tokensUsed };
            last.toolCalls = calls;
            last.tokensUsed = d.tokensUsed;
            stages[stages.length - 1] = last;
          }
          return { ...r, stages };
        });
        break;
      }

      case 'tool_error': {
        const d = evt.data as { tool: string; error: string; message?: string; tokensUsed?: number };
        this.run.update(r => {
          const stages = [...r.stages];
          if (stages.length > 0) {
            const last = { ...stages[stages.length - 1] };
            const calls = [...(last.toolCalls ?? [])];
            const revIdx = [...calls].reverse().findIndex(c => c.tool === d.tool && c.result == null && !c.error);
            if (revIdx >= 0) calls[calls.length - 1 - revIdx] = { ...calls[calls.length - 1 - revIdx], error: d.message ?? d.error, isError: true, tokensUsed: d.tokensUsed };
            last.toolCalls = calls;
            last.tokensUsed = d.tokensUsed;
            stages[stages.length - 1] = last;
          }
          return { ...r, stages };
        });
        break;
      }

      case 'error': {
        const errorMsg = evt.data as string;
        this.run.update(r => {
          const stages = [...r.stages];
          if (stages.length > 0) {
            const last = { ...stages[stages.length - 1] };
            last.status = 'error';
            last.error = errorMsg;
            stages[stages.length - 1] = last;
          }
          return { ...r, stages, status: 'error', error: errorMsg };
        });
        break;
      }

      case 'done': {
        const data = evt.data as { agentRunId?: string; tokensUsed?: number; resumeRunId?: string };
        if (data.resumeRunId) {
          // Workflow is paused — waiting for user input
          this.run.update(r => ({
            ...r,
            status: 'paused',
            resumeRunId: data.resumeRunId,
            tokens: data.tokensUsed ?? r.tokens,
            stages: r.stages.map(s => s.status === 'running' ? { ...s, status: 'paused' as const } : s),
          }));
        } else {
          // Workflow complete
          this.run.update(r => ({
            ...r,
            status: 'done',
            tokens: data.tokensUsed,
            stages: r.stages.map(s => s.status === 'running' ? { ...s, status: 'done' as const } : s),
          }));
        }
        break;
      }

      case 'user_input_required':
      case 'confirmation_required': {
        const d = evt.data as {
          prompt?: string;
          preview?: string;
          inputType?: string;
          payload?: {
            prompt?: string;
            options?: { label: string; value: string }[];
          };
        };
        const pausePrompt = d.prompt ?? d.payload?.prompt ?? d.preview;
        const selectOptions = d.inputType === 'select' ? d.payload?.options ?? [] : undefined;
        this.run.update(r => {
          const stages = [...r.stages];
          if (stages.length > 0) {
            const last = { ...stages[stages.length - 1] };
            last.status = 'paused';
            stages[stages.length - 1] = last;
          }
          return { ...r, stages, pausePrompt, selectOptions };
        });
        break;
      }
    }
  }

  reset(): void {
    this.abortController?.abort();
    this.abortController = null;
    this.resumeMessage = '';
    this.run.set({ stages: [], status: 'idle' });
  }
}

function tryParseJson(value: unknown): unknown {
  if (typeof value !== 'string') return value;
  try { return JSON.parse(value); } catch { return value; }
}
