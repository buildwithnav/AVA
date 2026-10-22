import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { MatListModule } from '@angular/material/list';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import {
  WorkflowEditorComponent,
  WorkflowBuilderService,
  WorkflowApiService,
  WorkflowCreatorService,
  WorkflowCreationViewComponent,
  DomainInfo,
  PromptDefinition,
} from '@ava/workflow-builder';
import { OrchestrationService } from '../../services/orchestration';

/**
 * Workflow Builder page — wraps the shared WorkflowEditorComponent
 * with a prompt list sidebar and API integration.
 */
@Component({
  selector: 'app-workflow-builder',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatListModule, MatButtonModule,
    MatIconModule, MatCardModule, MatDividerModule,
    MatTooltipModule, MatExpansionModule, MatFormFieldModule,
    MatInputModule, WorkflowEditorComponent, WorkflowCreationViewComponent,
  ],
  template: `
    <div class="wb-layout" [class.creating]="isCreating()">
      <!-- Left sidebar (hidden during creation) -->
      @if (!isCreating()) {
      <div class="wb-sidebar">
        <div class="wb-sidebar-header">
          <mat-form-field appearance="outline" class="wb-search-field" subscriptSizing="dynamic">
            <mat-label>Search...</mat-label>
            <mat-icon matPrefix>search</mat-icon>
            <input matInput [ngModel]="searchQuery()" (ngModelChange)="searchQuery.set($event)" />
            @if (searchQuery()) {
              <button matSuffix mat-icon-button (click)="searchQuery.set('')">
                <mat-icon>close</mat-icon>
              </button>
            }
          </mat-form-field>
          <button mat-mini-fab color="primary" (click)="newWorkflow()" matTooltip="New workflow">
            <mat-icon>add</mat-icon>
          </button>
        </div>

        <div class="wb-sidebar-list">
          <mat-accordion multi>
            @for (group of groupedPrompts(); track group.domain) {
              <mat-expansion-panel [expanded]="shouldExpandDomain(group.domain)">
                <mat-expansion-panel-header>
                  <mat-panel-title>
                    <span class="wb-domain-name">{{ group.domainName }}</span>
                    <span class="wb-domain-badge">{{ group.prompts.length }}</span>
                  </mat-panel-title>
                </mat-expansion-panel-header>

                <mat-nav-list dense class="tree-container">
                  @for (p of group.prompts; track p.id; let last = $last) {
                    <div class="tree-node" [class.tree-node-last]="last">
                      <a mat-list-item (click)="selectPrompt(p)"
                         [activated]="selectedId() === p.id">
                        <span matListItemTitle>{{ p.id }}</span>
                        <span matListItemLine>{{ p.description }}</span>
                      </a>
                    </div>
                  }
                </mat-nav-list>
              </mat-expansion-panel>
            }
          </mat-accordion>

          @if (filteredPrompts().length === 0 && !showArchived()) {
            <p class="wb-empty">{{ searchQuery() ? 'No workflows match your search.' : 'No workflows found. Click + to create one.' }}</p>
          }
        </div>

        <div class="wb-sidebar-footer">
          <button mat-button (click)="toggleArchived()">
            <mat-icon>{{ showArchived() ? 'visibility_off' : 'inventory_2' }}</mat-icon>
            {{ showArchived() ? 'Hide Archived' : 'View Archived' }}
          </button>

          @if (showArchived() && archivedPrompts().length > 0) {
            <mat-nav-list class="wb-archived-list">
              @for (p of archivedPrompts(); track p.id) {
                <a mat-list-item class="wb-archived-item">
                  <span matListItemTitle>{{ p.id }}</span>
                  <span matListItemLine>Archived</span>
                  <button mat-icon-button matListItemMeta (click)="restoreWorkflow(p.id)" matTooltip="Restore">
                    <mat-icon>unarchive</mat-icon>
                  </button>
                </a>
              }
            </mat-nav-list>
          }
          @if (showArchived() && archivedPrompts().length === 0) {
            <p class="wb-empty">No archived workflows.</p>
          }
        </div>
      </div>
      }

      <!-- Right panel -->
      <div class="wb-editor-panel">
        @if (isCreating()) {
          <wfb-workflow-creation-view
            (exitCreation)="exitCreation()"
            (openBlankEditor)="openBlankEditor()"
          />
        } @else if (selectedId() || isNew()) {
          <wfb-workflow-editor [domains]="availableDomains()" (saved)="onWorkflowSaved()" (archived)="archiveWorkflow($event)" (editWithAi)="editWithAi()" />
        } @else {
          <div class="wb-placeholder">
            <mat-icon>schema</mat-icon>
            <h3>Select a workflow or create a new one</h3>
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; height: 100%; }
    .wb-layout {
      display: grid;
      grid-template-columns: 300px 1fr;
      height: 100%;
      overflow: hidden;
    }
    .wb-layout.creating {
      grid-template-columns: 1fr;
    }
    .wb-sidebar {
      border-right: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      display: flex;
      flex-direction: column;
      overflow: hidden;
      background: var(--mat-sys-surface-container-lowest, #fafafa);
    }
    .wb-sidebar-header {
      display: flex; align-items: center; padding: 10px 12px; gap: 8px; flex-shrink: 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
    }
    .wb-search-field {
      flex: 1;
      ::ng-deep {
        .mat-mdc-form-field-subscript-wrapper { display: none; }
        mat-icon[matPrefix] { margin-right: 8px; color: rgba(0, 0, 0, 0.5); }
      }
    }
    .wb-sidebar-list {
      flex: 1; overflow-y: auto; overflow-x: hidden; min-height: 0;
      padding: 4px 0;
      ::ng-deep {
        .mat-expansion-panel {
          box-shadow: none !important;
          background: transparent;
          border-radius: 0 !important;
          &::before { display: none; }
        }
        .mat-expansion-panel-spacing { margin: 0; }
        .mat-expansion-panel-header {
          padding: 0 14px;
          height: 44px;
          font-size: 13px;
          &:hover { background: var(--mat-sys-surface-container, #f0f0f0) !important; }
        }
        .mat-expansion-panel-body { padding: 0 0 4px 0; }
        .mat-content { align-items: center; overflow: visible; }
      }
    }
    .tree-container {
      margin-left: 20px;
      padding: 0;
      position: relative;
    }
    .tree-node {
      position: relative;
      padding-left: 14px;
      &::before {
        content: '';
        position: absolute;
        left: 0;
        top: 0;
        bottom: 0;
        width: 2px;
        background: var(--mat-sys-outline-variant, #d0d0d0);
      }
      &::after {
        content: '';
        position: absolute;
        left: 0;
        top: 50%;
        width: 14px;
        height: 2px;
        background: var(--mat-sys-outline-variant, #d0d0d0);
      }
    }
    .tree-node-last::before {
      bottom: 50%;
    }
    .tree-container ::ng-deep .mat-mdc-list-item {
      border-radius: 8px;
      &:hover { background: var(--mat-sys-surface-container, #eee); }
    }
    .wb-domain-name {
      font-weight: 500;
      font-size: 13px;
      letter-spacing: 0.01em;
      color: var(--mat-sys-on-surface, #1a1a1a);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      flex: 1;
    }
    .wb-domain-badge {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: 22px;
      height: 22px;
      padding: 0 6px;
      border-radius: 11px;
      background: var(--mat-sys-primary-container, #d4e3ff);
      color: var(--mat-sys-on-primary-container, #1a3d6e);
      font-size: 11px;
      font-weight: 600;
      flex-shrink: 0;
      margin-left: 8px;
    }
    .wb-sidebar-footer {
      border-top: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      padding: 8px;
      flex-shrink: 0;
      max-height: 200px;
      overflow-y: auto;
    }
    .wb-archived-list { padding-top: 0; }
    .wb-archived-item { opacity: 0.6; }
    .wb-editor-panel { overflow: hidden; height: 100%; }
    .wb-empty { padding: 16px; text-align: center; opacity: 0.6; font-size: 13px; }
    .wb-placeholder {
      display: flex; flex-direction: column; align-items: center;
      justify-content: center; height: 100%; opacity: 0.5;
      mat-icon { font-size: 64px; width: 64px; height: 64px; }
    }
  `],
})
export class WorkflowBuilder implements OnInit {
  private readonly api = inject(WorkflowApiService);
  private readonly builder = inject(WorkflowBuilderService);
  private readonly creator = inject(WorkflowCreatorService);
  private readonly orchSvc = inject(OrchestrationService);

  prompts = signal<PromptDefinition[]>([]);
  archivedPrompts = signal<PromptDefinition[]>([]);
  selectedId = signal<string | null>(null);
  isNew = signal(false);
  isCreating = signal(false);
  showArchived = signal(false);
  availableDomains = signal<DomainInfo[]>([]);
  searchQuery = signal('');

  /** Filtered prompts based on search query */
  filteredPrompts = computed(() => {
    const query = this.searchQuery().toLowerCase().trim();
    const all = this.prompts();
    if (!query) return all;
    return all.filter(p =>
      p.id.toLowerCase().includes(query) ||
      (p.domain || '').toLowerCase().includes(query) ||
      (p.description || '').toLowerCase().includes(query)
    );
  });

  /** Prompts grouped by domain for the accordion */
  groupedPrompts = computed(() => {
    const filtered = this.filteredPrompts();
    const domainMap = new Map(this.availableDomains().map(d => [d.id, d.name]));
    const groups = new Map<string, PromptDefinition[]>();
    for (const p of filtered) {
      const domain = p.domain || '';
      if (!groups.has(domain)) groups.set(domain, []);
      groups.get(domain)!.push(p);
    }
    return Array.from(groups.entries())
      .map(([domain, prompts]) => ({
        domain,
        domainName: domain ? (domainMap.get(domain) ?? domain) : 'Uncategorized',
        prompts,
      }))
      .sort((a, b) => a.domainName.localeCompare(b.domainName));
  });

  /** Determine if a domain panel should be auto-expanded */
  shouldExpandDomain(domain: string): boolean {
    const groups = this.groupedPrompts();
    // Auto-expand if only 1 domain group exists
    if (groups.length === 1) return true;
    // Auto-expand if search is active (show all matching results)
    if (this.searchQuery().trim()) return true;
    // Auto-expand the domain containing the currently selected workflow
    const selected = this.selectedId();
    if (selected) {
      const group = groups.find(g => g.domain === domain);
      if (group?.prompts.some(p => p.id === selected)) return true;
    }
    return false;
  }

  ngOnInit(): void {
    this.loadPrompts();
    this.loadTools();
    this.loadTemplates();
    this.loadDomains();
    this.loadMcpToolNames();
  }

  private loadPrompts(onLoaded?: () => void): void {
    this.api.getPrompts().subscribe({
      next: (res) => {
        this.prompts.set(res.prompts);
        this.builder.setAllPrompts(res.prompts);
        onLoaded?.();
      },
      error: () => {},
    });
  }

  private loadTools(): void {
    this.orchSvc.getTools().subscribe({
      next: (res) => this.builder.setAvailableTools(res.tools as any[]),
      error: () => {},
    });
  }

  /** Load templates so they're available for workflow step references */
  private loadTemplates(): void {
    this.api.getTemplates().subscribe({
      next: (res) => this.builder.setAvailableTemplates(res.templates as any[]),
      error: () => {},
    });
  }

  private loadDomains(): void {
    this.orchSvc.getDomains().subscribe({
      next: (domains) => {
        const childRequests = domains.map(parent =>
          this.orchSvc.getChildDomains(parent.id, false, true).pipe(catchError(() => of([])))
        );

        forkJoin(childRequests).subscribe({
          next: (childrenByParent) => {
            const selectableDomains = domains.flatMap((parent, index) => [
              { id: parent.id, name: parent.name },
              ...childrenByParent[index].map(child => ({
                id: child.id,
                name: `${parent.name} - ${child.name}`,
              })),
            ]);
            this.availableDomains.set(
              selectableDomains.sort((a, b) => a.name.localeCompare(b.name))
            );
          },
          error: () => this.availableDomains.set(
            [...domains].sort((a, b) => a.name.localeCompare(b.name))
          ),
        });
      },
      error: () => {},
    });
  }

  private loadMcpToolNames(): void {
    this.api.getMcpToolNames().subscribe({
      next: (names) => this.builder.setAvailableMcpToolNames(names),
      error: () => {},
    });
  }

  selectPrompt(p: PromptDefinition): void {
    this.selectedId.set(p.id);
    this.isNew.set(false);
    this.builder.loadPrompt(p);
  }

  newWorkflow(): void {
    this.selectedId.set(null);
    this.isNew.set(false);
    this.isCreating.set(true);
    this.creator.startSession();
  }

  /** Open the AI editor seeded with the currently selected workflow. */
  editWithAi(): void {
    const current = this.builder.activePrompt();
    if (!current?.id) return;
    if (this.builder.isDirty()) {
      const proceed = confirm(
        'You have unsaved changes in the editor. Starting an AI edit will use the current in-memory state. Continue?'
      );
      if (!proceed) return;
    }
    this.isNew.set(false);
    this.isCreating.set(true);
    this.creator.startEditSession(current);
  }

  exitCreation(): void {
    this.isCreating.set(false);
    // If the creator saved a workflow, reload the list first, then select it —
    // selecting before the list loads leaves the sidebar with no matching item
    // to highlight or expand.
    const loadedPrompt = this.builder.activePrompt();
    const newId = loadedPrompt && loadedPrompt.id ? loadedPrompt.id : null;
    this.loadPrompts(() => {
      if (newId) {
        this.selectedId.set(newId);
        this.isNew.set(false);
      }
    });
  }

  openBlankEditor(): void {
    this.isCreating.set(false);
    this.isNew.set(true);
    this.builder.newPrompt('ado');
  }

  onWorkflowSaved(): void {
    this.loadPrompts();
  }

  toggleArchived(): void {
    this.showArchived.set(!this.showArchived());
    if (this.showArchived()) {
      this.loadArchivedPrompts();
    }
  }

  archiveWorkflow(id: string): void {
    this.api.archivePrompt(id).subscribe({
      next: () => {
        this.selectedId.set(null);
        this.isNew.set(false);
        this.loadPrompts();
        if (this.showArchived()) this.loadArchivedPrompts();
      },
    });
  }

  restoreWorkflow(id: string): void {
    this.api.restorePrompt(id).subscribe({
      next: () => {
        this.loadPrompts();
        this.loadArchivedPrompts();
      },
    });
  }

  private loadArchivedPrompts(): void {
    this.api.getArchivedPrompts().subscribe({
      next: (res) => this.archivedPrompts.set(res.prompts),
      error: () => this.archivedPrompts.set([]),
    });
  }
}
