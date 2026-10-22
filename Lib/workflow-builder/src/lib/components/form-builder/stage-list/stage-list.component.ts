import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CdkDragDrop, DragDropModule, moveItemInArray } from '@angular/cdk/drag-drop';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';
import { WorkflowBuilderService } from '../../../services/workflow-builder.service';
import { StageType, WorkflowStage, ForEachStage } from '../../../models';
import { getStageMeta, isStageTypeHidden } from '../../../shared/stage-meta';
import { StageConfigComponent } from '../stage-config/stage-config.component';

/** All stage types allowed at depth-0 and depth-1 (foreach is allowed, enabling a depth-2 foreach). */
const ALL_STAGE_TYPES: StageType[] = ([
  'ai-extract', 'tool', 'ai-generate', 'ai-analyze',
  'user-select', 'confirmation', 'foreach', 'agentic',
] as StageType[]).filter(t => !isStageTypeHidden(t));

/** Stage types allowed at depth-2 — foreach excluded to prevent a 3rd nesting level. */
const DEPTH2_STAGE_TYPES: StageType[] = ALL_STAGE_TYPES.filter(t => t !== 'foreach');

/**
 * Drag-reorderable list of workflow stages with add/delete/duplicate controls.
 * Each stage can be expanded to show its type-specific configuration form.
 * ForEach stages display their depth-1 sub-stages inline with full editing.
 * ForEach sub-stages that are themselves ForEach expose depth-2 stages with full editing.
 * Depth-2 add menus exclude 'foreach' to enforce a maximum of 2 nesting levels.
 */
@Component({
  selector: 'wfb-stage-list',
  standalone: true,
  imports: [
    CommonModule, DragDropModule,
    MatCardModule, MatButtonModule, MatIconModule,
    MatMenuModule, MatChipsModule, MatTooltipModule,
    StageConfigComponent,
  ],
  template: `
    <div class="wfb-stage-list">
      <div class="wfb-stage-header">
        <h3>Workflow Stages</h3>
        <button mat-mini-fab color="primary" [matMenuTriggerFor]="addMenu"
                matTooltip="Add stage">
          <mat-icon>add</mat-icon>
        </button>
        <mat-menu #addMenu="matMenu">
          @for (type of allStageTypes; track type) {
            <button mat-menu-item (click)="addStage(type)">
              <mat-icon [style.color]="getMeta(type).color">{{ getMeta(type).icon }}</mat-icon>
              <span>{{ getMeta(type).label }}</span>
            </button>
          }
        </mat-menu>
      </div>

      @if (builder.stages().length === 0) {
        <div class="wfb-empty-stages">
          <mat-icon>playlist_add</mat-icon>
          <p>No stages yet. Click + to add the first stage.</p>
        </div>
      }

      <!-- Top-level drag list -->
      <div cdkDropList (cdkDropListDropped)="onTopLevelDrop($event)" class="wfb-stages-drop">
        @for (stage of builder.stages(); track $index; let i = $index) {
          <mat-card cdkDrag class="wfb-stage-card"
                    [class.wfb-selected]="builder.selectedStageIndex() === i">
            <mat-card-header cdkDragHandle (click)="builder.selectStage(i)">
              <mat-icon [style.color]="getMeta(stage.type).color" class="wfb-stage-icon">
                {{ getMeta(stage.type).icon }}
              </mat-icon>
              <div class="wfb-stage-title">
                <strong>{{ stage.stageId }}</strong>
                <mat-chip class="wfb-stage-type-chip" [style.background]="getMeta(stage.type).color">
                  {{ getMeta(stage.type).label }}
                </mat-chip>
              </div>
              <span class="wfb-spacer"></span>
              <button mat-icon-button matTooltip="Duplicate"
                      (click)="duplicate(i); $event.stopPropagation()">
                <mat-icon>content_copy</mat-icon>
              </button>
              <button mat-icon-button matTooltip="Delete" color="warn"
                      (click)="remove(i); $event.stopPropagation()">
                <mat-icon>delete</mat-icon>
              </button>
            </mat-card-header>

            @if (builder.selectedStageIndex() === i) {
              <mat-card-content>
                <wfb-stage-config [stage]="stage" [index]="i" />
              </mat-card-content>
            }

            <!-- Depth-1 sub-stages for ForEach -->
            @if (stage.type === 'foreach') {
              <div class="wfb-substages">
                <div class="wfb-substages-header">
                  <mat-icon class="wfb-substages-icon">subdirectory_arrow_right</mat-icon>
                  <span class="wfb-substages-label">
                    Sub-stages ({{ asForEach(stage).stages.length }})
                  </span>
                  <span class="wfb-spacer"></span>
                  <button mat-icon-button [matMenuTriggerFor]="subAddMenu" matTooltip="Add sub-stage"
                          (click)="$event.stopPropagation()">
                    <mat-icon>add</mat-icon>
                  </button>
                  <mat-menu #subAddMenu="matMenu">
                    @for (type of allStageTypes; track type) {
                      <button mat-menu-item (click)="addSubStage(i, type)">
                        <mat-icon [style.color]="getMeta(type).color">{{ getMeta(type).icon }}</mat-icon>
                        <span>{{ getMeta(type).label }}</span>
                      </button>
                    }
                  </mat-menu>
                </div>

                @if (asForEach(stage).stages.length === 0) {
                  <div class="wfb-substages-empty">
                    <mat-icon>info</mat-icon>
                    <span>No sub-stages yet.</span>
                    <button mat-button color="primary" [matMenuTriggerFor]="emptySubMenu"
                            (click)="$event.stopPropagation()">
                      <mat-icon>add</mat-icon> Add sub-stage
                    </button>
                    <mat-menu #emptySubMenu="matMenu">
                      @for (type of allStageTypes; track type) {
                        <button mat-menu-item (click)="addSubStage(i, type)">
                          <mat-icon [style.color]="getMeta(type).color">{{ getMeta(type).icon }}</mat-icon>
                          <span>{{ getMeta(type).label }}</span>
                        </button>
                      }
                    </mat-menu>
                  </div>
                }

                <!-- Depth-1 sub-stage drag list (isolated — not connected to top-level list) -->
                <div cdkDropList
                     (cdkDropListDropped)="onSubStageDrop($event, i)"
                     class="wfb-substages-drop">
                  @for (sub of asForEach(stage).stages; track $index; let j = $index) {
                    <mat-card cdkDrag class="wfb-substage-card"
                              [class.wfb-selected]="isSubSelected(i, j)">
                      <mat-card-header cdkDragHandle
                                       (click)="selectSubStage(i, j); $event.stopPropagation()">
                        <mat-icon [style.color]="getMeta(sub.type).color" class="wfb-stage-icon">
                          {{ getMeta(sub.type).icon }}
                        </mat-icon>
                        <div class="wfb-stage-title">
                          <strong>{{ sub.stageId }}</strong>
                          <mat-chip class="wfb-stage-type-chip wfb-substage-chip"
                                    [style.background]="getMeta(sub.type).color">
                            {{ getMeta(sub.type).label }}
                          </mat-chip>
                        </div>
                        <span class="wfb-spacer"></span>
                        <button mat-icon-button matTooltip="Duplicate" size="small"
                                (click)="duplicateSubStage(i, j); $event.stopPropagation()">
                          <mat-icon>content_copy</mat-icon>
                        </button>
                        <button mat-icon-button matTooltip="Delete" color="warn" size="small"
                                (click)="removeSubStage(i, j); $event.stopPropagation()">
                          <mat-icon>delete</mat-icon>
                        </button>
                      </mat-card-header>

                      @if (isSubSelected(i, j)) {
                        <mat-card-content>
                          <wfb-stage-config [stage]="sub" [index]="j" [parentIndex]="i" />
                        </mat-card-content>
                      }

                      <!-- Depth-2: foreach within depth-1 sub-stage -->
                      @if (sub.type === 'foreach') {
                        <div class="wfb-substages wfb-substages-deep">
                          <div class="wfb-substages-header">
                            <mat-icon class="wfb-substages-icon">subdirectory_arrow_right</mat-icon>
                            <span class="wfb-substages-label">
                              Nested ({{ asForEach(sub).stages.length }})
                            </span>
                            <span class="wfb-spacer"></span>
                            <button mat-icon-button [matMenuTriggerFor]="deepAddMenu"
                                    matTooltip="Add nested stage"
                                    (click)="$event.stopPropagation()">
                              <mat-icon>add</mat-icon>
                            </button>
                            <!-- depth-2 add menu excludes foreach -->
                            <mat-menu #deepAddMenu="matMenu">
                              @for (type of depth2StageTypes; track type) {
                                <button mat-menu-item (click)="addDeepStage(i, j, type)">
                                  <mat-icon [style.color]="getMeta(type).color">{{ getMeta(type).icon }}</mat-icon>
                                  <span>{{ getMeta(type).label }}</span>
                                </button>
                              }
                            </mat-menu>
                          </div>

                          @if (asForEach(sub).stages.length === 0) {
                            <div class="wfb-substages-empty">
                              <mat-icon>info</mat-icon>
                              <span>No nested stages yet.</span>
                              <button mat-button color="primary" [matMenuTriggerFor]="emptyDeepMenu"
                                      (click)="$event.stopPropagation()">
                                <mat-icon>add</mat-icon> Add nested stage
                              </button>
                              <mat-menu #emptyDeepMenu="matMenu">
                                @for (type of depth2StageTypes; track type) {
                                  <button mat-menu-item (click)="addDeepStage(i, j, type)">
                                    <mat-icon [style.color]="getMeta(type).color">{{ getMeta(type).icon }}</mat-icon>
                                    <span>{{ getMeta(type).label }}</span>
                                  </button>
                                }
                              </mat-menu>
                            </div>
                          }

                          <!-- Depth-2 drag list (isolated) -->
                          <div cdkDropList
                               (cdkDropListDropped)="onDeepStageDrop($event, i, j)"
                               class="wfb-substages-drop">
                            @for (deep of asForEach(sub).stages; track $index; let k = $index) {
                              <mat-card cdkDrag class="wfb-substage-card wfb-deep-card"
                                        [class.wfb-selected]="isDeepSelected(i, j, k)">
                                <mat-card-header cdkDragHandle
                                                 (click)="selectDeepStage(i, j, k); $event.stopPropagation()">
                                  <mat-icon [style.color]="getMeta(deep.type).color" class="wfb-stage-icon">
                                    {{ getMeta(deep.type).icon }}
                                  </mat-icon>
                                  <div class="wfb-stage-title">
                                    <strong>{{ deep.stageId }}</strong>
                                    <mat-chip class="wfb-stage-type-chip wfb-substage-chip"
                                              [style.background]="getMeta(deep.type).color">
                                      {{ getMeta(deep.type).label }}
                                    </mat-chip>
                                  </div>
                                  <span class="wfb-spacer"></span>
                                  <button mat-icon-button matTooltip="Duplicate" size="small"
                                          (click)="duplicateDeepStage(i, j, k); $event.stopPropagation()">
                                    <mat-icon>content_copy</mat-icon>
                                  </button>
                                  <button mat-icon-button matTooltip="Delete" color="warn" size="small"
                                          (click)="removeDeepStage(i, j, k); $event.stopPropagation()">
                                    <mat-icon>delete</mat-icon>
                                  </button>
                                </mat-card-header>

                                @if (isDeepSelected(i, j, k)) {
                                  <mat-card-content>
                                    <wfb-stage-config [stage]="deep" [index]="k"
                                                      [parentIndex]="j"
                                                      [grandparentIndex]="i" />
                                  </mat-card-content>
                                }
                              </mat-card>
                            }
                          </div>
                        </div>
                      }
                    </mat-card>
                  }
                </div>
              </div>
            }
          </mat-card>
        }
      </div>
    </div>
  `,
  styles: [`
    .wfb-stage-list { padding: 16px; }
    .wfb-stage-header {
      display: flex; align-items: center; gap: 12px; margin-bottom: 12px;
      h3 { margin: 0; flex: 1; }
    }
    .wfb-empty-stages {
      text-align: center; padding: 32px; opacity: 0.6;
      mat-icon { font-size: 48px; width: 48px; height: 48px; }
    }
    .wfb-stage-card {
      margin-bottom: 8px;
      transition: box-shadow 0.2s;
      &.wfb-selected { box-shadow: 0 0 0 2px var(--mat-sys-primary, #4285f4); }
    }
    mat-card-header {
      display: flex; align-items: center; gap: 8px;
      min-height: 48px; padding: 12px 16px;
      cursor: pointer;
    }
    .wfb-stage-icon { margin-right: 4px; display: flex; align-items: center; }
    .wfb-stage-title { display: flex; align-items: center; gap: 8px; }
    .wfb-spacer { flex: 1; }
    .cdk-drag-preview {
      box-shadow: 0 4px 16px rgba(0,0,0,0.2);
      border-radius: 8px;
    }
    .cdk-drag-placeholder { opacity: 0.3; }
    .wfb-stages-drop { min-height: 4px; }
    .wfb-stage-type-chip {
      --mat-chip-label-text-color: white !important;
      --mdc-chip-label-text-color: white !important;
      font-size: 11px;
      color: white !important;
    }
    :host ::ng-deep .wfb-stage-type-chip .mdc-evolution-chip__text-label {
      color: white !important;
    }
    /* Sub-stage nesting styles */
    .wfb-substages {
      margin: 0 16px 12px 32px;
      padding: 8px 0;
      border-left: 3px solid #4e342e;
      padding-left: 12px;
    }
    .wfb-substages-deep {
      margin-left: 16px;
      border-left-color: #795548;
    }
    .wfb-substages-header {
      display: flex; align-items: center; gap: 4px;
      margin-bottom: 8px; padding: 0 4px;
      font-size: 12px; color: rgba(0,0,0,0.6);
    }
    .wfb-substages-icon { font-size: 18px; width: 18px; height: 18px; }
    .wfb-substages-label { font-weight: 500; }
    .wfb-substages-drop { min-height: 4px; }
    .wfb-substage-card {
      margin-bottom: 6px;
      transition: box-shadow 0.2s;
      &.wfb-selected { box-shadow: 0 0 0 2px var(--mat-sys-primary, #4285f4); }
    }
    .wfb-substage-card mat-card-header {
      min-height: 40px; padding: 8px 12px;
    }
    .wfb-substage-chip { font-size: 10px; }
    .wfb-deep-card { background: rgba(0,0,0,0.015); }
    .wfb-substages-empty {
      display: flex; align-items: center; gap: 8px;
      padding: 8px 4px; font-size: 13px;
      color: rgba(0,0,0,0.5);
    }
  `],
})
export class StageListComponent {
  readonly builder = inject(WorkflowBuilderService);

  readonly allStageTypes = ALL_STAGE_TYPES;
  readonly depth2StageTypes = DEPTH2_STAGE_TYPES;

  getMeta(type: StageType) { return getStageMeta(type); }

  asForEach(stage: WorkflowStage): ForEachStage {
    return stage as ForEachStage;
  }

  // ── Top-level operations ──────────────────────────────────────

  addStage(type: StageType): void {
    this.builder.addStage(type);
  }

  remove(index: number): void {
    this.builder.removeStage(index);
  }

  duplicate(index: number): void {
    this.builder.duplicateStage(index);
  }

  onTopLevelDrop(event: CdkDragDrop<WorkflowStage[]>): void {
    this.builder.moveStage(event.previousIndex, event.currentIndex);
  }

  // ── Depth-1 sub-stage operations ─────────────────────────────

  isSubSelected(parent: number, child: number): boolean {
    const sel = this.builder.selectedSubStage();
    return sel !== null && sel.parent === parent && sel.child === child;
  }

  selectSubStage(parent: number, child: number): void {
    const current = this.builder.selectedSubStage();
    if (current?.parent === parent && current?.child === child) {
      this.builder.selectedSubStage.set(null);
    } else {
      this.builder.selectedSubStage.set({ parent, child });
    }
  }

  addSubStage(parentIndex: number, type: StageType): void {
    this.builder.addSubStage(parentIndex, type);
  }

  removeSubStage(parentIndex: number, childIndex: number): void {
    this.builder.removeSubStage(parentIndex, childIndex);
  }

  duplicateSubStage(parentIndex: number, childIndex: number): void {
    this.builder.duplicateSubStage(parentIndex, childIndex);
  }

  onSubStageDrop(event: CdkDragDrop<WorkflowStage[]>, parentIndex: number): void {
    this.builder.moveSubStage(parentIndex, event.previousIndex, event.currentIndex);
  }

  // ── Depth-2 operations ───────────────────────────────────────

  isDeepSelected(parent: number, sub: number, child: number): boolean {
    const sel = this.builder.selectedDeepStage();
    return sel !== null && sel.parent === parent && sel.sub === sub && sel.child === child;
  }

  selectDeepStage(parent: number, sub: number, child: number): void {
    const current = this.builder.selectedDeepStage();
    if (current?.parent === parent && current?.sub === sub && current?.child === child) {
      this.builder.selectedDeepStage.set(null);
    } else {
      this.builder.selectedDeepStage.set({ parent, sub, child });
    }
  }

  addDeepStage(parentIndex: number, subIndex: number, type: StageType): void {
    this.builder.addDeepStage(parentIndex, subIndex, type);
  }

  removeDeepStage(parentIndex: number, subIndex: number, childIndex: number): void {
    this.builder.removeDeepStage(parentIndex, subIndex, childIndex);
  }

  duplicateDeepStage(parentIndex: number, subIndex: number, childIndex: number): void {
    this.builder.duplicateDeepStage(parentIndex, subIndex, childIndex);
  }

  onDeepStageDrop(event: CdkDragDrop<WorkflowStage[]>, parentIndex: number, subIndex: number): void {
    this.builder.moveDeepStage(parentIndex, subIndex, event.previousIndex, event.currentIndex);
  }
}
