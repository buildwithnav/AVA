import { Component, OnInit, ViewChild, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatListModule } from '@angular/material/list';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import {
  WorkflowApiService,
  WorkflowBuilderService,
  TemplateEditorComponent,
  TemplateDefinition,
  createEmptyTemplate,
} from '@ava/workflow-builder';

/**
 * Data Templates page — manages data templates with a sidebar list
 * and the shared TemplateEditorComponent.
 */
@Component({
  selector: 'app-data-templates',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatListModule, MatButtonModule,
    MatIconModule, MatCardModule, MatDividerModule,
    MatSnackBarModule, MatTooltipModule,
    MatFormFieldModule, MatInputModule,
    TemplateEditorComponent,
  ],
  template: `
    <div class="dt-layout">
      <!-- Left sidebar -->
      <div class="dt-sidebar">
        <div class="dt-sidebar-header">
          <mat-form-field appearance="outline" class="dt-search-field" subscriptSizing="dynamic">
            <mat-label>Search...</mat-label>
            <mat-icon matPrefix>search</mat-icon>
            <input matInput [ngModel]="searchQuery()" (ngModelChange)="searchQuery.set($event)" />
            @if (searchQuery()) {
              <button matSuffix mat-icon-button (click)="searchQuery.set('')">
                <mat-icon>close</mat-icon>
              </button>
            }
          </mat-form-field>
          <button mat-mini-fab color="primary" (click)="newTemplate()" matTooltip="New template">
            <mat-icon>add</mat-icon>
          </button>
        </div>

        <div class="dt-sidebar-list">
          <mat-nav-list>
            @for (t of filteredTemplates(); track t.id) {
              <a mat-list-item (click)="selectTemplate(t)"
                 [activated]="selectedTemplateId() === t.id">
                <span matListItemTitle>{{ t.id }}</span>
                <span matListItemLine>{{ t.description }}</span>
              </a>
            }
          </mat-nav-list>

          @if (filteredTemplates().length === 0 && !showArchived()) {
            <p class="dt-empty">{{ searchQuery() ? 'No templates match your search.' : 'No templates found. Click + to create one.' }}</p>
          }
        </div>

        <div class="dt-sidebar-footer">
          <button mat-button (click)="toggleArchived()">
            <mat-icon>{{ showArchived() ? 'visibility_off' : 'inventory_2' }}</mat-icon>
            {{ showArchived() ? 'Hide Archived' : 'View Archived' }}
          </button>

          @if (showArchived() && archivedTemplates().length > 0) {
            <mat-nav-list class="dt-archived-list">
              @for (t of archivedTemplates(); track t.id) {
                <a mat-list-item class="dt-archived-item">
                  <span matListItemTitle>{{ t.id }}</span>
                  <span matListItemLine>Archived</span>
                  <button mat-icon-button matListItemMeta (click)="restoreTemplate(t.id)" matTooltip="Restore">
                    <mat-icon>unarchive</mat-icon>
                  </button>
                </a>
              }
            </mat-nav-list>
          }
          @if (showArchived() && archivedTemplates().length === 0) {
            <p class="dt-empty">No archived templates.</p>
          }
        </div>
      </div>

      <!-- Right panel -->
      <div class="dt-editor-panel">
        @if (selectedTemplateId() || isNewTemplate()) {
          <div class="dt-template-panel">
            <div class="dt-template-toolbar">
              <span class="dt-template-title">
                <mat-icon>description</mat-icon>
                {{ isNewTemplate() ? 'New Template' : 'Edit Template' }}
              </span>
              <span class="dt-spacer"></span>
              <button mat-raised-button color="primary" (click)="saveTemplate()">
                <mat-icon>save</mat-icon> Save
              </button>
              @if (!isNewTemplate()) {
                <button mat-raised-button color="warn" (click)="archiveTemplate()">
                  <mat-icon>archive</mat-icon> Archive
                </button>
              }
            </div>
            <div class="dt-template-editor-scroll">
              <wfb-template-editor />
            </div>
          </div>
        } @else {
          <div class="dt-placeholder">
            <mat-icon>description</mat-icon>
            <h3>Select a template or create a new one</h3>
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; height: 100%; }
    .dt-layout {
      display: grid;
      grid-template-columns: 300px 1fr;
      height: 100%;
      overflow: hidden;
    }
    .dt-sidebar {
      border-right: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      display: flex;
      flex-direction: column;
      overflow: hidden;
    }
    .dt-sidebar-header {
      display: flex; align-items: center; padding: 10px 12px; gap: 8px; flex-shrink: 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
    }
    .dt-search-field {
      flex: 1;
      ::ng-deep {
        .mat-mdc-form-field-subscript-wrapper { display: none; }
        mat-icon[matPrefix] { margin-right: 8px; color: rgba(0, 0, 0, 0.5); }
      }
    }
    .dt-sidebar-list {
      flex: 1; overflow-y: auto; overflow-x: hidden; min-height: 0;
    }
    .dt-sidebar-footer {
      border-top: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      padding: 8px;
      flex-shrink: 0;
      max-height: 200px;
      overflow-y: auto;
    }
    .dt-archived-list { padding-top: 0; }
    .dt-archived-item { opacity: 0.6; }
    .dt-editor-panel { overflow: hidden; height: 100%; }
    .dt-empty { padding: 16px; text-align: center; opacity: 0.6; font-size: 13px; }
    .dt-placeholder {
      display: flex; flex-direction: column; align-items: center;
      justify-content: center; height: 100%; opacity: 0.5;
      mat-icon { font-size: 64px; width: 64px; height: 64px; }
    }
    .dt-template-panel {
      display: flex; flex-direction: column; height: 100%;
    }
    .dt-template-toolbar {
      display: flex; align-items: center; gap: 8px;
      padding: 8px 16px;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
    }
    .dt-template-title {
      display: flex; align-items: center; gap: 8px;
      font-size: 16px; font-weight: 500;
    }
    .dt-spacer { flex: 1; }
    .dt-template-editor-scroll {
      flex: 1; overflow-y: auto; overflow-x: hidden;
    }
  `],
})
export class DataTemplates implements OnInit {
  @ViewChild(TemplateEditorComponent) templateEditor?: TemplateEditorComponent;

  private readonly api = inject(WorkflowApiService);
  private readonly builder = inject(WorkflowBuilderService);
  private readonly snackBar = inject(MatSnackBar);

  templates = signal<TemplateDefinition[]>([]);
  archivedTemplates = signal<TemplateDefinition[]>([]);
  selectedTemplateId = signal<string | null>(null);
  isNewTemplate = signal(false);
  showArchived = signal(false);
  searchQuery = signal('');
  private originalTemplateId: string | null = null;

  /** Filtered templates based on search query */
  filteredTemplates = computed(() => {
    const query = this.searchQuery().toLowerCase().trim();
    const all = this.templates();
    if (!query) return all;
    return all.filter(t =>
      t.id.toLowerCase().includes(query) ||
      (t.description || '').toLowerCase().includes(query)
    );
  });

  ngOnInit(): void {
    this.loadTemplates();
  }

  private loadTemplates(): void {
    this.api.getTemplates().subscribe({
      next: (res) => {
        this.templates.set(res.templates);
        this.builder.setAvailableTemplates(res.templates as any[]);
      },
      error: () => {},
    });
  }

  selectTemplate(t: TemplateDefinition): void {
    this.selectedTemplateId.set(t.id);
    this.isNewTemplate.set(false);
    this.originalTemplateId = t.id;
    setTimeout(() => this.templateEditor?.loadTemplate(t));
  }

  newTemplate(): void {
    this.selectedTemplateId.set(null);
    this.isNewTemplate.set(true);
    this.originalTemplateId = null;
    setTimeout(() => this.templateEditor?.loadTemplate(createEmptyTemplate()));
  }

  saveTemplate(): void {
    if (!this.templateEditor) return;
    const template = this.templateEditor.getValue();

    if (!template.id.trim()) {
      this.snackBar.open('Template ID is required', 'OK', { duration: 3000 });
      return;
    }

    if (this.isNewTemplate()) {
      this.api.createTemplate(template).subscribe({
        next: () => {
          this.snackBar.open(`Template '${template.id}' created`, 'OK', { duration: 3000 });
          this.isNewTemplate.set(false);
          this.selectedTemplateId.set(template.id);
          this.originalTemplateId = template.id;
          this.loadTemplates();
        },
        error: (err) => {
          const msg = err.error?.error || 'Failed to create template';
          this.snackBar.open(msg, 'OK', { duration: 5000 });
        },
      });
    } else {
      const id = this.originalTemplateId ?? template.id;
      this.api.updateTemplate(id, template).subscribe({
        next: () => {
          this.snackBar.open(`Template '${template.id}' saved`, 'OK', { duration: 3000 });
          this.selectedTemplateId.set(template.id);
          this.originalTemplateId = template.id;
          this.loadTemplates();
        },
        error: (err) => {
          const msg = err.error?.error || 'Failed to save template';
          this.snackBar.open(msg, 'OK', { duration: 5000 });
        },
      });
    }
  }

  archiveTemplate(): void {
    const id = this.originalTemplateId ?? this.selectedTemplateId();
    if (!id) return;

    if (!confirm(`Archive template '${id}'? It can be restored later.`)) return;

    this.api.archiveTemplate(id).subscribe({
      next: () => {
        this.snackBar.open(`Template '${id}' archived`, 'OK', { duration: 3000 });
        this.selectedTemplateId.set(null);
        this.isNewTemplate.set(false);
        this.originalTemplateId = null;
        this.loadTemplates();
        if (this.showArchived()) this.loadArchivedTemplates();
      },
      error: (err) => {
        const msg = err.error?.error || 'Failed to archive template';
        this.snackBar.open(msg, 'OK', { duration: 5000 });
      },
    });
  }

  toggleArchived(): void {
    this.showArchived.set(!this.showArchived());
    if (this.showArchived()) {
      this.loadArchivedTemplates();
    }
  }

  restoreTemplate(id: string): void {
    this.api.restoreTemplate(id).subscribe({
      next: () => {
        this.snackBar.open(`Template '${id}' restored`, 'OK', { duration: 3000 });
        this.loadTemplates();
        this.loadArchivedTemplates();
      },
      error: (err) => {
        const msg = err.error?.error || 'Failed to restore template';
        this.snackBar.open(msg, 'OK', { duration: 5000 });
      },
    });
  }

  private loadArchivedTemplates(): void {
    this.api.getArchivedTemplates().subscribe({
      next: (res) => this.archivedTemplates.set(res.templates),
      error: () => this.archivedTemplates.set([]),
    });
  }
}
