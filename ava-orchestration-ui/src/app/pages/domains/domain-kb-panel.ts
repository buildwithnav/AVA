import { Component, Input, OnChanges, SimpleChanges, signal, ViewChild, ElementRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { OrchestrationService, DomainKb, KBColumnDefinition } from '../../services/orchestration';

/**
 * Panel for managing domain-scoped API knowledge bases.
 * Shown in the domain editor; lets admins create KB definitions,
 * edit column metadata, and delete KB records.
 * Refresh is intentionally in the chat UI — this panel is config only.
 */
@Component({
  selector: 'app-domain-kb-panel',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatButtonModule, MatIconModule, MatProgressBarModule,
    MatSnackBarModule, MatTooltipModule,
    MatFormFieldModule, MatInputModule, MatSelectModule,
  ],
  template: `
    <div class="dkp-container">

      <!-- Hidden file input reused for all KB data-file uploads -->
      <input #dataFileInput type="file" accept=".xlsx,.xls,.csv" hidden
             (change)="onDataFileSelected($event)">

      <!-- Header row -->
      <div class="dkp-header">
        <span class="dkp-title">Domain Knowledge Bases</span>
        <button mat-stroked-button color="primary" (click)="showAddForm.set(!showAddForm())" class="dkp-add-btn">
          <mat-icon>{{ showAddForm() ? 'close' : 'add' }}</mat-icon>
          {{ showAddForm() ? 'Cancel' : 'Add KB' }}
        </button>
      </div>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" class="dkp-progress"></mat-progress-bar>
      }

      <!-- Add KB inline form -->
      @if (showAddForm()) {
        <div class="dkp-add-form">
          <mat-form-field appearance="outline" class="dkp-field">
            <mat-label>KB Name</mat-label>
            <input matInput [(ngModel)]="newName" placeholder="e.g. CTMS Studies">
          </mat-form-field>
          <mat-form-field appearance="outline" class="dkp-field">
            <mat-label>Description (optional)</mat-label>
            <input matInput [(ngModel)]="newDescription">
          </mat-form-field>
          <mat-form-field appearance="outline" class="dkp-field">
            <mat-label>Source Tool ID</mat-label>
            <input matInput [(ngModel)]="newToolId" placeholder="e.g. get-ctms-studies">
            <mat-hint>The tool that fetches this KB's data (used when refreshing)</mat-hint>
          </mat-form-field>
          <div class="dkp-add-actions">
            <button mat-raised-button color="primary" (click)="createKb()" [disabled]="!newName.trim()">
              <mat-icon>save</mat-icon> Create
            </button>
          </div>
        </div>
      }

      <!-- KB list -->
      @if (kbs().length === 0 && !loading()) {
        <p class="dkp-empty">No domain knowledge bases configured. Click "Add KB" to create one.</p>
      }

      @for (kb of kbs(); track kb.id) {
        <div class="dkp-kb-card">
          <!-- KB summary row -->
          <div class="dkp-kb-header" (click)="toggleExpanded(kb.id)">
            <mat-icon class="dkp-kb-icon">storage</mat-icon>
            <div class="dkp-kb-info">
              <span class="dkp-kb-name">{{ kb.name }}</span>
              @if (kb.description) {
                <span class="dkp-kb-desc">{{ kb.description }}</span>
              }
              <span class="dkp-kb-meta">
                {{ kb.type }} ·
                @if (kb.rowCount != null) { {{ kb.rowCount | number }} rows · }
                @if (kb.lastRefreshedAt) {
                  refreshed {{ formatDate(kb.lastRefreshedAt) }}
                } @else {
                  never refreshed
                }
              </span>
            </div>
            <span class="dkp-kb-tool" matTooltip="Source tool ID">
              <mat-icon>build</mat-icon> {{ kb.source.toolId || '—' }}
            </span>
            <button mat-icon-button (click)="triggerUpload(kb, $event)"
                    [disabled]="uploadingId() === kb.id"
                    matTooltip="Upload / replace data file (Excel or CSV)">
              <mat-icon>{{ uploadingId() === kb.id ? 'hourglass_empty' : 'upload_file' }}</mat-icon>
            </button>
            <button mat-icon-button color="warn" (click)="deleteKb(kb, $event)"
                    matTooltip="Delete KB definition">
              <mat-icon>delete_outline</mat-icon>
            </button>
            <mat-icon class="dkp-expand-icon">
              {{ expandedId() === kb.id ? 'expand_less' : 'expand_more' }}
            </mat-icon>
          </div>

          <!-- Expanded: description editor + column definitions editor -->
          @if (expandedId() === kb.id) {
            <div class="dkp-col-editor">
              <!-- Type + Description editor -->
              <div class="dkp-desc-editor">
                <mat-form-field appearance="outline" class="dkp-type-field">
                  <mat-label>Type</mat-label>
                  <mat-select [value]="kb.type"
                              [disabled]="savingTypeId() === kb.id"
                              (selectionChange)="onTypeChange(kb, $event.value)">
                    <mat-option value="dataset">dataset</mat-option>
                    <mat-option value="document">document</mat-option>
                  </mat-select>
                </mat-form-field>
                <mat-form-field appearance="outline" class="dkp-field">
                  <mat-label>Description</mat-label>
                  <input matInput [value]="kb.description || ''"
                         (input)="onDescriptionInput(kb, $event)"
                         placeholder="Brief description of what this KB contains">
                </mat-form-field>
                <button mat-stroked-button color="primary" (click)="saveDescription(kb)"
                        [disabled]="savingDescId() === kb.id" class="dkp-desc-save-btn">
                  <mat-icon>save</mat-icon>
                  {{ savingDescId() === kb.id ? 'Saving…' : 'Save' }}
                </button>
              </div>

              <p class="dkp-col-hint">
                Define labels and descriptions for columns so AVA understands their domain meaning.
                Only fill in the columns you want to annotate.
              </p>

              @if (kb.schema && kb.schema.length > 0) {
                <div class="dkp-col-table-wrap">
                  <table class="dkp-col-table">
                    <thead>
                      <tr>
                        <th>Column</th>
                        <th>Type</th>
                        <th>Label</th>
                        <th>Description</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (col of kb.schema; track col.name) {
                        <tr>
                          <td class="dkp-col-name">{{ col.name }}</td>
                          <td class="dkp-col-type-cell">
                            <input class="dkp-inline-input dkp-type-input"
                                   placeholder="type"
                                   [(ngModel)]="getTypeEdit(kb, col.name, col.type).value">
                          </td>
                          <td>
                            <input class="dkp-inline-input"
                                   placeholder="Human label"
                                   [(ngModel)]="getOrCreateDef(kb, col.name).label">
                          </td>
                          <td>
                            <input class="dkp-inline-input dkp-wide"
                                   placeholder="What this column represents"
                                   [(ngModel)]="getOrCreateDef(kb, col.name).description">
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              } @else {
                <p class="dkp-col-hint dkp-col-no-schema">
                  No schema yet — refresh this KB from the chat to populate column data.
                </p>
              }

              <div class="dkp-col-actions">
                <button mat-raised-button color="primary" (click)="saveColumns(kb)"
                        [disabled]="savingId() === kb.id">
                  <mat-icon>save</mat-icon>
                  {{ savingId() === kb.id ? 'Saving…' : 'Save Column Definitions' }}
                </button>
              </div>
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .dkp-container { padding: 0 4px; }
    .dkp-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px; }
    .dkp-title { font-size: 14px; font-weight: 500; }
    .dkp-add-btn { height: 32px; font-size: 13px; }
    .dkp-progress { margin-bottom: 8px; border-radius: 4px; }
    .dkp-add-form { background: #f5f5f5; border: 1px solid #ddd; border-radius: 8px; padding: 16px; margin-bottom: 16px; display: flex; flex-direction: column; gap: 4px; }
    .dkp-field { width: 100%; }
    .dkp-add-actions { display: flex; justify-content: flex-end; margin-top: 4px; }
    .dkp-empty { color: #888; font-size: 13px; font-style: italic; margin: 8px 0; }
    .dkp-kb-card { border: 1px solid #e0e0e0; border-radius: 8px; margin-bottom: 10px; overflow: hidden; }
    .dkp-kb-header { display: flex; align-items: center; gap: 10px; padding: 10px 12px; cursor: pointer; background: #fafafa; transition: background 0.15s; }
    .dkp-kb-header:hover { background: #f5f5f5; }
    .dkp-kb-icon { color: #1976d2; font-size: 20px; width: 20px; height: 20px; flex-shrink: 0; }
    .dkp-kb-info { flex: 1; display: flex; flex-direction: column; min-width: 0; }
    .dkp-kb-name { font-size: 14px; font-weight: 500; }
    .dkp-kb-desc { font-size: 12px; color: #555; margin-top: 1px; font-style: italic; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 400px; }
    .dkp-kb-meta { font-size: 12px; color: #666; margin-top: 1px; }
    .dkp-kb-tool { display: flex; align-items: center; gap: 4px; font-size: 12px; color: #888; flex-shrink: 0; }
    .dkp-expand-icon { color: #999; flex-shrink: 0; }
    .dkp-col-editor { padding: 16px; border-top: 1px solid #e0e0e0; background: #fff; }
    .dkp-desc-editor { display: flex; align-items: center; gap: 12px; margin-bottom: 16px; }
    .dkp-desc-editor .dkp-field { flex: 1; margin-bottom: 0; }
    .dkp-type-field { width: 150px; flex: 0 0 auto; margin-bottom: 0; }
    .dkp-desc-save-btn { height: 36px; }
    .dkp-col-hint { font-size: 12px; color: #666; margin: 0 0 12px; }
    .dkp-col-no-schema { font-style: italic; }
    .dkp-col-table-wrap { overflow-x: auto; margin-bottom: 16px; }
    .dkp-col-table { width: 100%; border-collapse: collapse; font-size: 13px; }
    .dkp-col-table th { text-align: left; padding: 6px 8px; font-weight: 500; border-bottom: 2px solid #e0e0e0; color: #555; font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; }
    .dkp-col-table td { padding: 6px 8px; border-bottom: 1px solid #f0f0f0; vertical-align: middle; }
    .dkp-col-name { font-family: monospace; font-size: 12px; color: #333; white-space: nowrap; }
    .dkp-col-type-cell { width: 130px; }
    .dkp-type-input { width: 120px; font-family: monospace; font-size: 12px; text-transform: uppercase; }
    .dkp-inline-input { width: 100%; padding: 4px 6px; border: 1px solid #ddd; border-radius: 4px; font-size: 13px; font-family: inherit; background: #fafafa; box-sizing: border-box; }
    .dkp-inline-input:focus { outline: none; border-color: #1976d2; background: #fff; }
    .dkp-wide { min-width: 200px; }
    .dkp-col-actions { display: flex; justify-content: flex-end; }
  `],
})
export class DomainKbPanel implements OnChanges {
  @Input() domainId!: string;
  @ViewChild('dataFileInput') dataFileInput?: ElementRef<HTMLInputElement>;

  kbs       = signal<DomainKb[]>([]);
  loading   = signal(false);
  showAddForm = signal(false);
  expandedId  = signal<string | null>(null);
  savingId    = signal<string | null>(null);
  savingDescId = signal<string | null>(null);
  savingTypeId = signal<string | null>(null);
  uploadingId = signal<string | null>(null);

  // Add form fields
  newName        = '';
  newDescription = '';
  newToolId      = '';

  // KB targeted by the shared hidden file input
  private pendingUploadKbId: string | null = null;

  // Temporary column definition edits keyed by kb.id + col.name
  private _editDefs = new Map<string, Record<string, KBColumnDefinition>>();

  // Pending column type edits keyed by kb.id -> { colName -> { value } }
  private _editTypes = new Map<string, Record<string, { value: string }>>();

  // Pending description edits keyed by kb.id
  private _editDescs = new Map<string, string>();

  constructor(
    private orchestration: OrchestrationService,
    private snackBar: MatSnackBar,
  ) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['domainId'] && this.domainId) this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.orchestration.getDomainKbs(this.domainId).subscribe({
      next: (kbs) => { this.kbs.set(kbs); this.loading.set(false); },
      error: () => { this.loading.set(false); },
    });
  }

  toggleExpanded(id: string): void {
    this.expandedId.set(this.expandedId() === id ? null : id);
  }

  createKb(): void {
    if (!this.newName.trim()) return;
    this.orchestration.createDomainKb({
      name: this.newName.trim(),
      description: this.newDescription.trim() || undefined,
      domainId: this.domainId,
      toolId: this.newToolId.trim() || undefined,
    }).subscribe({
      next: () => {
        this.snackBar.open('Knowledge base created', 'Dismiss', { duration: 3000 });
        this.newName = ''; this.newDescription = ''; this.newToolId = '';
        this.showAddForm.set(false);
        this.load();
      },
      error: () => this.snackBar.open('Failed to create KB', 'Dismiss', { duration: 3000 }),
    });
  }

  deleteKb(kb: DomainKb, event: Event): void {
    event.stopPropagation();
    if (!confirm(`Delete "${kb.name}"? This removes the KB definition and its data.`)) return;
    this.orchestration.deleteDomainKb(kb.id).subscribe({
      next: () => {
        this.snackBar.open('KB deleted', 'Dismiss', { duration: 3000 });
        this.load();
      },
      error: () => this.snackBar.open('Failed to delete KB', 'Dismiss', { duration: 3000 }),
    });
  }

  onDescriptionInput(kb: DomainKb, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this._editDescs.set(kb.id, value);
  }

  saveDescription(kb: DomainKb): void {
    const desc = this._editDescs.get(kb.id) ?? kb.description ?? '';
    this.savingDescId.set(kb.id);
    this.orchestration.updateKbDescription(kb.id, desc.trim()).subscribe({
      next: (res) => {
        const updated = this.kbs().map(k =>
          k.id === kb.id ? { ...k, description: res.description } : k);
        this.kbs.set(updated);
        this.savingDescId.set(null);
        this._editDescs.delete(kb.id);
        this.snackBar.open('Description saved', 'Dismiss', { duration: 3000 });
      },
      error: () => {
        this.savingDescId.set(null);
        this.snackBar.open('Failed to save description', 'Dismiss', { duration: 3000 });
      },
    });
  }

  /** Persist a KB type change ("dataset" or "document") immediately on selection. */
  onTypeChange(kb: DomainKb, type: string): void {
    if (!type || type === kb.type) return;
    this.savingTypeId.set(kb.id);
    this.orchestration.updateKbType(kb.id, type).subscribe({
      next: (res) => {
        const updated = this.kbs().map(k =>
          k.id === kb.id ? { ...k, type: res.type } : k);
        this.kbs.set(updated);
        this.savingTypeId.set(null);
        this.snackBar.open('Type updated', 'Dismiss', { duration: 3000 });
      },
      error: () => {
        this.savingTypeId.set(null);
        this.snackBar.open('Failed to update type', 'Dismiss', { duration: 3000 });
      },
    });
  }

  /** Open the file picker targeting a specific KB. */
  triggerUpload(kb: DomainKb, event: Event): void {
    event.stopPropagation();
    this.pendingUploadKbId = kb.id;
    this.dataFileInput?.nativeElement.click();
  }

  /** Upload the chosen data file to the pending KB (re-populates its rows + schema). */
  onDataFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    const kbId = this.pendingUploadKbId;
    // Reset so the same file can be re-selected later, and clear the pending target.
    input.value = '';
    this.pendingUploadKbId = null;
    if (!file || !kbId) return;

    const formData = new FormData();
    formData.append('file', file);
    formData.append('domainId', this.domainId);

    this.uploadingId.set(kbId);
    this.orchestration.uploadKbDataFile(kbId, formData).subscribe({
      next: (res) => {
        this.uploadingId.set(null);
        // Drop any cached column edits so the refreshed schema loads cleanly.
        this._editDefs.delete(kbId);
        this._editTypes.delete(kbId);
        const undefinedCount = res.undefinedColumns?.length ?? 0;
        const warn = undefinedCount > 0 ? ` — ${undefinedCount} column(s) need definitions` : '';
        this.snackBar.open(
          `Loaded ${res.rowCount ?? 0} rows, ${res.columnCount} columns${warn}`,
          'Dismiss',
          { duration: 4000 }
        );
        this.load();
      },
      error: (err) => {
        this.uploadingId.set(null);
        const msg = err.error?.error || 'Failed to upload data file';
        this.snackBar.open(msg, 'Dismiss', { duration: 5000 });
      },
    });
  }

  getOrCreateDef(kb: DomainKb, colName: string): KBColumnDefinition {
    const key = kb.id;
    if (!this._editDefs.has(key)) {
      // Deep-copy existing definitions into the edit map
      const existing = kb.columnDefinitions ? { ...kb.columnDefinitions } : {};
      // Ensure all schema columns have an entry
      (kb.schema ?? []).forEach(c => {
        if (!existing[c.name]) existing[c.name] = { label: '', description: '', dataType: c.type };
      });
      this._editDefs.set(key, existing);
    }
    const defs = this._editDefs.get(key)!;
    if (!defs[colName]) defs[colName] = { label: '', description: '', dataType: '' };
    return defs[colName];
  }

  /** Stable holder for an editable column type, seeded from the schema's detected type. */
  getTypeEdit(kb: DomainKb, colName: string, initial: string): { value: string } {
    let m = this._editTypes.get(kb.id);
    if (!m) { m = {}; this._editTypes.set(kb.id, m); }
    if (!m[colName]) m[colName] = { value: initial };
    return m[colName];
  }

  saveColumns(kb: DomainKb): void {
    const defs = this._editDefs.get(kb.id) ?? {};
    // Only send columns that have at least a label or description
    const filtered: Record<string, KBColumnDefinition> = {};
    Object.entries(defs).forEach(([col, def]) => {
      if (def.label.trim() || def.description.trim()) filtered[col] = def;
    });

    // Rebuild the schema with any edited types applied (blank falls back to original).
    const typeEdits = this._editTypes.get(kb.id) ?? {};
    const schema = (kb.schema ?? []).map(c => ({
      name: c.name,
      type: (typeEdits[c.name]?.value ?? c.type).trim() || c.type,
    }));

    this.savingId.set(kb.id);
    this.orchestration.updateKbColumns(kb.id, filtered, schema).subscribe({
      next: (res) => {
        // Update local KB object so changes reflect immediately
        const updated = this.kbs().map(k =>
          k.id === kb.id ? { ...k, columnDefinitions: res.columnDefinitions, schema: res.schema } : k);
        this.kbs.set(updated);
        this._editTypes.delete(kb.id);
        this.savingId.set(null);
        this.snackBar.open('Column definitions saved', 'Dismiss', { duration: 3000 });
      },
      error: () => {
        this.savingId.set(null);
        this.snackBar.open('Failed to save column definitions', 'Dismiss', { duration: 3000 });
      },
    });
  }

  formatDate(iso: string): string {
    const d = new Date(iso);
    const now = new Date();
    const diffMs = now.getTime() - d.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    return d.toLocaleDateString();
  }
}


