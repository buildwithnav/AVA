import { Component, inject, signal, computed, ViewChild, ElementRef, AfterViewChecked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TextFieldModule } from '@angular/cdk/text-field';
import {
  WorkflowCreatorService,
  CreatorMessage,
  DEFAULT_SUGGESTIONS,
  EDIT_SUGGESTIONS,
  StarterSuggestion,
} from '../../services/workflow-creator.service';

@Component({
  selector: 'wfb-creator-chat-panel',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    TextFieldModule,
  ],
  template: `
    <div class="creator-chat-panel">
      <!-- Welcome / Suggestions (shown when no messages yet) -->
      @if (messages().length === 0) {
        <div class="welcome-area">
          <div class="welcome-header">
            <span class="welcome-icon">✨</span>
            <h3>{{ welcomeTitle() }}</h3>
          </div>
          <p class="welcome-text">{{ welcomeText() }}</p>
          <p class="suggestions-label">{{ suggestionsLabel() }}</p>
          <div class="suggestions-list">
            @for (suggestion of suggestions(); track suggestion.text) {
              <button class="suggestion-chip" (click)="useSuggestion(suggestion)">
                <span class="suggestion-icon">{{ suggestion.icon }}</span>
                <span class="suggestion-text">"{{ suggestion.text }}"</span>
              </button>
            }
          </div>
        </div>
      }

      <!-- Message list -->
      @if (messages().length > 0) {
        <div class="messages-area" #messagesContainer>
          @for (msg of messages(); track msg.timestamp) {
            <div class="message" [class.user]="msg.role === 'user'" [class.assistant]="msg.role === 'assistant'">
              @if (msg.role === 'user' || hasFormattedContent(msg.content)) {
                <div class="message-bubble">
                  <div class="message-content" [innerHTML]="formatMessage(msg.content)"></div>
                </div>
              }
            </div>
          }
          @if (isThinking()) {
            <div class="message assistant">
              <div class="message-bubble thinking">
                <mat-spinner diameter="16"></mat-spinner>
                <span>Building your workflow...</span>
              </div>
            </div>
          }
        </div>
      }

      <!-- Input area -->
      <div class="input-area">
        <mat-form-field appearance="outline" class="message-input">
          <textarea
            matInput
            cdkTextareaAutosize
            cdkAutosizeMinRows="1"
            cdkAutosizeMaxRows="6"
            [(ngModel)]="inputText"
            placeholder="Describe your workflow or ask for changes..."
            (keydown)="onInputKeydown($event)"
            [disabled]="isThinking()"
          ></textarea>
        </mat-form-field>
        <button
          mat-icon-button
          color="primary"
          (click)="send()"
          [disabled]="!inputText.trim() || isThinking()"
          class="send-button"
        >
          <mat-icon>send</mat-icon>
        </button>
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
    }

    .creator-chat-panel {
      display: flex;
      flex-direction: column;
      height: 100%;
      overflow: hidden;
    }

    .welcome-area {
      flex: 1;
      padding: 24px;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 16px;
    }

    .welcome-header {
      display: flex;
      align-items: center;
      gap: 8px;
    }

    .welcome-icon { font-size: 24px; }

    .welcome-header h3 {
      margin: 0;
      font-size: 18px;
      font-weight: 500;
    }

    .welcome-text {
      color: var(--mat-sys-on-surface-variant, #666);
      margin: 0;
      line-height: 1.5;
    }

    .suggestions-label {
      margin: 8px 0 0;
      font-weight: 500;
      font-size: 14px;
    }

    .suggestions-list {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }

    .suggestion-chip {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      padding: 12px 16px;
      border: 1px solid var(--mat-sys-outline-variant, #ddd);
      border-radius: 12px;
      background: var(--mat-sys-surface-container-low, #f8f8f8);
      cursor: pointer;
      text-align: left;
      transition: all 0.15s ease;
    }

    .suggestion-chip:hover {
      background: var(--mat-sys-surface-container, #eee);
      border-color: var(--mat-sys-primary, #1976d2);
    }

    .suggestion-icon { font-size: 16px; flex-shrink: 0; margin-top: 2px; }
    .suggestion-text { font-size: 13px; line-height: 1.4; color: var(--mat-sys-on-surface, #333); }

    .messages-area {
      flex: 1;
      overflow-y: auto;
      padding: 16px;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }

    .message {
      display: flex;
    }

    .message.user { justify-content: flex-end; }
    .message.assistant { justify-content: flex-start; }

    .message-bubble {
      max-width: 85%;
      padding: 10px 14px;
      border-radius: 12px;
      font-size: 13px;
      line-height: 1.5;
    }

    .message.user .message-bubble {
      background: var(--mat-sys-primary, #1976d2);
      color: white;
      border-bottom-right-radius: 4px;
    }

    .message.assistant .message-bubble {
      background: var(--mat-sys-surface-container, #f0f0f0);
      color: var(--mat-sys-on-surface, #333);
      border-bottom-left-radius: 4px;
    }

    .message-bubble.thinking {
      display: flex;
      align-items: center;
      gap: 8px;
      font-style: italic;
      color: var(--mat-sys-on-surface-variant, #666);
    }

    .message-content {
      white-space: pre-wrap;
      word-break: break-word;
    }

    .input-area {
      display: flex;
      align-items: flex-end;
      gap: 8px;
      padding: 12px 16px;
      border-top: 1px solid var(--mat-sys-outline-variant, #ddd);
    }

    .message-input {
      flex: 1;
    }

    .message-input textarea {
      resize: none;
      line-height: 1.4;
      overflow-y: auto;
    }

    .message-input ::ng-deep .mat-mdc-form-field-subscript-wrapper {
      display: none;
    }

    .send-button {
      flex-shrink: 0;
    }
  `],
})
export class CreatorChatPanelComponent implements AfterViewChecked {
  private readonly creator = inject(WorkflowCreatorService);

  @ViewChild('messagesContainer') messagesContainer?: ElementRef<HTMLElement>;

  readonly messages = this.creator.messages;
  readonly isThinking = this.creator.isAgentThinking;
  readonly isEditing = computed(() => this.creator.mode() === 'edit');
  readonly suggestions = computed<StarterSuggestion[]>(() =>
    this.creator.mode() === 'edit' ? EDIT_SUGGESTIONS : DEFAULT_SUGGESTIONS
  );
  readonly welcomeTitle = computed(() => this.isEditing() ? 'Edit This Workflow' : 'Create a New Workflow');
  readonly welcomeText = computed(() => this.isEditing()
    ? 'Tell me what to change and I\'ll update the workflow — add or remove stages, wire in a loop, adjust prompts, and more. You can also edit the cards directly on the right.'
    : 'Describe what you want your workflow to do, and I\'ll build it for you. You can also edit the cards directly on the right at any time.');
  readonly suggestionsLabel = computed(() => this.isEditing() ? 'Try one of these edits:' : 'Try one of these to get started:');

  inputText = '';
  private shouldScroll = false;

  ngAfterViewChecked(): void {
    if (this.shouldScroll && this.messagesContainer) {
      const el = this.messagesContainer.nativeElement;
      el.scrollTop = el.scrollHeight;
      this.shouldScroll = false;
    }
  }

  send(): void {
    const text = this.inputText.trim();
    if (!text || this.isThinking()) return;
    this.inputText = '';
    this.shouldScroll = true;
    this.creator.sendMessage(text);
  }

  onInputKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  useSuggestion(suggestion: StarterSuggestion): void {
    this.inputText = suggestion.text;
    this.send();
  }

  formatMessage(content: string): string {
    if (!content) return '';
    // Basic markdown-like formatting
    return content
      .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
      .replace(/`(.+?)`/g, '<code>$1</code>')
      .replace(/\n/g, '<br>');
  }

  hasFormattedContent(content: string): boolean {
    return this.formatMessage(content).replace(/<br\s*\/?>/gi, '').trim().length > 0;
  }
}
