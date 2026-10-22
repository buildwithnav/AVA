import { Component, EventEmitter, Input, OnChanges, Output, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import {
  OrchestrationService, DomainDefinition, DomainGrant,
} from '../../services/orchestration';

/**
 * Child-domain management for a domain (e.g. the individual studies under "Clinical Studies"),
 * plus the per-principal access grants that gate them. Children are full DomainDefinition rows
 * (parentId set) edited via the shared domain editor in the parent Domains component — this
 * panel only lists them and lets a study administrator grant access. Grants live in AVA rather
 * than the CDO Gateway because a domain can have dozens of children.
 */
@Component({
  selector: 'app-child-domains-panel',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatButtonModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatSelectModule,
    MatCheckboxModule, MatTooltipModule, MatSnackBarModule,
  ],
  template: `
    <div class="cd-panel">
      <!-- Child domain list -->
      <div class="cd-section">
        <div class="cd-row">
          <button mat-raised-button color="primary" (click)="addChild.emit(parentId)">
            <mat-icon>add</mat-icon> Add Child Domain
          </button>
        </div>

        @if (children().length === 0) {
          <p class="cd-empty">No child domains yet. Add one to enable per-study access control.</p>
        } @else {
          <table class="cd-table">
            <thead>
              <tr><th>Name</th><th>External Ref</th><th>Status</th><th></th></tr>
            </thead>
            <tbody>
              @for (c of children(); track c.id) {
                <tr>
                  <td><a class="cd-link" (click)="editChild.emit(c)">{{ c.name }}</a></td>
                  <td class="cd-mono">{{ c.externalRef || '—' }}</td>
                  <td>{{ c.enabled ? 'Active' : 'Archived' }}</td>
                  <td class="cd-actions">
                    <button mat-icon-button (click)="deleteChild(c)" matTooltip="Delete permanently">
                      <mat-icon>delete_forever</mat-icon>
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>

      <!-- Grants -->
      <div class="cd-section">
        <h4 class="cd-heading">Access Grants</h4>
        <p class="cd-hint">
          A grant lets a user reach a child domain. They still need the parent domain's gateway
          module — grants narrow access, they never widen it. Choose "All child domains" to cover
          every child.
        </p>

        <div class="cd-row cd-add-row">
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="cd-grow">
            <mat-label>Principal (user ID)</mat-label>
            <input matInput [(ngModel)]="grantPrincipal" placeholder="e.g. widemtx">
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="cd-grow">
            <mat-label>Child domain</mat-label>
            <mat-select [(ngModel)]="grantDomainId">
              <mat-option value="">All child domains</mat-option>
              @for (c of children(); track c.id) {
                <mat-option [value]="c.id">{{ c.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <div class="cd-checks">
            <mat-checkbox [(ngModel)]="grantBrowse">Browse</mat-checkbox>
            <mat-checkbox [(ngModel)]="grantUpdate">Update</mat-checkbox>
            <mat-checkbox [(ngModel)]="grantInsert">Insert</mat-checkbox>
            <mat-checkbox [(ngModel)]="grantDelete">Delete</mat-checkbox>
          </div>
          <button mat-raised-button color="primary" (click)="addGrant()">
            <mat-icon>person_add</mat-icon> Grant
          </button>
        </div>

        @if (grants().length === 0) {
          <p class="cd-empty">No grants configured.</p>
        } @else {
          <table class="cd-table">
            <thead>
              <tr><th>Principal</th><th>Child domain</th><th>Access</th><th></th></tr>
            </thead>
            <tbody>
              @for (g of grants(); track g.id) {
                <tr>
                  <td class="cd-mono">{{ g.principalId }}</td>
                  <td class="cd-mono">{{ g.domainId ? childName(g.domainId) : 'All (*)' }}</td>
                  <td>{{ accessLabel(g) }}</td>
                  <td class="cd-actions">
                    <button mat-icon-button (click)="removeGrant(g)" matTooltip="Remove grant">
                      <mat-icon>delete</mat-icon>
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>
    </div>
  `,
  styles: [`
    .cd-panel { display: flex; flex-direction: column; gap: 24px; }
    .cd-section { display: flex; flex-direction: column; gap: 12px; }
    .cd-heading { margin: 0; font-size: 1rem; font-weight: 500; }
    .cd-hint { margin: 0; font-size: 0.8125rem; opacity: 0.7; }
    .cd-row { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
    .cd-grow { flex: 1 1 180px; }
    .cd-checks { display: flex; gap: 12px; align-items: center; }
    .cd-empty { opacity: 0.6; font-size: 0.875rem; margin: 0; }
    .cd-table { width: 100%; border-collapse: collapse; font-size: 0.875rem; }
    .cd-table th {
      text-align: left; padding: 8px; font-weight: 500; opacity: 0.7;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
    }
    .cd-table td {
      padding: 6px 8px;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #eee);
    }
    .cd-mono { font-family: ui-monospace, monospace; font-size: 0.8125rem; }
    .cd-link { cursor: pointer; color: var(--mat-sys-primary, #1976d2); text-decoration: none; }
    .cd-link:hover { text-decoration: underline; }
    .cd-actions { text-align: right; width: 56px; }
  `],
})
export class ChildDomainsPanel implements OnChanges {
  @Input({ required: true }) parentId!: string;
  /** Emits the parent id — the host opens the shared editor pre-seeded to create a child of it. */
  @Output() addChild = new EventEmitter<string>();
  /** Emits the child domain to edit — the host opens the shared editor loaded with it. */
  @Output() editChild = new EventEmitter<DomainDefinition>();

  private svc = inject(OrchestrationService);
  private snackBar = inject(MatSnackBar);

  children = signal<DomainDefinition[]>([]);
  grants = signal<DomainGrant[]>([]);

  grantPrincipal = '';
  grantDomainId = '';
  grantBrowse = true;
  grantUpdate = false;
  grantInsert = false;
  grantDelete = false;

  ngOnChanges(): void {
    if (this.parentId) this.reload();
  }

  private reload(): void {
    this.svc.getChildDomains(this.parentId, true).subscribe({
      next: (list) => this.children.set(list),
      error: () => this.children.set([]),
    });
    this.svc.getGrants(this.parentId).subscribe({
      next: (list) => this.grants.set(list),
      error: () => this.grants.set([]),
    });
  }

  childName(id: string): string {
    return this.children().find(c => c.id === id)?.name ?? id;
  }

  deleteChild(c: DomainDefinition): void {
    if (!confirm(`Permanently delete '${c.name}' and all of its access grants? This cannot be undone.`)) return;
    this.svc.deleteDomain(c.id).subscribe({
      next: () => this.reload(),
      error: (err) => this.snackBar.open(err.error?.error || 'Failed to delete', 'OK', { duration: 5000 }),
    });
  }

  addGrant(): void {
    if (!this.grantPrincipal.trim()) {
      this.snackBar.open('Principal is required', 'OK', { duration: 3000 });
      return;
    }

    this.svc.upsertGrant(this.parentId, {
      parentId: this.parentId,
      domainId: this.grantDomainId || null,
      principalType: 'user',
      principalId: this.grantPrincipal.trim(),
      browse: this.grantBrowse,
      update: this.grantUpdate,
      insert: this.grantInsert,
      delete: this.grantDelete,
    }).subscribe({
      next: () => {
        this.snackBar.open('Grant saved', 'OK', { duration: 3000 });
        this.grantPrincipal = '';
        this.reload();
      },
      error: (err) => this.snackBar.open(err.error?.error || 'Failed to save grant', 'OK', { duration: 5000 }),
    });
  }

  removeGrant(g: DomainGrant): void {
    if (!confirm(`Remove ${g.principalId}'s access to ${g.domainId ? this.childName(g.domainId) : 'all child domains'}?`)) return;
    this.svc.removeGrant(this.parentId, g.id).subscribe({
      next: () => this.reload(),
      error: (err) => this.snackBar.open(err.error?.error || 'Failed to remove grant', 'OK', { duration: 5000 }),
    });
  }

  accessLabel(g: DomainGrant): string {
    const keys: (keyof DomainGrant)[] = ['browse', 'update', 'insert', 'delete'];
    return keys.filter(k => g[k]).map(k => (k as string).charAt(0).toUpperCase() + (k as string).slice(1)).join(', ') || 'None';
  }
}
