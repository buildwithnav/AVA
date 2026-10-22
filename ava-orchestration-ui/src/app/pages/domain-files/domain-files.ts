import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatTableModule } from '@angular/material/table';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBarModule, MatSnackBar } from '@angular/material/snack-bar';
import { MatDialogModule } from '@angular/material/dialog';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSelectModule } from '@angular/material/select';
import { OrchestrationService, DomainFile, DomainDefinition } from '../../services/orchestration';

@Component({
  selector: 'app-domain-files',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatTableModule,
    MatProgressBarModule,
    MatSnackBarModule,
    MatDialogModule,
    MatTooltipModule,
    MatSelectModule,
  ],
  templateUrl: './domain-files.html',
  styleUrls: ['./domain-files.scss'],
})
export class DomainFiles implements OnInit {
  files = signal<DomainFile[]>([]);
  domains = signal<DomainDefinition[]>([]);
  domainId = signal('');
  uploading = signal(false);
  dragOver = signal(false);

  uploadTitle = '';
  uploadDescription = '';
  uploadTags = '';

  editingFile: DomainFile | null = null;
  editTitle = '';
  editDescription = '';
  editTags = '';

  displayedColumns = ['title', 'filename', 'contentType', 'tags', 'chunkCount', 'size', 'uploadedAt', 'actions'];

  constructor(
    private orchestration: OrchestrationService,
    private snackBar: MatSnackBar,
  ) {}

  ngOnInit(): void {
    this.loadDomains();
  }

  loadDomains(): void {
    this.orchestration.getDomains().subscribe({
      next: (domains) => this.domains.set(domains.filter(d => d.enabled)),
      error: () => this.snackBar.open('Failed to load domains', 'Dismiss', { duration: 3000 }),
    });
  }

  loadFiles(): void {
    if (!this.domainId()) {
      return;
    }

    this.orchestration.getDomainFiles(this.domainId()).subscribe({
      next: files => this.files.set(files),
      error: () => this.snackBar.open('Failed to load files', 'Dismiss', { duration: 3000 }),
    });
  }

  setDomain(domainId: string): void {
    this.domainId.set(domainId);
    if (domainId) {
      this.loadFiles();
    } else {
      this.files.set([]);
    }
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
    if (!this.domainId()) {
      this.snackBar.open('Please select a domain first', 'Dismiss', { duration: 3000 });
      return;
    }

    this.uploading.set(true);
    const formData = new FormData();
    formData.append('file', file);
    formData.append('domainId', this.domainId());
    formData.append('title', this.uploadTitle || file.name.replace(/\.[^/.]+$/, ''));
    formData.append('description', this.uploadDescription);
    formData.append('tags', this.uploadTags);
    formData.append('uploadedBy', 'admin');

    this.orchestration.uploadDomainFile(formData).subscribe({
      next: result => {
        this.uploading.set(false);
        this.uploadTitle = '';
        this.uploadDescription = '';
        this.uploadTags = '';
        this.snackBar.open(`Uploaded "${result.title}" (${result.chunkCount} chunks)`, 'OK', {
          duration: 3000,
        });
        this.loadFiles();
      },
      error: err => {
        this.uploading.set(false);
        this.snackBar.open(`Upload failed: ${err.error?.message || err.message}`, 'Dismiss', {
          duration: 5000,
        });
      },
    });
  }

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
    if (!this.editingFile) {
      return;
    }

    const tags = this.editTags
      .split(',')
      .map(tag => tag.trim())
      .filter(tag => tag);

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

  deleteFile(file: DomainFile): void {
    if (!confirm(`Delete "${file.title}"? This will remove the file and its indexed content.`)) {
      return;
    }

    this.orchestration.deleteDomainFile(file.id).subscribe({
      next: () => {
        this.snackBar.open(`Deleted "${file.title}"`, 'OK', { duration: 2000 });
        this.loadFiles();
      },
      error: () => this.snackBar.open('Delete failed', 'Dismiss', { duration: 3000 }),
    });
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) {
      return `${bytes} B`;
    }
    if (bytes < 1024 * 1024) {
      return `${(bytes / 1024).toFixed(1)} KB`;
    }
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  formatDate(date: string): string {
    return new Date(date).toLocaleDateString();
  }
}
