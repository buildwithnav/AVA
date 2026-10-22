import { Component, computed, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatTableModule } from '@angular/material/table';
import { MatBadgeModule } from '@angular/material/badge';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDividerModule } from '@angular/material/divider';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTabsModule } from '@angular/material/tabs';
import { MatDialogModule } from '@angular/material/dialog';
import { OrchestrationService, ToolDefinition, ToolParameter } from '../../services/orchestration';

interface ServiceGroup {
  name: string;
  baseUrl: string;
  enabled: boolean;
  tools: ToolDefinition[];
  status?: 'online' | 'offline' | 'unknown';
}

interface McpCategory {
  name: string;
  icon: string;
  toolNames: string[];
  tools: ToolDefinition[];
}

@Component({
  selector: 'app-tool-server-manager',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatTableModule,
    MatBadgeModule,
    MatTooltipModule,
    MatDividerModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatTabsModule,
    MatDialogModule,
  ],
  templateUrl: './tool-server-manager.html',
  styleUrl: './tool-server-manager.scss',
})
export class ToolServerManager implements OnInit {
  services = signal<ServiceGroup[]>([]);
  mcpCategories = signal<McpCategory[]>([]);
  archivedTools = signal<ToolDefinition[]>([]);
  showArchived = signal(false);
  isLoading = signal(true);
  private searchFilter = signal('');

  searchQuery = '';
  editingToolId: string | null = null;
  creatingNew = false;
  newTool: Partial<ToolDefinition> = this.emptyTool();

  filteredServices = computed(() => {
    const query = this.searchFilter().toLowerCase();
    if (!query) return this.services();
    return this.services()
      .map(svc => ({
        ...svc,
        tools: svc.tools.filter(t => this.toolMatchesSearch(t, query))
      }))
      .filter(svc => svc.tools.length > 0 || svc.name.toLowerCase().includes(query));
  });

  filteredMcpCategories = computed(() => {
    const query = this.searchFilter().toLowerCase();
    if (!query) return this.mcpCategories();
    return this.mcpCategories()
      .map(cat => ({
        ...cat,
        tools: cat.tools.filter(t => this.toolMatchesSearch(t, query))
      }))
      .filter(cat => cat.tools.length > 0);
  });

  private toolMatchesSearch(tool: ToolDefinition, query: string): boolean {
    return tool.id.toLowerCase().includes(query)
      || tool.service.toLowerCase().includes(query)
      || tool.route.toLowerCase().includes(query)
      || (tool.description ?? '').toLowerCase().includes(query);
  }

  private readonly MCP_CATEGORY_MAP: { name: string; icon: string; tools: string[] }[] = [
    {
      name: 'Pull Request Lifecycle', icon: 'merge_type',
      tools: ['list_pull_requests', 'get_pull_request', 'create_pull_request', 'update_pull_request', 'merge_pull_request']
    },
    {
      name: 'PR Review & Comments', icon: 'rate_review',
      tools: ['get_pull_request_comments', 'add_pull_request_comment', 'vote_on_pull_request', 'get_pull_request_files', 'add_pull_request_reviewer', 'update_pull_request_thread']
    },
    {
      name: 'PR Policies & Statuses', icon: 'policy',
      tools: ['get_pull_request_policies', 'get_pull_request_statuses', 'add_pull_request_label', 'remove_pull_request_label', 'add_pr_work_items', 'get_pull_request_iterations']
    },
    {
      name: 'Repository & Git', icon: 'source',
      tools: ['list_repositories', 'create_repository', 'delete_repository', 'rename_repository', 'list_deleted_repositories', 'restore_repository', 'destroy_deleted_repository', 'list_branches', 'create_branch', 'delete_branch', 'compare_branches', 'get_file_contents', 'list_directory', 'list_commits', 'get_commit', 'get_file_history', 'search_commits', 'push_files', 'search_code', 'search_wiki', 'search_work_items', 'get_pull_requests_by_commit', 'get_file_diff', 'get_repository_stats']
    },
    {
      name: 'Work Items', icon: 'task_alt',
      tools: ['list_work_items', 'get_work_item', 'create_work_item', 'update_work_item', 'delete_work_item', 'list_recycle_bin', 'restore_work_item', 'add_work_item_comment', 'get_work_item_updates', 'link_work_items', 'remove_work_item_link', 'link_work_item_to_pr', 'unlink_work_item_from_pr', 'list_work_item_types', 'get_work_item_type_fields', 'get_area_paths', 'create_area_path', 'delete_area_path', 'get_iteration_paths', 'create_iteration', 'update_iteration', 'delete_iteration', 'run_wiql_query', 'list_saved_queries', 'get_saved_query', 'run_saved_query', 'list_tags', 'delete_tag', 'get_work_items_batch', 'update_work_items_batch', 'get_my_work_items']
    },
    {
      name: 'Enterprise & Governance', icon: 'admin_panel_settings',
      tools: ['get_branch_policies', 'get_policy_types', 'requeue_policy_evaluation', 'list_teams', 'get_team_members', 'list_projects', 'get_project', 'get_identity', 'resolve_reviewer', 'get_current_user', 'get_user_memberships', 'get_group_members', 'get_repo_permissions', 'set_repo_permissions', 'create_branch_policy', 'update_branch_policy', 'delete_branch_policy']
    },
    {
      name: 'Wiki', icon: 'menu_book',
      tools: ['list_wikis', 'get_wiki_page', 'create_wiki_page', 'update_wiki_page', 'list_wiki_pages', 'delete_wiki_page', 'get_wiki_page_stats']
    },
    {
      name: 'Pipelines / CI-CD', icon: 'rocket_launch',
      tools: ['list_pipelines', 'get_pipeline', 'list_builds', 'get_build', 'get_build_timeline', 'get_build_log', 'get_build_log_by_task', 'get_build_image_tag', 'queue_build', 'create_pipeline_definition', 'update_pipeline_definition', 'get_pipeline_permissions', 'authorize_pipeline_resource', 'get_build_artifacts', 'get_build_changes', 'get_build_definition', 'list_build_definition_revisions', 'retry_build', 'cancel_build', 'delete_pipeline_definition', 'list_agent_queues', 'list_service_connections', 'list_environments', 'check_pipeline_authorizations', 'set_pipeline_trigger', 'get_pipeline_triggers', 'scaffold_container_pipeline', 'scaffold_container_infra', 'deploy_container_service', 'get_pool_agents', 'get_build_agent_info', 'get_build_test_results', 'compare_builds', 'get_agent_queue_depth']
    },
    {
      name: 'Releases & Deployments', icon: 'publish',
      tools: ['list_release_definitions', 'get_release_definition', 'list_releases', 'get_release', 'get_release_approvals', 'update_release_approval']
    },
    {
      name: 'Variable Groups', icon: 'data_object',
      tools: ['list_variable_groups', 'get_variable_group', 'create_variable_group', 'update_variable_group']
    },
    {
      name: 'Sprint & Board Management', icon: 'dashboard',
      tools: ['get_team_iterations', 'get_iteration_work_items', 'get_team_capacity', 'update_team_member_capacity', 'get_team_days_off', 'update_team_days_off', 'list_boards', 'get_board_columns', 'update_board_columns', 'get_board_rows', 'list_backlogs', 'get_backlog_items', 'get_team_settings', 'list_templates', 'get_template', 'create_template', 'delete_template']
    },
    {
      name: 'Test Management', icon: 'science',
      tools: ['list_test_plans', 'get_test_plan', 'create_test_plan', 'update_test_plan', 'list_test_suites', 'get_test_suite', 'create_test_suite', 'list_test_cases', 'create_test_case', 'update_test_case_steps', 'add_test_cases_to_suite', 'remove_test_cases_from_suite', 'list_test_runs', 'get_test_run', 'create_test_run', 'add_test_results', 'update_test_run', 'delete_test_run', 'delete_test_plan', 'delete_test_suite', 'delete_test_case', 'get_test_results', 'list_test_points', 'list_test_configurations', 'update_test_suite', 'update_test_case_automation']
    },
    {
      name: 'Metrics & Reporting', icon: 'bar_chart',
      tools: ['get_bug_summary', 'get_traceability_report', 'get_test_results_summary']
    },
    {
      name: 'Delivery Plans', icon: 'timeline',
      tools: ['list_delivery_plans', 'get_delivery_plan', 'get_delivery_timeline', 'create_delivery_plan', 'update_delivery_plan', 'delete_delivery_plan']
    },
  ];

  constructor(private svc: OrchestrationService) {}

  ngOnInit(): void {
    this.loadTools();
  }

  private loadTools(): void {
    this.isLoading.set(true);
    this.svc.getTools().subscribe({
      next: (res) => {
        // Separate MCP tools (httpMethod === 'MCP') from REST tools
        const mcpTools: ToolDefinition[] = [];
        const restTools: ToolDefinition[] = [];

        for (const tool of res.tools) {
          if (tool.httpMethod === 'MCP') {
            mcpTools.push(tool);
          } else {
            restTools.push(tool);
          }
        }

        // Group REST tools by service
        const groups = new Map<string, ServiceGroup>();
        for (const tool of restTools) {
          if (!groups.has(tool.service)) {
            groups.set(tool.service, {
              name: tool.service,
              baseUrl: '',
              enabled: true,
              tools: [],
              status: 'unknown',
            });
          }
          groups.get(tool.service)!.tools.push(tool);
        }
        this.services.set(Array.from(groups.values()));

        // Group MCP tools by service
        const mcpToolMap = new Map<string, ToolDefinition>();
        for (const t of mcpTools) {
          const name = t.route.startsWith('mcp:') ? t.route.slice(4) : t.id;
          mcpToolMap.set(name, t);
        }

        const categories: McpCategory[] = this.MCP_CATEGORY_MAP.map(cat => ({
          name: cat.name,
          icon: cat.icon,
          toolNames: cat.tools,
          tools: cat.tools.map(name => mcpToolMap.get(name)).filter((t): t is ToolDefinition => !!t),
        }));
        this.mcpCategories.set(categories);

        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
      }
    });
  }

  // ── Inline editing ──────────────────────────────────────────────────────────

  startEdit(tool: ToolDefinition): void {
    this.editingToolId = tool.id;
    this.creatingNew = false;
  }

  cancelEdit(): void {
    this.editingToolId = null;
    this.creatingNew = false;
    this.newTool = this.emptyTool();
  }

  saveTool(tool: ToolDefinition): void {
    this.svc.updateTool(tool.id, tool).subscribe({
      next: () => {
        this.editingToolId = null;
        this.loadTools();
      }
    });
  }

  startCreate(): void {
    this.creatingNew = true;
    this.editingToolId = null;
    this.newTool = this.emptyTool();
  }

  saveNewTool(): void {
    this.svc.createTool(this.newTool).subscribe({
      next: () => {
        this.creatingNew = false;
        this.newTool = this.emptyTool();
        this.loadTools();
      }
    });
  }

  archiveTool(tool: ToolDefinition): void {
    if (confirm(`Archive tool "${tool.id}"? It can be restored later.`)) {
      this.svc.archiveTool(tool.id).subscribe({
        next: () => this.loadTools()
      });
    }
  }

  toggleArchived(): void {
    this.showArchived.set(!this.showArchived());
    if (this.showArchived()) {
      this.loadArchivedTools();
    }
  }

  restoreTool(tool: ToolDefinition): void {
    this.svc.restoreTool(tool.id).subscribe({
      next: () => {
        this.loadTools();
        this.loadArchivedTools();
      }
    });
  }

  private loadArchivedTools(): void {
    this.svc.getArchivedTools().subscribe({
      next: (res) => this.archivedTools.set(res.tools),
      error: () => this.archivedTools.set([])
    });
  }

  // ── Parameter editing ───────────────────────────────────────────────────────

  addParameter(params: ToolParameter[]): void {
    params.push({ name: '', type: 'string', required: false, location: 'query', description: '' });
  }

  removeParameter(params: ToolParameter[], index: number): void {
    params.splice(index, 1);
  }

  onSearchChange(): void {
    this.searchFilter.set(this.searchQuery);
  }

  clearSearch(): void {
    this.searchQuery = '';
    this.searchFilter.set('');
  }

  getMcpToolName(tool: ToolDefinition): string {
    return tool.route.startsWith('mcp:') ? tool.route.slice(4) : tool.id;
  }

  private emptyTool(): Partial<ToolDefinition> {
    return {
      id: '',
      service: '',
      route: '',
      httpMethod: 'GET',
      description: '',
      parameters: [],
    };
  }
}
