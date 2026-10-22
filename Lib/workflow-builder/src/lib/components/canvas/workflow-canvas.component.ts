import { Component, inject, signal, computed, ElementRef, viewChild, AfterViewInit, OnDestroy, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { WorkflowBuilderService } from '../../services/workflow-builder.service';
import { WorkflowStage, StageType, ToolStage, ForEachStage } from '../../models';

/** Color + icon map for stage types */
const STAGE_THEME: Record<StageType, { color: string; icon: string; label: string }> = {
  'ai-extract':   { color: '#1565c0', icon: 'psychology',        label: 'Extract' },
  'tool':         { color: '#2e7d32', icon: 'build',             label: 'Tool' },
  'ai-generate':  { color: '#6a1b9a', icon: 'auto_awesome',     label: 'Generate' },
  'ai-analyze':   { color: '#e65100', icon: 'analytics',         label: 'Analyze' },
  'user-select':  { color: '#00838f', icon: 'touch_app',         label: 'Select' },
  'confirmation': { color: '#ad1457', icon: 'help_outline',      label: 'Confirm' },
  'foreach':      { color: '#4e342e', icon: 'loop',              label: 'ForEach' },
  'agentic':      { color: '#00695c', icon: 'smart_toy',         label: 'Agentic' },
};

interface CanvasNode {
  stage: WorkflowStage;
  index: number;
  x: number;
  y: number;
  width: number;
  height: number;
  theme: { color: string; icon: string; label: string };
  isBranching: boolean;
  /** Whether this node is a sub-stage inside a for-each */
  isSubStage: boolean;
  /** Parent stage index (set when isSubStage = true) */
  parentIndex: number;
  /** Child index within the parent's sub-stages (set when isSubStage = true) */
  childIndex: number;
}

interface BranchChip {
  toolId: string;
  label: string;
  parentIndex: number;
  x: number;
  y: number;
  width: number;
  height: number;
  isOverflow: boolean;
}

/**
 * Visual canvas for workflow stages — shows nodes connected top-to-bottom.
 * Supports drag-to-reorder, click context menu, zoom/pan, and adding new stages.
 */
@Component({
  selector: 'wfb-canvas',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatIconModule, MatTooltipModule, MatMenuModule],
  host: { style: 'display:flex;flex:1;min-height:0;height:100%' },
  template: `
    <div class="canvas-container" #canvasContainer
         (wheel)="onWheel($event)"
         (mousedown)="onCanvasPanStart($event)"
         (mousemove)="onMouseMove($event)"
         (mouseup)="onMouseUp($event)">

      <!-- Zoom controls -->
      <div class="canvas-controls">
        <button mat-mini-fab matTooltip="Zoom in" (click)="zoomIn()">
          <mat-icon>zoom_in</mat-icon>
        </button>
        <button mat-mini-fab matTooltip="Zoom out" (click)="zoomOut()">
          <mat-icon>zoom_out</mat-icon>
        </button>
        <button mat-mini-fab matTooltip="Fit to view" (click)="fitToView()">
          <mat-icon>fit_screen</mat-icon>
        </button>
        <button mat-mini-fab matTooltip="Add stage" [matMenuTriggerFor]="addMenu">
          <mat-icon>add</mat-icon>
        </button>
      </div>

      <mat-menu #addMenu="matMenu">
        @for (st of stageTypes; track st) {
          <button mat-menu-item (click)="addStage(st)">
            <mat-icon [style.color]="getTheme(st).color">{{ getTheme(st).icon }}</mat-icon>
            {{ getTheme(st).label }}
          </button>
        }
      </mat-menu>

      <!-- Viewport with zoom/pan transform -->
      <div class="canvas-viewport"
           [style.transform]="'translate(' + panX() + 'px,' + panY() + 'px) scale(' + zoom() + ')'">

        <!-- SVG connection lines -->
        <svg class="canvas-lines" [attr.width]="svgWidth()" [attr.height]="svgHeight()">
          @for (conn of connections(); track $index) {
            <path [attr.d]="conn.path"
                  fill="none" stroke="#9e9e9e" stroke-width="2"
                  stroke-dasharray="6,3"
                  marker-end="url(#arrowhead)" />
          }
          @for (conn of branchConnections(); track $index) {
            <path [attr.d]="conn.path"
                  fill="none" [attr.stroke]="conn.color" stroke-width="1.5"
                  stroke-dasharray="4,3"
                  [attr.marker-end]="'url(#arrowhead-branch)'" />
          }
          <defs>
            <marker id="arrowhead" markerWidth="8" markerHeight="6"
                    refX="8" refY="3" orient="auto">
              <polygon points="0 0, 8 3, 0 6" fill="#9e9e9e" />
            </marker>
            <marker id="arrowhead-branch" markerWidth="6" markerHeight="5"
                    refX="6" refY="2.5" orient="auto">
              <polygon points="0 0, 6 2.5, 0 5" fill="#66bb6a" />
            </marker>
          </defs>
        </svg>

        <!-- Drop indicator line -->
        @if (dropTargetIndex() >= 0) {
          <div class="canvas-drop-indicator"
               [style.top.px]="dropIndicatorY()"
               [style.left.px]="NODE_CENTER_X - 90"
               [style.width.px]="NODE_W">
          </div>
        }

        <!-- Stage nodes -->
        @for (node of nodes(); track node.stage.stageId) {
          <div class="canvas-node"
               [class.canvas-node-selected]="!node.isSubStage && builder.selectedStageIndex() === node.index"
               [class.canvas-node-substage]="node.isSubStage"
               [class.canvas-node-dragging]="!node.isSubStage && draggingIndex() === node.index"
               [class.canvas-node-dropping]="!node.isSubStage && droppingIndex() === node.index"
               [style.left.px]="!node.isSubStage && (draggingIndex() === node.index || droppingIndex() === node.index) ? dragCurrentX() : node.x"
               [style.top.px]="!node.isSubStage && (draggingIndex() === node.index || droppingIndex() === node.index) ? dragCurrentY() : (node.y + (node.isSubStage ? 0 : (nodeOffsets().get(node.index) ?? 0)))"
               [style.borderColor]="node.theme.color"
               [style.z-index]="!node.isSubStage && (draggingIndex() === node.index || droppingIndex() === node.index) ? 100 : 1"
               (mousedown)="node.isSubStage ? $event.stopPropagation() : onNodeMouseDown(node, $event)">
            <div class="canvas-node-header" [style.background]="node.theme.color">
              <mat-icon>{{ node.theme.icon }}</mat-icon>
              <span class="canvas-node-type">{{ node.theme.label }}</span>
              @if (node.isBranching) {
                <mat-icon class="canvas-node-dynamic-badge" matTooltip="Dynamic tool selection">bolt</mat-icon>
              }
              @if (!node.isSubStage) {
                <span class="canvas-node-step">#{{ node.index + 1 }}</span>
              }
            </div>
            <div class="canvas-node-body">
              <span class="canvas-node-id">{{ node.stage.stageId }}</span>
            </div>
            <!-- Node action buttons (visible on hover, top-level only) -->
            @if (!node.isSubStage) {
            <div class="canvas-node-actions">
              <button mat-icon-button matTooltip="Move up" [disabled]="node.index === 0"
                      (click)="moveUp(node.index, $event)">
                <mat-icon>arrow_upward</mat-icon>
              </button>
              <button mat-icon-button matTooltip="Move down"
                      [disabled]="node.index === builder.stages().length - 1"
                      (click)="moveDown(node.index, $event)">
                <mat-icon>arrow_downward</mat-icon>
              </button>
              <button mat-icon-button matTooltip="Duplicate"
                      (click)="duplicateStage(node.index, $event)">
                <mat-icon>content_copy</mat-icon>
              </button>
              <button mat-icon-button matTooltip="Delete"
                      (click)="removeStage(node.index, $event)">
                <mat-icon>delete</mat-icon>
              </button>
            </div>
            }
          </div>
        }

        <!-- Branch chips (visual-only tool indicators for dynamic stages) -->
        @for (chip of branchChips(); track chip.toolId + chip.parentIndex) {
          <div class="canvas-branch-chip"
               [class.canvas-branch-chip-overflow]="chip.isOverflow"
               [style.left.px]="chip.x"
               [style.top.px]="chip.y"
               [style.width.px]="chip.width"
               [style.height.px]="chip.height"
               [matTooltip]="chip.toolId"
               (mousedown)="onBranchChipClick(chip, $event)">
            <span class="branch-chip-label">{{ chip.label }}</span>
          </div>
        }

        @if (nodes().length === 0) {
          <div class="canvas-empty">
            <mat-icon>add_circle_outline</mat-icon>
            <p>No stages yet. Click + to add one.</p>
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    .canvas-container {
      position: relative;
      flex: 1;
      overflow: hidden;
      height: 100%;
      min-height: 0;
      background:
        linear-gradient(90deg, #f0f0f0 1px, transparent 1px),
        linear-gradient(#f0f0f0 1px, transparent 1px);
      background-size: 20px 20px;
      background-color: #fafafa;
      cursor: grab;
      user-select: none;
    }
    .canvas-container.grabbing { cursor: grabbing; }

    .canvas-controls {
      position: absolute;
      top: 12px;
      left: 12px;
      display: flex;
      flex-direction: column;
      gap: 8px;
      z-index: 10;
    }
    .canvas-controls button { transform: scale(0.85); }

    .canvas-viewport {
      position: absolute;
      top: 0; left: 0;
      transform-origin: 0 0;
    }

    .canvas-lines {
      position: absolute;
      top: 0; left: 0;
      pointer-events: none;
    }

    .canvas-drop-indicator {
      position: absolute;
      height: 3px;
      background: #1976d2;
      border-radius: 2px;
      pointer-events: none;
      z-index: 50;
      box-shadow: 0 0 6px rgba(25,118,210,0.5);
    }

    .canvas-node {
      position: absolute;
      width: 180px;
      border: 2px solid;
      border-radius: 8px;
      background: #fff;
      box-shadow: 0 2px 6px rgba(0,0,0,0.12);
      cursor: pointer;
      transition: top 0.4s cubic-bezier(0.25, 0.8, 0.25, 1),
                  box-shadow 0.15s;
    }
    .canvas-node:hover {
      box-shadow: 0 4px 12px rgba(0,0,0,0.2);
    }
    .canvas-node-selected {
      box-shadow: 0 0 0 3px rgba(25,118,210,0.4), 0 4px 12px rgba(0,0,0,0.2);
    }
    .canvas-node-dragging {
      opacity: 0.85;
      box-shadow: 0 8px 24px rgba(0,0,0,0.3);
      cursor: grabbing;
      transition: none;
    }
    .canvas-node-dropping {
      opacity: 0.85;
      box-shadow: 0 4px 12px rgba(0,0,0,0.2);
      transition: top 0.4s cubic-bezier(0.25, 0.8, 0.25, 1),
                  left 0.4s cubic-bezier(0.25, 0.8, 0.25, 1),
                  opacity 0.3s, box-shadow 0.3s;
      z-index: 100;
    }

    .canvas-node-header {
      display: flex;
      align-items: center;
      gap: 6px;
      padding: 6px 10px;
      border-radius: 6px 6px 0 0;
      color: #fff;
      font-size: 12px;
      font-weight: 500;
      mat-icon { font-size: 16px; width: 16px; height: 16px; }
    }
    .canvas-node-step {
      margin-left: auto;
      font-size: 11px;
      opacity: 0.8;
    }

    .canvas-node-body {
      padding: 8px 10px;
      display: flex;
      align-items: center;
    }
    .canvas-node-id {
      font-size: 12px;
      font-weight: 500;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      max-width: 155px;
    }

    /* Inline action buttons — appear on hover only */
    .canvas-node-actions {
      display: none;
      position: absolute;
      right: -38px;
      top: 50%;
      transform: translateY(-50%);
      flex-direction: column;
      gap: 0;
      background: #fff;
      border-radius: 6px;
      box-shadow: 0 2px 8px rgba(0,0,0,0.15);
      padding: 4px;
      z-index: 5;
    }
    .canvas-node:hover .canvas-node-actions {
      display: flex;
    }
    .canvas-node-actions button[mat-icon-button] {
      width: 30px;
      height: 30px;
      padding: 0;
      display: flex;
      align-items: center;
      justify-content: center;
      border-radius: 6px;
      --mdc-icon-button-state-layer-size: 30px;
      --mdc-icon-button-icon-size: 18px;
    }
    .canvas-node-actions mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }

    .canvas-empty {
      position: absolute;
      top: 50%; left: 50%;
      transform: translate(-50%, -50%);
      text-align: center;
      opacity: 0.5;
      mat-icon { font-size: 48px; width: 48px; height: 48px; }
      p { font-size: 14px; margin-top: 8px; }
    }

    /* Dynamic tool badge on branching nodes */
    .canvas-node-dynamic-badge {
      font-size: 14px;
      width: 14px;
      height: 14px;
      margin-left: 4px;
      opacity: 0.9;
      color: #ffeb3b;
    }

    /* Branch chips — small pill-shaped indicators */
    .canvas-branch-chip {
      position: absolute;
      border-radius: 16px;
      background: #e8f5e9;
      border: 1.5px solid #66bb6a;
      display: flex;
      align-items: center;
      justify-content: center;
      cursor: pointer;
      transition: box-shadow 0.15s, transform 0.15s;
      z-index: 2;
    }
    .canvas-branch-chip:hover {
      box-shadow: 0 2px 8px rgba(46, 125, 50, 0.25);
      transform: scale(1.05);
    }
    .canvas-branch-chip-overflow {
      background: #f5f5f5;
      border-color: #bdbdbd;
      font-style: italic;
    }
    .branch-chip-label {
      font-size: 10px;
      font-weight: 500;
      color: #2e7d32;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      padding: 0 8px;
      max-width: 100%;
    }
    .canvas-branch-chip-overflow .branch-chip-label {
      color: #757575;
    }

    /* Sub-stage nodes — smaller, indented, with different styling */
    .canvas-node-substage {
      width: 150px;
      opacity: 0.9;
      border-style: dashed;
      box-shadow: 0 1px 4px rgba(0,0,0,0.08);
    }
    .canvas-node-substage .canvas-node-header {
      padding: 4px 8px;
      font-size: 11px;
    }
    .canvas-node-substage .canvas-node-body {
      padding: 6px 8px;
    }
    .canvas-node-substage .canvas-node-id {
      font-size: 11px;
      max-width: 125px;
    }
    .canvas-node-substage:hover {
      box-shadow: 0 2px 8px rgba(0,0,0,0.15);
    }
  `],
})
export class WorkflowCanvasComponent implements AfterViewInit, OnDestroy {
  readonly builder = inject(WorkflowBuilderService);

  private readonly canvasRef = viewChild<ElementRef<HTMLDivElement>>('canvasContainer');

  constructor() {
    // Re-fit the canvas whenever a new workflow is loaded
    effect(() => {
      this.builder.promptLoadVersion(); // track the signal
      // Wait a frame for the DOM to update with the new nodes
      requestAnimationFrame(() => this.fitToView());
    });
  }

  readonly stageTypes: StageType[] = [
    'ai-extract', 'tool', 'ai-generate', 'ai-analyze',
    'user-select', 'confirmation', 'foreach',
  ];

  // Viewport state
  readonly zoom = signal(1);
  readonly panX = signal(60);
  readonly panY = signal(40);

  // Layout constants (vertical flow, centered)
  readonly NODE_W = 180;
  private readonly NODE_H = 68;
  private readonly V_GAP = 50;
  readonly NODE_CENTER_X = 250;

  // Branch chip layout constants
  private readonly BRANCH_CHIP_W = 110;
  private readonly BRANCH_CHIP_H = 30;
  private readonly BRANCH_H_GAP = 10;
  private readonly BRANCH_V_OFFSET = 36;
  private readonly BRANCH_V_BELOW = 36;
  private readonly MAX_VISIBLE_CHIPS = 4;

  // Panning state
  private isPanning = false;
  private panStartX = 0;
  private panStartY = 0;
  private panOriginX = 0;
  private panOriginY = 0;

  // Drag-reorder state
  readonly draggingIndex = signal(-1);
  readonly droppingIndex = signal(-1);
  readonly dragCurrentX = signal(0);
  readonly dragCurrentY = signal(0);
  readonly dropTargetIndex = signal(-1);
  /** Visual Y offsets for non-dragged cards during drop animation */
  readonly nodeOffsets = signal<Map<number, number>>(new Map());
  private dragOffsetX = 0;
  private dragOffsetY = 0;
  private hasDragged = false;

  // Sub-stage layout constants
  private readonly SUB_NODE_W = 150;
  private readonly SUB_NODE_H = 56;
  private readonly SUB_V_GAP = 30;
  private readonly SUB_INDENT = 60;

  /** Check if a stage is a branching dynamic tool stage */
  private isBranchingStage(stage: WorkflowStage): boolean {
    if (stage.type !== 'tool') return false;
    const tool = stage as ToolStage;
    return !tool.toolId && !!tool.prePrompt && (tool.prePrompt.allowedToolIds?.length ?? 0) > 1;
  }

  /** Computed nodes — vertical layout with extra space for branching and for-each nodes */
  readonly nodes = computed<CanvasNode[]>(() => {
    const stages = this.builder.stages();
    const result: CanvasNode[] = [];
    let currentY = 0;

    for (let i = 0; i < stages.length; i++) {
      const stage = stages[i];
      const isBranching = this.isBranchingStage(stage);
      const node: CanvasNode = {
        stage,
        index: i,
        x: this.NODE_CENTER_X - this.NODE_W / 2,
        y: currentY,
        width: this.NODE_W,
        height: this.NODE_H,
        theme: STAGE_THEME[stage.type] ?? STAGE_THEME['tool'],
        isBranching,
        isSubStage: false,
        parentIndex: -1,
        childIndex: -1,
      };
      result.push(node);

      if (isBranching) {
        currentY += this.NODE_H + this.BRANCH_V_OFFSET + this.BRANCH_CHIP_H + this.BRANCH_V_BELOW;
      } else {
        currentY += this.NODE_H + this.V_GAP;
      }

      // Render sub-stages for for-each nodes
      if (stage.type === 'foreach') {
        const forEachStage = stage as ForEachStage;
        if (forEachStage.stages && forEachStage.stages.length > 0) {
          for (let j = 0; j < forEachStage.stages.length; j++) {
            const subStage = forEachStage.stages[j];
            const subNode: CanvasNode = {
              stage: subStage,
              index: i,
              x: this.NODE_CENTER_X - this.SUB_NODE_W / 2 + this.SUB_INDENT,
              y: currentY,
              width: this.SUB_NODE_W,
              height: this.SUB_NODE_H,
              theme: STAGE_THEME[subStage.type] ?? STAGE_THEME['tool'],
              isBranching: false,
              isSubStage: true,
              parentIndex: i,
              childIndex: j,
            };
            result.push(subNode);
            currentY += this.SUB_NODE_H + this.SUB_V_GAP;
          }
        }
      }
    }

    return result;
  });

  /** Branch chips for dynamic tool stages */
  readonly branchChips = computed<BranchChip[]>(() => {
    const ns = this.nodes();
    const chips: BranchChip[] = [];
    for (const node of ns) {
      if (!node.isBranching) continue;
      const tool = node.stage as ToolStage;
      const allIds = tool.prePrompt!.allowedToolIds;
      const showOverflow = allIds.length > this.MAX_VISIBLE_CHIPS;
      const visibleIds = showOverflow ? allIds.slice(0, this.MAX_VISIBLE_CHIPS - 1) : allIds;
      const chipCount = showOverflow ? visibleIds.length + 1 : visibleIds.length;
      const totalWidth = chipCount * this.BRANCH_CHIP_W + (chipCount - 1) * this.BRANCH_H_GAP;
      const startX = node.x + node.width / 2 - totalWidth / 2;
      const chipY = node.y + node.height + this.BRANCH_V_OFFSET;

      for (let i = 0; i < visibleIds.length; i++) {
        chips.push({
          toolId: visibleIds[i],
          label: visibleIds[i],
          parentIndex: node.index,
          x: startX + i * (this.BRANCH_CHIP_W + this.BRANCH_H_GAP),
          y: chipY,
          width: this.BRANCH_CHIP_W,
          height: this.BRANCH_CHIP_H,
          isOverflow: false,
        });
      }
      if (showOverflow) {
        const remaining = allIds.length - visibleIds.length;
        chips.push({
          toolId: `+${remaining}-more`,
          label: `+${remaining} more`,
          parentIndex: node.index,
          x: startX + visibleIds.length * (this.BRANCH_CHIP_W + this.BRANCH_H_GAP),
          y: chipY,
          width: this.BRANCH_CHIP_W,
          height: this.BRANCH_CHIP_H,
          isOverflow: true,
        });
      }
    }
    return chips;
  });

  /** SVG connection paths — vertical lines between non-branching nodes */
  readonly connections = computed(() => {
    const ns = this.nodes();
    const conns: { fromIndex: number; path: string }[] = [];
    for (let i = 0; i < ns.length - 1; i++) {
      if (ns[i].isBranching) continue; // handled by branchConnections
      const from = ns[i];
      const to = ns[i + 1];
      const fromX = from.x + from.width / 2;
      const fromY = from.y + from.height;
      const toX = to.x + to.width / 2;
      const toY = to.y;
      const midY = (fromY + toY) / 2;
      conns.push({
        fromIndex: i,
        path: `M ${fromX} ${fromY} C ${fromX} ${midY}, ${toX} ${midY}, ${toX} ${toY}`,
      });
    }
    return conns;
  });

  /** Branch connection paths — fan-out from tool node to chips, fan-in from chips to next node */
  readonly branchConnections = computed(() => {
    const ns = this.nodes();
    const chips = this.branchChips();
    const conns: { path: string; color: string }[] = [];

    for (const node of ns) {
      if (!node.isBranching) continue;
      const nodeChips = chips.filter(c => c.parentIndex === node.index);
      const nextNode = ns.find(n => n.index === node.index + 1);

      // Fan-out: tool node bottom → each chip top
      const fromX = node.x + node.width / 2;
      const fromY = node.y + node.height;
      for (const chip of nodeChips) {
        const toX = chip.x + chip.width / 2;
        const toY = chip.y;
        const midY = (fromY + toY) / 2;
        conns.push({
          path: `M ${fromX} ${fromY} C ${fromX} ${midY}, ${toX} ${midY}, ${toX} ${toY}`,
          color: chip.isOverflow ? '#bdbdbd' : '#66bb6a',
        });
      }

      // Fan-in: each chip bottom → next node top
      if (nextNode) {
        const toX = nextNode.x + nextNode.width / 2;
        const toY = nextNode.y;
        for (const chip of nodeChips) {
          const cFromX = chip.x + chip.width / 2;
          const cFromY = chip.y + chip.height;
          const midY = (cFromY + toY) / 2;
          conns.push({
            path: `M ${cFromX} ${cFromY} C ${cFromX} ${midY}, ${toX} ${midY}, ${toX} ${toY}`,
            color: chip.isOverflow ? '#bdbdbd' : '#66bb6a',
          });
        }
      }
    }
    return conns;
  });

  readonly svgWidth = computed(() => {
    const chips = this.branchChips();
    const ns = this.nodes();
    const nodeMaxRight = ns.length > 0 ? Math.max(...ns.map(n => n.x + n.width)) : 0;
    if (chips.length === 0) return Math.max(this.NODE_W + 200, nodeMaxRight + 60);
    const minLeft = Math.min(0, ...chips.map(c => c.x));
    const maxRight = Math.max(...chips.map(c => c.x + c.width), nodeMaxRight);
    return Math.max(this.NODE_W + 200, maxRight - minLeft + 60);
  });

  readonly svgHeight = computed(() => {
    const ns = this.nodes();
    if (ns.length === 0) return 600;
    return Math.max(...ns.map(n => n.y + n.height)) + 100;
  });

  /** Y position of the drop indicator line (top of the target slot) */
  readonly dropIndicatorY = computed(() => {
    const idx = this.dropTargetIndex();
    if (idx < 0) return 0;
    const ns = this.nodes();
    if (idx < ns.length) return ns[idx].y;
    // After the last node
    const last = ns[ns.length - 1];
    return last ? last.y + last.height + 20 : 0;
  });

  ngAfterViewInit(): void {
    // Defer so the container has its final layout dimensions
    requestAnimationFrame(() => this.fitToView());
  }

  ngOnDestroy(): void {
    this.isPanning = false;
    this.draggingIndex.set(-1);
  }

  // ─── Zoom ────────────────────────────────────────────────────

  onWheel(e: WheelEvent): void {
    e.preventDefault();
    const delta = e.deltaY > 0 ? -0.1 : 0.1;
    this.zoom.update(z => Math.max(0.3, Math.min(2.5, z + delta)));
  }

  zoomIn(): void {
    this.zoom.update(z => Math.min(2.5, z + 0.15));
  }

  zoomOut(): void {
    this.zoom.update(z => Math.max(0.3, z - 0.15));
  }

  fitToView(): void {
    const container = this.canvasRef()?.nativeElement;
    if (!container) return;
    const rect = container.getBoundingClientRect();
    const contentW = this.svgWidth();
    const contentH = this.svgHeight();
    if (contentW === 0 || contentH === 0) {
      this.zoom.set(1);
      this.panX.set(60);
      this.panY.set(40);
      return;
    }
    const scaleX = (rect.width - 100) / contentW;
    const scaleY = (rect.height - 80) / contentH;
    const scale = Math.max(0.3, Math.min(1.5, Math.min(scaleX, scaleY)));
    this.zoom.set(scale);
    // Center horizontally
    this.panX.set((rect.width - contentW * scale) / 2);
    this.panY.set(40);
  }

  // ─── Pan ─────────────────────────────────────────────────────

  onCanvasPanStart(e: MouseEvent): void {
    if ((e.target as HTMLElement).closest('.canvas-node') ||
        (e.target as HTMLElement).closest('.canvas-controls') ||
        (e.target as HTMLElement).closest('button')) return;
    this.isPanning = true;
    this.panStartX = e.clientX;
    this.panStartY = e.clientY;
    this.panOriginX = this.panX();
    this.panOriginY = this.panY();
    const container = this.canvasRef()?.nativeElement;
    if (container) container.classList.add('grabbing');
  }

  onMouseMove(e: MouseEvent): void {
    if (this.isPanning) {
      this.panX.set(this.panOriginX + (e.clientX - this.panStartX));
      this.panY.set(this.panOriginY + (e.clientY - this.panStartY));
      return;
    }

    if (this.draggingIndex() >= 0) {
      this.hasDragged = true;
      const z = this.zoom();
      const container = this.canvasRef()?.nativeElement;
      if (!container) return;
      const rect = container.getBoundingClientRect();
      const canvasX = (e.clientX - rect.left - this.panX()) / z - this.dragOffsetX;
      const canvasY = (e.clientY - rect.top - this.panY()) / z - this.dragOffsetY;
      this.dragCurrentX.set(canvasX);
      this.dragCurrentY.set(canvasY);

      // Determine drop target based on actual node positions
      const dragTop = canvasY;
      const ns = this.nodes();
      const fromIdx = this.draggingIndex();
      let targetSlot = fromIdx;
      for (let i = 0; i < ns.length; i++) {
        if (i === fromIdx) continue;
        const nodeMidY = ns[i].y + ns[i].height / 2;
        if (dragTop + this.NODE_H / 2 > nodeMidY && i > targetSlot) {
          targetSlot = i;
        } else if (dragTop + this.NODE_H / 2 < nodeMidY && i < fromIdx) {
          targetSlot = i;
          break;
        }
      }
      targetSlot = Math.max(0, Math.min(this.builder.stages().length - 1, targetSlot));
      this.dropTargetIndex.set(targetSlot);
    }
  }

  onMouseUp(e: MouseEvent): void {
    if (this.isPanning) {
      this.isPanning = false;
      const container = this.canvasRef()?.nativeElement;
      if (container) container.classList.remove('grabbing');
      return;
    }

    if (this.draggingIndex() >= 0) {
      const fromIdx = this.draggingIndex();
      const toIdx = this.dropTargetIndex();
      this.dropTargetIndex.set(-1);

      if (this.hasDragged && toIdx >= 0 && toIdx !== fromIdx) {
        // Set up dropping state (transition class, keep current drag position)
        this.draggingIndex.set(-1);
        this.droppingIndex.set(fromIdx);

        // Compute visual offsets for displaced cards using node positions
        const ns = this.nodes();
        const offsets = new Map<number, number>();
        if (fromIdx < toIdx) {
          for (let i = fromIdx + 1; i <= toIdx; i++) {
            offsets.set(i, -(ns[i].y - ns[i - 1].y));
          }
        } else {
          for (let i = toIdx; i < fromIdx; i++) {
            offsets.set(i, ns[i + 1].y - ns[i].y);
          }
        }
        this.nodeOffsets.set(offsets);

        // After one frame (classes + offsets rendered), animate dropped card
        const targetY = ns[toIdx]?.y ?? 0;
        requestAnimationFrame(() => {
          requestAnimationFrame(() => {
            this.dragCurrentX.set(this.NODE_CENTER_X - this.NODE_W / 2);
            this.dragCurrentY.set(targetY);

            // After animation, commit data model and clear visual state
            setTimeout(() => {
              this.droppingIndex.set(-1);
              this.nodeOffsets.set(new Map());
              this.builder.moveStage(fromIdx, toIdx);
            }, 420);
          });
        });
      } else {
        this.draggingIndex.set(-1);
        if (!this.hasDragged) {
          // Force-select (don't toggle) so the stage is expanded in the form view
          this.builder.selectedStageIndex.set(fromIdx);
          this.builder.selectedTabIndex.set(1); // Stages tab
          this.builder.setMode('form');
        }
      }
      this.hasDragged = false;
    }
  }

  // ─── Node Interactions ───────────────────────────────────────

  onNodeMouseDown(node: CanvasNode, e: MouseEvent): void {
    // Ignore if clicking an action button
    if ((e.target as HTMLElement).closest('.canvas-node-actions')) return;
    e.stopPropagation();
    e.preventDefault();

    const z = this.zoom();
    const container = this.canvasRef()?.nativeElement;
    if (!container) return;
    const rect = container.getBoundingClientRect();
    const canvasX = (e.clientX - rect.left - this.panX()) / z;
    const canvasY = (e.clientY - rect.top - this.panY()) / z;
    this.dragOffsetX = canvasX - node.x;
    this.dragOffsetY = canvasY - node.y;
    this.dragCurrentX.set(node.x);
    this.dragCurrentY.set(node.y);
    this.draggingIndex.set(node.index);
    this.hasDragged = false;
    this.builder.selectStage(node.index);
  }

  moveUp(index: number, e: MouseEvent): void {
    e.stopPropagation();
    if (index > 0) this.builder.moveStage(index, index - 1);
  }

  moveDown(index: number, e: MouseEvent): void {
    e.stopPropagation();
    if (index < this.builder.stages().length - 1) this.builder.moveStage(index, index + 1);
  }

  duplicateStage(index: number, e: MouseEvent): void {
    e.stopPropagation();
    this.builder.duplicateStage(index);
  }

  removeStage(index: number, e: MouseEvent): void {
    e.stopPropagation();
    this.builder.removeStage(index);
  }

  addStage(type: StageType): void {
    this.builder.addStage(type);
  }

  getTheme(type: StageType) {
    return STAGE_THEME[type] ?? STAGE_THEME['tool'];
  }

  onBranchChipClick(chip: BranchChip, e: MouseEvent): void {
    e.stopPropagation();
    this.builder.selectedStageIndex.set(chip.parentIndex);
    this.builder.selectedTabIndex.set(1);
    this.builder.setMode('form');
  }
}
