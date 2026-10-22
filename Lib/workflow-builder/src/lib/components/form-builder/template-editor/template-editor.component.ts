import { Component, inject, input, output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { CdkDragDrop, DragDropModule } from '@angular/cdk/drag-drop';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCardModule } from '@angular/material/card';
import {
  TemplateDefinition, TemplateField, FieldType,
  createEmptyTemplate, createEmptyField,
} from '../../../models';

/**
 * Edits a TemplateDefinition — the form displayed during ai-generate stages.
 * Allows adding/removing/reordering fields with type/required/options config.
 */
@Component({
  selector: 'wfb-template-editor',
  standalone: true,
  imports: [
    CommonModule, FormsModule, DragDropModule,
    MatFormFieldModule, MatInputModule, MatSelectModule,
    MatSlideToggleModule, MatButtonModule, MatIconModule, MatCardModule,
  ],
  template: `
    <div class="wfb-template-editor">
      <mat-form-field appearance="outline" class="wfb-full">
        <mat-label>Template ID</mat-label>
        <input matInput [(ngModel)]="template.id" placeholder="e.g. work-item-create" />
      </mat-form-field>

      <mat-form-field appearance="outline" class="wfb-full">
        <mat-label>Description</mat-label>
        <input matInput [(ngModel)]="template.description" />
      </mat-form-field>

      <div class="wfb-fields-header">
        <h4>Fields</h4>
        <button mat-mini-fab color="primary" (click)="addField()">
          <mat-icon>add</mat-icon>
        </button>
      </div>

      <div cdkDropList (cdkDropListDropped)="onDrop($event)" class="wfb-drop-list">
        @for (field of template.fields; track $index; let i = $index) {
          <mat-card cdkDrag class="wfb-field-card">
            <!-- Card header: drag handle, field number, actions -->
            <div class="wfb-card-header" cdkDragHandle>
              <mat-icon class="wfb-drag-handle">drag_indicator</mat-icon>
              <span class="wfb-field-badge">Field {{ i + 1 }}</span>
              <span class="wfb-spacer"></span>
              <mat-slide-toggle [(ngModel)]="field.required" class="wfb-required-toggle">
                Required
              </mat-slide-toggle>
              <button mat-icon-button (click)="removeField(i)" class="wfb-delete-btn">
                <mat-icon>delete</mat-icon>
              </button>
            </div>

            <!-- Card body: structured form rows -->
            <div class="wfb-card-body">
              <div class="wfb-input-row">
                <mat-form-field appearance="outline" class="wfb-grow">
                  <mat-label>Name</mat-label>
                  <input matInput [(ngModel)]="field.name" placeholder="fieldName" />
                </mat-form-field>

                <mat-form-field appearance="outline" class="wfb-grow">
                  <mat-label>Label</mat-label>
                  <input matInput [(ngModel)]="field.label" placeholder="Display Label" />
                </mat-form-field>

                <mat-form-field appearance="outline" class="wfb-type-field">
                  <mat-label>Type</mat-label>
                  <mat-select [(ngModel)]="field.type">
                    @for (t of fieldTypes; track t) {
                      <mat-option [value]="t">{{ t }}</mat-option>
                    }
                  </mat-select>
                </mat-form-field>
              </div>

              @if (field.type === 'select') {
                <mat-form-field appearance="outline" class="wfb-full">
                  <mat-label>Options (comma-separated)</mat-label>
                  <input matInput
                         [ngModel]="(field.options || []).join(', ')"
                         (ngModelChange)="field.options = parseOptions($event)" />
                </mat-form-field>
              }
            </div>
          </mat-card>
        }
      </div>

      @if (template.fields.length === 0) {
        <p class="wfb-hint">No fields yet. Click + to add form fields.</p>
      }
    </div>
  `,
  styles: [`
    .wfb-template-editor {
      padding: 20px;
      display: flex;
      flex-direction: column;
      gap: 4px;
      max-width: 860px;
    }

    .wfb-full { width: 100%; }

    .wfb-fields-header {
      display: flex;
      align-items: center;
      margin: 8px 0 4px;
      h4 { margin: 0; flex: 1; font-size: 15px; font-weight: 500; }
    }

    .wfb-drop-list { display: flex; flex-direction: column; gap: 8px; }

    .wfb-field-card {
      overflow: hidden;
      border-left: 3px solid var(--mat-sys-primary, #6750a4);
    }

    // Card header — always a single compact row
    .wfb-card-header {
      display: flex;
      align-items: center;
      gap: 4px;
      padding: 6px 8px 2px;
      cursor: grab;
    }

    .wfb-drag-handle {
      font-size: 18px;
      width: 18px;
      height: 18px;
      opacity: 0.4;
    }

    .wfb-field-badge {
      font-size: 11px;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.5px;
      opacity: 0.5;
    }

    .wfb-spacer { flex: 1; }

    .wfb-required-toggle {
      transform: scale(0.85);
      transform-origin: right center;
    }

    .wfb-delete-btn {
      opacity: 0.4;
      transition: opacity 0.15s;
      &:hover { opacity: 1; color: var(--mat-sys-error, #b3261e); }
    }

    // Card body — form fields in structured rows
    .wfb-card-body {
      padding: 0 12px 12px;
    }

    .wfb-input-row {
      display: grid;
      grid-template-columns: 1fr 1fr auto;
      gap: 8px;
      align-items: start;
    }

    .wfb-grow { min-width: 0; } // prevent grid blowout

    .wfb-type-field { width: 120px; min-width: 100px; }

    // Responsive: stack fields vertically on narrow panels
    @media (max-width: 580px) {
      .wfb-input-row {
        grid-template-columns: 1fr 1fr;
      }
      .wfb-type-field {
        grid-column: 1 / -1;
        width: 100%;
      }
    }

    .wfb-hint {
      font-size: 13px;
      opacity: 0.55;
      text-align: center;
      padding: 24px 16px;
    }

    // CDK drag preview styling
    .cdk-drag-preview {
      box-shadow: 0 4px 16px rgba(0,0,0,0.16);
      border-radius: 8px;
    }
    .cdk-drag-placeholder {
      opacity: 0.3;
    }
  `],
})
export class TemplateEditorComponent {
  /** Template being edited — two-way bound by parent */
  template: TemplateDefinition = createEmptyTemplate();

  /** Available field types */
  readonly fieldTypes: FieldType[] = ['text', 'multiline', 'select', 'number', 'date'];

  /** Set the template to edit */
  loadTemplate(t: TemplateDefinition): void {
    this.template = structuredClone(t);
  }

  addField(): void {
    this.template.fields.push(createEmptyField());
  }

  removeField(index: number): void {
    this.template.fields.splice(index, 1);
  }

  onDrop(event: CdkDragDrop<TemplateField[]>): void {
    const fields = [...this.template.fields];
    const [moved] = fields.splice(event.previousIndex, 1);
    fields.splice(event.currentIndex, 0, moved);
    this.template.fields = fields;
  }

  parseOptions(value: string): string[] {
    return value.split(',').map(s => s.trim()).filter(s => s.length > 0);
  }

  /** Get the current template as a clean object */
  getValue(): TemplateDefinition {
    return structuredClone(this.template);
  }
}
