import { Component, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatListModule } from '@angular/material/list';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatExpansionModule } from '@angular/material/expansion';
import { WorkflowBuilderService } from '../../../services/workflow-builder.service';
import { ToolDefinition, groupToolsByService, WorkflowStage, AgenticStage, ForEachStage } from '../../../models';

/** Resolved agentic stage tool scope for display in Tool Bindings */
interface AgenticBinding {
  stageId: string;
  domains: string[];
  tools: string[];
  allTools: boolean;
  /** True when domain prefixes expanded to MORE tools than the domains list — show both filters + tools.
   *  False when domains are exact tool names (filters == tools) — show only once. */
  isExpanded: boolean;
}

/**
 * Shows tools referenced by the current workflow stages,
 * and provides a searchable catalog to add new tool references.
 */
@Component({
  selector: 'wfb-tool-binding-panel',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatCardModule, MatListModule,
    MatChipsModule, MatIconModule, MatButtonModule, MatTooltipModule,
    MatFormFieldModule, MatInputModule, MatExpansionModule,
  ],
  template: `
    <div class="wfb-tool-panel">
      <h4>Tool Bindings</h4>

      @if (referencedTools().length === 0 && agenticBindings().length === 0) {
        <p class="wfb-hint">No tools referenced yet. Add a "Tool Call" or "Agentic" stage to bind tools.</p>
      }

      @if (referencedTools().length > 0) {
        <p class="wfb-section-label">Tool Call Stages</p>
        <mat-chip-set>
          @for (tool of referencedTools(); track tool.id) {
            <mat-chip [matTooltip]="tool.description || tool.id">
              <mat-icon matChipAvatar>build</mat-icon>
              {{ tool.id }}
            </mat-chip>
          }
        </mat-chip-set>
      }

      @if (agenticBindings().length > 0) {
        <p class="wfb-section-label" style="margin-top: 12px">Agentic Stages</p>
        @for (binding of agenticBindings(); track binding.stageId) {
          <mat-expansion-panel class="wfb-agentic-panel">
            <mat-expansion-panel-header>
              <mat-panel-title>
                <mat-icon class="wfb-agentic-icon">smart_toy</mat-icon>
                {{ binding.stageId }}
              </mat-panel-title>
              <mat-panel-description>
                @if (binding.allTools) {
                  All {{ binding.tools.length }} MCP tools
                } @else {
                  {{ binding.domains.join(', ') }} — {{ binding.tools.length }} tools
                }
              </mat-panel-description>
            </mat-expansion-panel-header>

            <div class="wfb-agentic-domains">
              @if (binding.allTools) {
                <span class="wfb-hint-inline">No domain filter — all MCP tools are in scope</span>
              } @else if (binding.isExpanded) {
                <!-- Domain prefix filters that expand to more tools: show filters + expanded list -->
                <span class="wfb-label">Filters:</span>
                @for (d of binding.domains; track d) {
                  <mat-chip class="wfb-domain-chip">{{ d }}</mat-chip>
                }
              } @else {
                <!-- Exact tool names selected: no need to show a separate filter row -->
                <span class="wfb-label">Tools:</span>
                @for (tool of binding.tools; track tool) {
                  <mat-chip class="wfb-domain-chip">
                    <mat-icon matChipAvatar>hub</mat-icon>
                    {{ tool }}
                  </mat-chip>
                }
              }
            </div>

            @if (binding.tools.length === 0) {
              <p class="wfb-hint">No MCP tools loaded yet (tool names not available).</p>
            } @else if (binding.isExpanded && !binding.allTools) {
              <!-- Only show the expanded chip list when domain prefixes were used -->
              <mat-chip-set class="wfb-tool-chips">
                @for (tool of binding.tools; track tool) {
                  <mat-chip class="wfb-mcp-chip">
                    <mat-icon matChipAvatar>hub</mat-icon>
                    {{ tool }}
                  </mat-chip>
                }
              </mat-chip-set>
            } @else if (binding.allTools) {
              <mat-chip-set class="wfb-tool-chips">
                @for (tool of binding.tools; track tool) {
                  <mat-chip class="wfb-mcp-chip">
                    <mat-icon matChipAvatar>hub</mat-icon>
                    {{ tool }}
                  </mat-chip>
                }
              </mat-chip-set>
            }
          </mat-expansion-panel>
        }
      }

      <h4 style="margin-top: 16px">Available Tool Catalog ({{ filteredTools().length }} tools)</h4>
      <mat-form-field appearance="outline" class="wfb-tool-search">
        <mat-label>Filter tools by name</mat-label>
        <input matInput [ngModel]="searchFilter()" (ngModelChange)="searchFilter.set($event)"
               placeholder="e.g. work_item, pull_request..." />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>

      @for (group of filteredToolGroups(); track group.service) {
        <mat-card class="wfb-tool-group">
          <mat-card-header>
            <mat-card-title>{{ group.service }}</mat-card-title>
            <mat-card-subtitle>{{ group.tools.length }} tools</mat-card-subtitle>
          </mat-card-header>
          <mat-card-content>
            <mat-list dense>
              @for (tool of group.tools; track tool.id) {
                <mat-list-item>
                  <mat-icon matListItemIcon>{{ tool.httpMethod === 'MCP' ? 'hub' : (tool.internal ? 'memory' : 'http') }}</mat-icon>
                  <span matListItemTitle>{{ tool.id }}</span>
                  @if (tool.httpMethod === 'MCP') {
                    <span matListItemLine class="mcp-badge">MCP → {{ tool.route.substring(4) }}</span>
                  } @else {
                    <span matListItemLine>{{ tool.httpMethod }} {{ tool.route }}</span>
                  }
                  @if (tool.description) {
                    <span matListItemLine class="tool-desc">{{ tool.description }}</span>
                  }
                </mat-list-item>
              }
            </mat-list>
          </mat-card-content>
        </mat-card>
      }

      @if (filteredToolGroups().length === 0) {
        <p class="wfb-hint">No tools match "{{ searchFilter() }}"</p>
      }
    </div>
  `,
  styles: [`
    .wfb-tool-panel { padding: 16px; }
    .wfb-tool-panel h4 { margin: 12px 0 8px; }
    .wfb-hint { font-size: 13px; opacity: 0.6; }
    .wfb-hint-inline { font-size: 12px; opacity: 0.6; font-style: italic; }
    .wfb-tool-group { margin-bottom: 8px; }
    .wfb-tool-search { width: 100%; margin-bottom: 8px; }
    .mcp-badge { color: #1976d2; font-weight: 500; }
    .tool-desc { font-size: 11px; opacity: 0.7; }
    .wfb-section-label {
      font-size: 12px; font-weight: 600; text-transform: uppercase;
      letter-spacing: 0.05em; opacity: 0.6; margin: 4px 0 6px;
    }
    .wfb-agentic-panel {
      margin-bottom: 6px;
      box-shadow: none !important;
      border: 1px solid var(--mat-sys-outline-variant, #e0e0e0);
      border-radius: 8px !important;
    }
    .wfb-agentic-icon {
      font-size: 18px; width: 18px; height: 18px;
      color: #00695c; margin-right: 6px; flex-shrink: 0;
    }
    .wfb-agentic-domains {
      display: flex; align-items: center; flex-wrap: wrap;
      gap: 4px; margin-bottom: 8px;
    }
    .wfb-label {
      font-size: 12px; font-weight: 500; opacity: 0.7; margin-right: 4px;
    }
    .wfb-domain-chip {
      --mat-chip-label-text-color: #1565c0 !important;
      --mdc-chip-label-text-color: #1565c0 !important;
      background: #e3f2fd !important;
      font-size: 11px !important;
    }
    .wfb-tool-chips { display: flex; flex-wrap: wrap; gap: 4px; }
    .wfb-mcp-chip {
      font-size: 11px !important;
      --mat-chip-label-text-color: var(--mat-sys-on-surface-variant, #444) !important;
      --mdc-chip-label-text-color: var(--mat-sys-on-surface-variant, #444) !important;
    }
    mat-chip {
      --mat-chip-label-text-color: var(--mat-sys-on-surface-variant, #444) !important;
      --mdc-chip-label-text-color: var(--mat-sys-on-surface-variant, #444) !important;
    }
  `],
})
export class ToolBindingPanelComponent {
  private readonly builder = inject(WorkflowBuilderService);

  searchFilter = signal('');

  filteredTools = computed(() => {
    const filter = this.searchFilter().toLowerCase().trim();
    const all = this.builder.availableTools();
    if (!filter) return all;
    return all.filter(t =>
      t.id.toLowerCase().includes(filter) ||
      (t.description?.toLowerCase().includes(filter) ?? false) ||
      (t.route?.toLowerCase().includes(filter) ?? false)
    );
  });

  filteredToolGroups = computed(() => groupToolsByService(this.filteredTools()));

  /** Tools referenced by tool stages in the current workflow */
  referencedTools = (): ToolDefinition[] => {
    const stages = this.builder.stages();
    const toolIds = new Set<string>();
    this.collectToolIds(stages, toolIds);
    return this.builder.availableTools().filter(t => toolIds.has(t.id));
  };

  /**
   * Resolved tool lists for all agentic stages (including nested inside foreach).
   * Each entry shows the stage ID, its domain filters, and the matched MCP tool names.
   */
  agenticBindings = computed<AgenticBinding[]>(() => {
    const mcpNames = this.builder.availableMcpToolNames();
    const agenticStages = this.collectAgenticStages(this.builder.stages());

    return agenticStages.map(stage => {
      const domains = stage.toolDomains ?? [];
      const allTools = domains.length === 0;
      const tools = allTools
        ? mcpNames.slice().sort()
        : mcpNames.filter(name => this.toolMatchesDomain(name, domains)).sort();

      return { stageId: stage.stageId, domains, tools, allTools,
               isExpanded: allTools || tools.length !== domains.length };
    });
  });

  // ── Private helpers ─────────────────────────────────────────────────────────

  /** Recursively collect toolIds from tool stages (including foreach sub-stages). */
  private collectToolIds(stages: WorkflowStage[], into: Set<string>): void {
    for (const stage of stages) {
      if (stage.type === 'tool') {
        if (stage.toolId) into.add(stage.toolId);
        stage.prePrompt?.allowedToolIds?.forEach(id => into.add(id));
      } else if (stage.type === 'foreach') {
        const fe = stage as ForEachStage;
        if (fe.stages) this.collectToolIds(fe.stages, into);
      }
    }
  }

  /** Recursively collect all agentic stages (including inside foreach). */
  private collectAgenticStages(stages: WorkflowStage[]): AgenticStage[] {
    const result: AgenticStage[] = [];
    for (const stage of stages) {
      if (stage.type === 'agentic') {
        result.push(stage as AgenticStage);
      } else if (stage.type === 'foreach') {
        const fe = stage as ForEachStage;
        if (fe.stages) result.push(...this.collectAgenticStages(fe.stages));
      }
    }
    return result;
  }

  /**
   * Returns true if a (normalized) MCP tool name matches any of the given domain filters.
   * Mirrors the C# ToolMatchesDomain logic in McpClientService.
   */
  private toolMatchesDomain(toolName: string, domains: string[]): boolean {
    const normalized = toolName
      .replace(/^ado-mcp-server-/, '')
      .replace(/-/g, '_')
      .toLowerCase();

    return domains
      .filter(d => d.trim())
      .some(d => {
        const dn = d.replace(/-/g, '_').replace(/ /g, '_').toLowerCase();
        return normalized === dn || normalized.startsWith(dn + '_');
      });
  }
}
