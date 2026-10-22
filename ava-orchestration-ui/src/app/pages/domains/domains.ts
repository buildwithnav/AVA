import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { MatListModule } from '@angular/material/list';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDividerModule } from '@angular/material/divider';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { OrchestrationService, DomainDefinition } from '../../services/orchestration';
import { DomainFilesPanel } from './domain-files-panel';
import { DomainKbPanel } from './domain-kb-panel';
import { ChildDomainsPanel } from './child-domains-panel';

/**
 * Domains page — sidebar tree (top-level domains, expandable to their children) with an
 * editor panel. Children (e.g. individual studies) are full DomainDefinition rows with
 * parentId set, capped at 2 tiers, so the same editor serves both tiers.
 * Supports create, update, archive, restore, and embedded domain file management.
 */
@Component({
  selector: 'app-domains',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatListModule, MatExpansionModule, MatButtonModule,
    MatIconModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, MatDividerModule, MatSnackBarModule, MatTooltipModule,
    DomainFilesPanel, DomainKbPanel, ChildDomainsPanel,
  ],
  template: `
    <div class="dm-layout">
      <!-- Left sidebar -->
      <div class="dm-sidebar">
        <div class="dm-sidebar-header">
          <mat-form-field appearance="outline" class="dm-search-field" subscriptSizing="dynamic">
            <mat-label>Search...</mat-label>
            <mat-icon matPrefix>search</mat-icon>
            <input matInput [ngModel]="searchQuery()" (ngModelChange)="searchQuery.set($event)" />
            @if (searchQuery()) {
              <button matSuffix mat-icon-button (click)="searchQuery.set('')">
                <mat-icon>close</mat-icon>
              </button>
            }
          </mat-form-field>
          <button mat-mini-fab color="primary" (click)="newDomain()" matTooltip="New domain">
            <mat-icon>add</mat-icon>
          </button>
        </div>

        <div class="dm-sidebar-list">
          <mat-accordion multi>
            @for (d of filteredDomains(); track d.id) {
              <mat-expansion-panel [expanded]="isExpanded(d.id)" [hideToggle]="childrenFor(d.id).length === 0"
                                    (opened)="setExpanded(d.id, true)" (closed)="setExpanded(d.id, false)">
                <mat-expansion-panel-header>
                  <mat-panel-title>
                    <mat-nav-list dense class="dm-title-list">
                      <a mat-list-item (click)="selectDomain(d); $event.stopPropagation()"
                         [activated]="selectedId() === d.id">
                        <span matListItemTitle>{{ d.name }}</span>
                        <span matListItemLine>{{ d.description }}</span>
                      </a>
                    </mat-nav-list>
                  </mat-panel-title>
                </mat-expansion-panel-header>

                @if (childrenFor(d.id).length > 0) {
                  <mat-nav-list dense class="tree-container">
                    @for (c of childrenFor(d.id); track c.id; let last = $last) {
                      <div class="tree-node" [class.tree-node-last]="last">
                        <a mat-list-item (click)="selectDomain(c)"
                           [activated]="selectedId() === c.id" [class.dm-child-archived]="!c.enabled">
                          <span matListItemTitle>{{ c.name }}</span>
                          <span matListItemLine>{{ c.description }}</span>
                        </a>
                      </div>
                    }
                  </mat-nav-list>
                }
              </mat-expansion-panel>
            }
          </mat-accordion>

          @if (filteredDomains().length === 0 && !showArchived()) {
            <p class="dm-empty">{{ searchQuery() ? 'No domains match your search.' : 'No domains found. Click + to create one.' }}</p>
          }
        </div>

        <div class="dm-sidebar-footer">
          <button mat-button (click)="toggleArchived()">
            <mat-icon>{{ showArchived() ? 'visibility_off' : 'inventory_2' }}</mat-icon>
            {{ showArchived() ? 'Hide Archived' : 'View Archived' }}
          </button>

          @if (showArchived() && archivedDomains().length > 0) {
            <mat-nav-list class="dm-archived-list">
              @for (d of archivedDomains(); track d.id) {
                <a mat-list-item (click)="selectDomain(d)"
                   [activated]="selectedId() === d.id"
                   class="dm-archived-item">
                  <span matListItemTitle>{{ d.name }}</span>
                  <span matListItemLine>Archived</span>
                </a>
              }
            </mat-nav-list>
          }
          @if (showArchived() && archivedDomains().length === 0) {
            <p class="dm-empty">No archived domains.</p>
          }
        </div>
      </div>

      <!-- Right panel -->
      <div class="dm-editor-panel">
        @if (selectedId() || isNew()) {
          <div class="dm-editor">
            <!-- Toolbar -->
            <div class="dm-toolbar">
              <span class="dm-toolbar-title">
                <mat-icon>public</mat-icon>
                {{ isNew() ? (isChild() ? 'New Child Domain' : 'New Domain') : (isChild() ? 'Edit Child Domain' : 'Edit Domain') }}
              </span>
              <span class="dm-spacer"></span>
              <button mat-raised-button color="primary" (click)="saveDomain()">
                <mat-icon>save</mat-icon> Save
              </button>
              @if (!isNew() && editEnabled) {
                <button mat-raised-button color="warn" (click)="archiveDomain()" matTooltip="Archive this domain">
                  <mat-icon>archive</mat-icon> Archive
                </button>
              }
              @if (!isNew() && !editEnabled) {
                <button mat-raised-button color="primary" (click)="restoreDomain()" matTooltip="Restore this domain">
                  <mat-icon>unarchive</mat-icon> Restore
                </button>
              }
            </div>

            <!-- Editor form -->
            <div class="dm-form-scroll">
              @if (isChild()) {
                <div class="dm-parent-breadcrumb">
                  <button mat-button (click)="backToParent()">
                    <mat-icon>arrow_back</mat-icon> {{ parentName() }}
                  </button>
                  <span class="dm-breadcrumb-sep">/</span>
                  <span>{{ isNew() ? 'New Child Domain' : editName }}</span>
                </div>
              }

              <div class="dm-form">
                <div class="dm-form-left">
                  <mat-form-field appearance="outline" class="dm-field-full" subscriptSizing="dynamic">
                    <mat-label>Domain ID</mat-label>
                    <input matInput [(ngModel)]="editId" [readonly]="!isNew()" placeholder="e.g. my-domain">
                    @if (isNew()) {
                      <mat-hint>Unique slug-style identifier (cannot be changed later)</mat-hint>
                    }
                  </mat-form-field>

                  <mat-form-field appearance="outline" class="dm-field-full" subscriptSizing="dynamic">
                    <mat-label>Name</mat-label>
                    <input matInput [(ngModel)]="editName" placeholder="Display name">
                  </mat-form-field>

                  <mat-form-field appearance="outline" class="dm-field-full" subscriptSizing="dynamic">
                    <mat-label>KB Prompt Mode</mat-label>
                    <mat-select [(ngModel)]="editKbPromptMode">
                      <mat-option value="inline">Inline — full KB schema in prompt</mat-option>
                      <mat-option value="catalog">Catalog — table list + on-demand schema</mat-option>
                    </mat-select>
                    <mat-hint>Use "Catalog" for domains with many/large knowledge bases</mat-hint>
                  </mat-form-field>

                  @if (!isChild()) {
                    <mat-form-field appearance="outline" class="dm-field-full" subscriptSizing="dynamic">
                      <mat-label>Module Key</mat-label>
                      <input matInput [(ngModel)]="editModuleKey" placeholder="e.g. AVACLS">
                      <mat-hint>CDS gateway module gating this domain. Must be registered with the CDS admin. Leave blank to inherit the unmapped-domain policy.</mat-hint>
                    </mat-form-field>
                  } @else {
                    <mat-form-field appearance="outline" class="dm-field-full" subscriptSizing="dynamic">
                      <mat-label>External Ref</mat-label>
                      <input matInput [(ngModel)]="editExternalRef" placeholder="e.g. the study ID">
                      <mat-hint>Access to this domain is controlled by grants on its parent, not a module key.</mat-hint>
                    </mat-form-field>
                  }
                </div>

                <mat-form-field appearance="outline" class="dm-form-right">
                  <mat-label>Description</mat-label>
                  <textarea matInput [(ngModel)]="editDescription"
                            placeholder="What this domain covers"></textarea>
                </mat-form-field>
              </div>

              <!-- Agentic Instruction -->
              <mat-form-field appearance="outline" class="dm-field-full dm-agentic-field">
                <mat-label>Agentic Instruction</mat-label>
                <textarea matInput [(ngModel)]="editAgenticInstruction" rows="5"
                          placeholder="Domain-specific guidance injected into the system prompt (e.g. join rules, response style, constraints)"></textarea>
                <mat-hint>This instruction is appended to the system prompt for all conversations in this domain</mat-hint>
              </mat-form-field>

              <!-- Child domains & grants (only for saved, top-level domains — nesting is capped at 2 tiers) -->
              @if (!isNew() && selectedId() && !isChild()) {
                <mat-divider class="dm-divider"></mat-divider>
                <h3 class="dm-files-heading">Child Domains &amp; Access</h3>
                <app-child-domains-panel [parentId]="selectedId()!"
                  (addChild)="newChildDomain($event)" (editChild)="selectDomain($event)" />
              }

              <!-- Domain Knowledge Bases (only shown for saved domains) -->
              @if (!isNew() && selectedId()) {
                <mat-divider class="dm-divider"></mat-divider>
                <h3 class="dm-files-heading">Knowledge Bases</h3>
                <app-domain-kb-panel [domainId]="selectedId()!" />
              }

              <!-- Domain Files (only shown for saved domains) -->
              @if (!isNew() && selectedId()) {
                <mat-divider class="dm-divider"></mat-divider>
                <h3 class="dm-files-heading">Domain Files</h3>
                <app-domain-files-panel [domainId]="selectedId()!" />
              }
            </div>
          </div>
        } @else {
          <div class="dm-placeholder">
            <mat-icon>public</mat-icon>
            <h3>Select a domain or create a new one</h3>
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; height: 100%; }
    .dm-layout {
      display: grid;
      grid-template-columns: 300px 1fr;
      height: 100%;
      overflow: hidden;
    }
    .dm-sidebar {
      border-right: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      display: flex;
      flex-direction: column;
      overflow: hidden;
    }
    .dm-sidebar-header {
      display: flex; align-items: center; padding: 10px 12px; gap: 8px; flex-shrink: 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
    }
    .dm-search-field {
      flex: 1;
      ::ng-deep {
        .mat-mdc-form-field-subscript-wrapper { display: none; }
        mat-icon[matPrefix] { margin-right: 8px; color: rgba(0, 0, 0, 0.5); }
      }
    }
    .dm-sidebar-list {
      flex: 1; overflow-y: auto; overflow-x: hidden; min-height: 0;
      padding: 4px 8px 4px 0;
      ::ng-deep {
        .mat-expansion-panel {
          box-shadow: none !important;
          background: transparent;
          border-radius: 0 !important;
          &::before { display: none; }
        }
        .mat-expansion-panel-spacing { margin: 0; }
        .mat-expansion-panel-header {
          height: auto !important;
          padding: 0 8px 0 2px;
          &:hover { background: transparent !important; }
        }
        .mat-expansion-panel-body { padding: 0 0 4px 0; }
        .mat-content {
          align-items: center;
          overflow: hidden;
          min-width: 0;
        }
        mat-panel-title, .mat-expansion-panel-header-title {
          min-width: 0;
          overflow: hidden;
        }
      }
    }
    .dm-title-list {
      width: 100%;
      padding: 0;
      ::ng-deep .mdc-list {
        padding: 0;
      }
    }
    .dm-title-list ::ng-deep .mat-mdc-list-item {
      border-radius: 8px;
      padding: 0 8px;
      &:hover { background: var(--mat-sys-surface-container, #eee); }
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
    .dm-child-archived { opacity: 0.6; }
    .dm-sidebar-footer {
      border-top: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      padding: 8px;
      flex-shrink: 0;
      max-height: 200px;
      overflow-y: auto;
    }
    .dm-archived-list { padding-top: 0; }
    .dm-archived-item { opacity: 0.6; }
    .dm-editor-panel { overflow: hidden; height: 100%; }
    .dm-editor { display: flex; flex-direction: column; height: 100%; }
    .dm-toolbar {
      display: flex; align-items: center; gap: 8px;
      padding: 8px 16px;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      flex-shrink: 0;
    }
    .dm-toolbar-title {
      display: flex; align-items: center; gap: 8px;
      font-size: 16px; font-weight: 500;
    }
    .dm-spacer { flex: 1; }
    .dm-form-scroll { flex: 1; overflow-y: auto; padding: 24px; }
    .dm-parent-breadcrumb {
      display: flex; align-items: center; gap: 8px; margin-bottom: 16px;
      font-size: 14px; opacity: 0.8;
    }
    .dm-breadcrumb-sep { opacity: 0.5; }
    .dm-form {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 16px;
    }
    .dm-form-left {
      display: flex; flex-direction: column; gap: 20px;
    }
    .dm-form-right {
      width: 100%;
      height: 100%;
      ::ng-deep {
        .mat-mdc-form-field-flex { height: 100%; align-items: stretch; }
        .mat-mdc-text-field-wrapper { height: 100%; }
        textarea { height: 100% !important; resize: none; }
      }
    }
    .dm-field-full { width: 100%; }
    .dm-agentic-field { margin-top: 20px; }
    .dm-agentic-field textarea { font-family: 'Consolas', 'Courier New', monospace; font-size: 13px; }
    .dm-divider { margin: 24px 0 16px; }
    .dm-files-heading { font-size: 16px; font-weight: 500; margin: 0 0 12px; }
    .dm-empty { padding: 16px; text-align: center; opacity: 0.6; font-size: 13px; }
    .dm-placeholder {
      display: flex; flex-direction: column; align-items: center;
      justify-content: center; height: 100%; opacity: 0.5;
      mat-icon { font-size: 64px; width: 64px; height: 64px; }
    }
  `],
})
export class Domains implements OnInit {
  private readonly svc = inject(OrchestrationService);
  private readonly snackBar = inject(MatSnackBar);

  domains = signal<DomainDefinition[]>([]);
  archivedDomains = signal<DomainDefinition[]>([]);
  selectedId = signal<string | null>(null);
  isNew = signal(false);
  showArchived = signal(false);
  searchQuery = signal('');

  /** Which top-level domains are expanded in the sidebar tree. */
  expandedIds = signal<Set<string>>(new Set());
  /** Lazy-loaded + cached children per parent id, for the sidebar tree. */
  childrenByParent = signal<Record<string, DomainDefinition[]>>({});

  /** Filtered domains based on search query */
  filteredDomains = computed(() => {
    const query = this.searchQuery().toLowerCase().trim();
    const all = this.domains();
    if (!query) return all;
    return all.filter(d =>
      d.id.toLowerCase().includes(query) ||
      d.name.toLowerCase().includes(query) ||
      (d.description || '').toLowerCase().includes(query)
    );
  });

  // Editor form fields
  editId = '';
  editName = '';
  editDescription = '';
  editAgenticInstruction = '';
  editEnabled = true;
  editKbPromptMode: 'inline' | 'catalog' | string = 'inline';
  editModuleKey = '';
  editExternalRef = '';
  /** Full domain currently loaded in the editor — preserved so a save doesn't drop server-managed
   *  fields. A signal (not a plain field) so isChild()/parentName() re-evaluate whenever it changes —
   *  computed() only tracks signal reads, not plain mutable field reads. */
  private loadedDomain = signal<DomainDefinition | null>(null);
  /** Parent id for a domain being newly created as a child — only meaningful while isNew(). */
  private editParentId = signal<string | null>(null);

  /** True when the editor is showing a child domain (existing or being newly created under a parent). */
  isChild = computed(() => !!(this.isNew() ? this.editParentId() : this.loadedDomain()?.parentId));

  /** Display name of the parent, for the breadcrumb — looked up from the top-level domains list. */
  parentName = computed(() => {
    const pid = this.isNew() ? this.editParentId() : (this.loadedDomain()?.parentId ?? null);
    if (!pid) return '';
    return this.domains().find(d => d.id === pid)?.name ?? pid;
  });

  ngOnInit(): void {
    this.loadDomains();
  }

  private loadDomains(): void {
    this.svc.getAllDomains(true).subscribe({
      next: (all) => {
        const enabled = all.filter(d => d.enabled);
        this.domains.set(enabled);
        this.archivedDomains.set(all.filter(d => !d.enabled));
        this.preloadChildren(all);
      },
      error: () => this.snackBar.open('Failed to load domains', 'Dismiss', { duration: 3000 }),
    });
  }

  /** Loads every top-level domain's children up front, alongside the parent list, so expanding
   *  the sidebar tree never needs its own round-trip. */
  private preloadChildren(topLevel: DomainDefinition[]): void {
    if (topLevel.length === 0) {
      this.childrenByParent.set({});
      return;
    }
    forkJoin(
      topLevel.map(d => this.svc.getChildDomains(d.id, true).pipe(catchError(() => of([] as DomainDefinition[]))))
    ).subscribe(results => {
      const map: Record<string, DomainDefinition[]> = {};
      topLevel.forEach((d, i) => { map[d.id] = results[i]; });
      this.childrenByParent.set(map);
    });
  }

  isExpanded(id: string): boolean {
    return this.expandedIds().has(id);
  }

  childrenFor(id: string): DomainDefinition[] {
    return this.childrenByParent()[id] ?? [];
  }

  setExpanded(id: string, expanded: boolean): void {
    const next = new Set(this.expandedIds());
    if (expanded) next.add(id); else next.delete(id);
    this.expandedIds.set(next);
  }

  private refreshChildren(parentId: string): void {
    this.svc.getChildDomains(parentId, true).subscribe({
      next: (children) => this.childrenByParent.set({ ...this.childrenByParent(), [parentId]: children }),
      error: () => this.childrenByParent.set({ ...this.childrenByParent(), [parentId]: [] }),
    });
  }

  selectDomain(d: DomainDefinition): void {
    this.selectedId.set(d.id);
    this.isNew.set(false);
    this.loadedDomain.set(d);
    this.editParentId.set(null);
    this.editId = d.id;
    this.editName = d.name;
    this.editDescription = d.description;
    this.editAgenticInstruction = d.agenticInstruction || '';
    this.editEnabled = d.enabled;
    this.editKbPromptMode = d.kbPromptMode || 'inline';
    this.editModuleKey = d.moduleKey || '';
    this.editExternalRef = d.externalRef || '';
  }

  newDomain(): void {
    this.selectedId.set(null);
    this.isNew.set(true);
    this.loadedDomain.set(null);
    this.editParentId.set(null);
    this.editId = '';
    this.editName = '';
    this.editDescription = '';
    this.editAgenticInstruction = '';
    this.editEnabled = true;
    this.editKbPromptMode = 'inline';
    this.editModuleKey = '';
    this.editExternalRef = '';
  }

  /** Opens the editor for a brand-new child domain under parentId — same editor, same panel. */
  newChildDomain(parentId: string): void {
    this.newDomain();
    this.editParentId.set(parentId);
  }

  /** Returns from a child's editor to its parent's editor. */
  backToParent(): void {
    const pid = this.isNew() ? this.editParentId() : (this.loadedDomain()?.parentId ?? null);
    if (!pid) return;
    const parent = this.domains().find(d => d.id === pid);
    if (parent) this.selectDomain(parent);
  }

  saveDomain(): void {
    if (!this.editId.trim()) {
      this.snackBar.open('Domain ID is required', 'OK', { duration: 3000 });
      return;
    }
    if (!this.editName.trim()) {
      this.snackBar.open('Domain name is required', 'OK', { duration: 3000 });
      return;
    }

    const parentId = this.isNew() ? this.editParentId() : (this.loadedDomain()?.parentId ?? null);

    const payload: Partial<DomainDefinition> = {
      ...(this.loadedDomain() ?? {}),
      id: this.editId.trim(),
      name: this.editName.trim(),
      description: this.editDescription.trim(),
      agenticInstruction: this.editAgenticInstruction.trim() || undefined,
      enabled: true,
      kbPromptMode: this.editKbPromptMode || 'inline',
      moduleKey: parentId ? null : (this.editModuleKey.trim() || null),
      parentId: parentId || null,
      externalRef: parentId ? (this.editExternalRef.trim() || null) : null,
    };

    if (this.isNew()) {
      this.svc.createDomain(payload).subscribe({
        next: () => {
          this.snackBar.open(`Domain '${payload.name}' created`, 'OK', { duration: 3000 });
          this.isNew.set(false);
          this.selectedId.set(payload.id!);
          this.editEnabled = true;
          this.loadedDomain.set(payload as DomainDefinition);
          if (parentId) this.refreshChildren(parentId); else this.loadDomains();
        },
        error: (err) => {
          const msg = err.error?.error || 'Failed to create domain';
          this.snackBar.open(msg, 'OK', { duration: 5000 });
        },
      });
    } else {
      this.svc.updateDomain(this.editId, payload).subscribe({
        next: () => {
          this.snackBar.open(`Domain '${payload.name}' saved`, 'OK', { duration: 3000 });
          this.loadedDomain.set(payload as DomainDefinition);
          if (parentId) this.refreshChildren(parentId); else this.loadDomains();
        },
        error: (err) => {
          const msg = err.error?.error || 'Failed to save domain';
          this.snackBar.open(msg, 'OK', { duration: 5000 });
        },
      });
    }
  }

  archiveDomain(): void {
    const id = this.selectedId();
    if (!id) return;
    if (!confirm(`Archive domain '${this.editName}'? It can be restored later.`)) return;

    const parentId = this.loadedDomain()?.parentId ?? null;

    this.svc.archiveDomain(id).subscribe({
      next: () => {
        this.snackBar.open(`Domain '${this.editName}' archived`, 'OK', { duration: 3000 });
        this.selectedId.set(null);
        this.isNew.set(false);
        if (parentId) this.refreshChildren(parentId); else this.loadDomains();
      },
      error: (err) => {
        const msg = err.error?.error || 'Failed to archive domain';
        this.snackBar.open(msg, 'OK', { duration: 5000 });
      },
    });
  }

  restoreDomain(): void {
    const id = this.selectedId();
    if (!id) return;

    const parentId = this.loadedDomain()?.parentId ?? null;

    this.svc.restoreDomain(id).subscribe({
      next: () => {
        this.snackBar.open(`Domain '${this.editName}' restored`, 'OK', { duration: 3000 });
        this.editEnabled = true;
        if (parentId) this.refreshChildren(parentId); else this.loadDomains();
      },
      error: (err) => {
        const msg = err.error?.error || 'Failed to restore domain';
        this.snackBar.open(msg, 'OK', { duration: 5000 });
      },
    });
  }

  toggleArchived(): void {
    this.showArchived.set(!this.showArchived());
  }
}
