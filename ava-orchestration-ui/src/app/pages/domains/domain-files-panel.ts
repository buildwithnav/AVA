import { Component, Input, OnChanges, SimpleChanges, signal, ViewChild, ElementRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { OrchestrationService, DomainFile } from '../../services/orchestration';

/**
 * Embeddable panel for managing domain files (upload, list, edit metadata, replace, delete).
 * Takes a domainId input and handles its own data loading.
 */
@Component({
  selector: 'app-domain-files-panel',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatCardModule, MatFormFieldModule, MatInputModule,
    MatButtonModule, MatIconModule,
    MatProgressBarModule, MatSnackBarModule, MatTooltipModule,
    MatMenuModule,
  ],
  template: `
    <div class="dfp-container">
      <!-- Upload zone -->
      <div class="dfp-upload-zone"
           [class.drag-over]="dragOver()"
           (dragover)="onDragOver($event)"
           (dragleave)="onDragLeave($event)"
           (drop)="onDrop($event)">
        <div class="dfp-upload-content">
          <mat-icon class="dfp-upload-icon">cloud_upload</mat-icon>
          <p class="dfp-upload-text">Drag & drop files here</p>
          <p class="dfp-upload-subtext">or</p>
          <button mat-raised-button color="primary" (click)="fileInput.click()">
            <mat-icon>folder_open</mat-icon> Browse Files
          </button>
          <input #fileInput type="file" hidden
                 accept=".txt,.md,.csv,.json,.xlsx,.pdf,.docx"
                 (change)="onFileSelected($event)">
        </div>
        @if (uploading()) {
          <mat-progress-bar mode="indeterminate" class="dfp-upload-progress"></mat-progress-bar>
        }
      </div>

      <!-- File list -->
      @if (files().length > 0) {
        <h4 class="dfp-section-title">Files ({{ files().length }})</h4>
        <div class="dfp-file-list">
          @for (file of files(); track file.id) {
            <div class="dfp-file-row">
              <div class="dfp-file-main">
                @if (editingFile?.id === file.id) {
                  <div class="dfp-edit-fields">
                    <mat-form-field appearance="outline" subscriptSizing="dynamic" class="dfp-edit-field">
                      <mat-label>Title</mat-label>
                      <input matInput [(ngModel)]="editTitle">
                    </mat-form-field>
                    <mat-form-field appearance="outline" subscriptSizing="dynamic" class="dfp-edit-field">
                      <mat-label>Description</mat-label>
                      <input matInput [(ngModel)]="editDescription">
                    </mat-form-field>
                    <mat-form-field appearance="outline" subscriptSizing="dynamic" class="dfp-edit-field">
                      <mat-label>Tags</mat-label>
                      <input matInput [(ngModel)]="editTags" placeholder="comma-separated">
                    </mat-form-field>
                  </div>
                } @else {
                  <div class="dfp-file-primary">
                    <strong class="dfp-file-title">{{ file.title }}</strong>
                    @if (file.description) {
                      <span class="dfp-desc">— {{ file.description }}</span>
                    }
                  </div>
                }
                <div class="dfp-file-details">
                  <span class="dfp-detail-filename">{{ file.filename }}</span>
                  <span class="dfp-detail-right">
                    <span class="dfp-detail-item">{{ formatSize(file.fileSizeBytes) }}</span>
                    <span class="dfp-detail-sep">·</span>
                    <span class="dfp-detail-item">{{ formatDate(file.uploadedAt) }}</span>
                  </span>
                </div>
                <div class="dfp-file-chips">
                  <span class="dfp-type-badge">{{ file.contentType }}</span>
                  @for (tag of file.tags; track tag) {
                    <span class="dfp-tag">{{ tag }}</span>
                  }
                </div>
              </div>

              <div class="dfp-file-actions">
                @if (editingFile?.id === file.id) {
                  <div class="dfp-actions-row">
                    <button mat-icon-button color="primary" (click)="saveEdit()" matTooltip="Save">
                      <mat-icon>check</mat-icon>
                    </button>
                    <button mat-icon-button (click)="cancelEdit()" matTooltip="Cancel">
                      <mat-icon>close</mat-icon>
                    </button>
                  </div>
                } @else if (replacingFileId === file.id) {
                  <mat-progress-bar mode="indeterminate" class="dfp-row-progress"></mat-progress-bar>
                } @else {
                  <button mat-icon-button [matMenuTriggerFor]="fileMenu" matTooltip="Actions">
                    <mat-icon>more_vert</mat-icon>
                  </button>
                  <mat-menu #fileMenu="matMenu">
                    <button mat-menu-item (click)="startEdit(file)">
                      <mat-icon>edit</mat-icon>
                      <span>Edit metadata</span>
                    </button>
                    <button mat-menu-item (click)="startReplace(file)">
                      <mat-icon>swap_horiz</mat-icon>
                      <span>Replace file</span>
                    </button>
                    <button mat-menu-item (click)="deleteFile(file)">
                      <mat-icon color="warn">delete</mat-icon>
                      <span>Delete</span>
                    </button>
                  </mat-menu>
                }
              </div>
            </div>
          }
        </div>
      } @else {
        <p class="dfp-empty">No files uploaded yet. Upload specs, READMEs, or wiki entries to enrich AVA's responses.</p>
      }

      <!-- Hidden file input for replace -->
      <input #replaceFileInput type="file" hidden
             accept=".txt,.md,.csv,.json,.xlsx,.pdf,.docx"
             (change)="onReplaceFileSelected($event)">
    </div>
  `,
  styles: [`
    .dfp-container { display: flex; flex-direction: column; gap: 16px; }
    .dfp-upload-zone {
      border: 2px dashed #ccc; border-radius: 12px; text-align: center;
      padding: 32px 24px; transition: all 0.2s ease; cursor: pointer;
      &.drag-over { border-color: #1976d2; background: rgba(25, 118, 210, 0.05); }
    }
    .dfp-upload-content { display: flex; flex-direction: column; align-items: center; gap: 8px; }
    .dfp-upload-icon { font-size: 36px; width: 36px; height: 36px; color: #666; }
    .dfp-upload-text { font-size: 14px; color: #444; margin: 0; }
    .dfp-upload-subtext { color: #888; margin: 0; font-size: 12px; }
    .dfp-upload-progress { margin-top: 12px; }
    .dfp-section-title { margin: 8px 0 4px; font-size: 14px; font-weight: 500; }

    .dfp-file-list {
      display: flex; flex-direction: column;
      border: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      border-radius: 8px;
      overflow: hidden;
    }
    .dfp-file-row {
      display: flex; align-items: center;
      padding: 12px 16px;
      gap: 12px;
      min-height: 72px;
      border-bottom: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      &:last-child { border-bottom: none; }
    }
    .dfp-file-main {
      flex: 1; min-width: 0;
      display: flex; flex-direction: column; gap: 4px;
    }
    .dfp-file-primary {
      display: flex; align-items: baseline; gap: 8px;
      overflow: hidden; white-space: nowrap; text-overflow: ellipsis;
    }
    .dfp-file-title {
      font-size: 14px; flex-shrink: 0;
    }
    .dfp-desc { color: #666; font-size: 12px; flex-shrink: 1; overflow: hidden; text-overflow: ellipsis; }
    .dfp-file-details {
      display: flex; align-items: center; gap: 6px;
      font-size: 12px; color: #666;
      overflow: hidden; white-space: nowrap;
    }
    .dfp-detail-filename {
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
      flex: 1; min-width: 0;
    }
    .dfp-detail-right {
      display: flex; align-items: center; gap: 6px;
      flex-shrink: 0; white-space: nowrap;
    }
    .dfp-detail-item { white-space: nowrap; }
    .dfp-detail-sep { color: #bbb; }
    .dfp-file-chips {
      display: flex; align-items: center; gap: 4px;
      overflow: hidden; white-space: nowrap;
    }
    .dfp-type-badge {
      background: #e3f2fd; color: #1565c0;
      padding: 1px 6px; border-radius: 4px; font-size: 11px;
    }
    .dfp-tag {
      background: #f3e5f5; color: #6a1b9a;
      padding: 2px 8px; border-radius: 12px; font-size: 11px;
      display: inline-block;
    }
    .dfp-file-actions {
      flex-shrink: 0; width: 48px;
      display: flex; align-items: center; justify-content: center;
    }

    .dfp-edit-fields {
      display: flex; flex-direction: column; gap: 12px;
      padding: 12px 0; width: 100%;
    }
    .dfp-edit-field {
      width: 100%;
    }
    .dfp-actions-row {
      display: flex; flex-direction: row; align-items: center;
      gap: 0;
    }
    .dfp-row-progress {
      width: 40px;
    }
    .dfp-empty { text-align: center; opacity: 0.6; font-size: 13px; padding: 16px; }
  `],
})
export class DomainFilesPanel implements OnChanges {
  @Input() domainId!: string;
  @ViewChild('replaceFileInput') replaceFileInput!: ElementRef<HTMLInputElement>;

  files = signal<DomainFile[]>([]);
  uploading = signal(false);
  dragOver = signal(false);

  editingFile: DomainFile | null = null;
  editTitle = '';
  editDescription = '';
  editTags = '';

  replacingFileId: string | null = null;
  private replaceTarget: DomainFile | null = null;

  constructor(
    private orchestration: OrchestrationService,
    private snackBar: MatSnackBar,
  ) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['domainId'] && this.domainId) {
      this.loadFiles();
    }
  }

  loadFiles(): void {
    this.orchestration.getDomainFiles(this.domainId).subscribe({
      next: (files: DomainFile[]) => this.files.set(files),
      error: () => this.snackBar.open('Failed to load files', 'Dismiss', { duration: 3000 }),
    });
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.dragOver.set(true);
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.dragOver.set(false);
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.dragOver.set(false);
    const files = event.dataTransfer?.files;
    if (files && files.length > 0) {
      this.uploadFile(files[0]);
    }
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.uploadFile(input.files[0]);
      input.value = '';
    }
  }

  uploadFile(file: File): void {
    this.uploading.set(true);
    const formData = new FormData();
    formData.append('file', file);
    formData.append('domainId', this.domainId);
    formData.append('title', file.name.replace(/\.[^/.]+$/, ''));
    formData.append('description', '');
    formData.append('tags', '');
    formData.append('uploadedBy', 'admin');

    this.orchestration.uploadDomainFile(formData).subscribe({
      next: (result: DomainFile) => {
        this.uploading.set(false);
        this.snackBar.open(`Uploaded "${result.title}" (${result.chunkCount} chunks)`, 'OK', { duration: 3000 });
        this.loadFiles();
      },
      error: (err: any) => {
        this.uploading.set(false);
        this.snackBar.open(`Upload failed: ${err.error?.message || err.message}`, 'Dismiss', { duration: 5000 });
      },
    });
  }

  // ── Edit metadata ──────────────────────────────────────────────────────────

  startEdit(file: DomainFile): void {
    this.editingFile = file;
    this.editTitle = file.title;
    this.editDescription = file.description;
    this.editTags = file.tags.join(', ');
  }

  cancelEdit(): void {
    this.editingFile = null;
  }

  saveEdit(): void {
    if (!this.editingFile) return;
    const tags = this.editTags.split(',').map(t => t.trim()).filter(t => t);

    this.orchestration.updateDomainFileMetadata(this.editingFile.id, {
      title: this.editTitle,
      description: this.editDescription,
      tags,
    }).subscribe({
      next: () => {
        this.snackBar.open('Metadata updated', 'OK', { duration: 2000 });
        this.editingFile = null;
        this.loadFiles();
      },
      error: () => this.snackBar.open('Update failed', 'Dismiss', { duration: 3000 }),
    });
  }

  // ── Replace file ───────────────────────────────────────────────────────────

  startReplace(file: DomainFile): void {
    this.replaceTarget = file;
    this.replaceFileInput.nativeElement.value = '';
    this.replaceFileInput.nativeElement.click();
  }

  onReplaceFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (!input.files || input.files.length === 0 || !this.replaceTarget) return;

    const newFile = input.files[0];
    const target = this.replaceTarget;
    this.replaceTarget = null;
    this.replacingFileId = target.id;

    // Delete old file then upload new one with same metadata
    this.orchestration.deleteDomainFile(target.id).subscribe({
      next: () => {
        const formData = new FormData();
        formData.append('file', newFile);
        formData.append('domainId', this.domainId);
        formData.append('title', target.title);
        formData.append('description', target.description);
        formData.append('tags', target.tags.join(','));
        formData.append('uploadedBy', 'admin');

        this.orchestration.uploadDomainFile(formData).subscribe({
          next: (result: DomainFile) => {
            this.replacingFileId = null;
            this.snackBar.open(`Replaced "${result.title}" (${result.chunkCount} chunks)`, 'OK', { duration: 3000 });
            this.loadFiles();
          },
          error: (err: any) => {
            this.replacingFileId = null;
            this.snackBar.open(`Replace failed: ${err.error?.message || err.message}`, 'Dismiss', { duration: 5000 });
            this.loadFiles();
          },
        });
      },
      error: () => {
        this.replacingFileId = null;
        this.snackBar.open('Failed to remove old file during replace', 'Dismiss', { duration: 3000 });
      },
    });

    input.value = '';
  }

  // ── Delete file ────────────────────────────────────────────────────────────

  deleteFile(file: DomainFile): void {
    if (!confirm(`Delete "${file.title}"? This will remove the file and its indexed content.`)) return;

    this.orchestration.deleteDomainFile(file.id).subscribe({
      next: () => {
        this.snackBar.open(`Deleted "${file.title}"`, 'OK', { duration: 2000 });
        this.loadFiles();
      },
      error: () => this.snackBar.open('Delete failed', 'Dismiss', { duration: 3000 }),
    });
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  formatDate(date: string): string {
    if (!date) return '';
    return new Date(date).toLocaleDateString();
  }
}
