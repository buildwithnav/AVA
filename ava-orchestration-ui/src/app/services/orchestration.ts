import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface ToolDefinition {
  id: string;
  service: string;
  route: string;
  httpMethod: string;
  description?: string;
  requiresConfirmation: boolean;
  parameters: ToolParameter[];
}

export interface ToolParameter {
  name: string;
  type: string;
  required: boolean;
  location: string;
  description?: string;
}

export interface AgentRun {
  agentRunId: string;
  conversationId: string;
  userId: string;
  domain: string;
  input: string;
  status: 'Running' | 'WaitingForConfirmation' | 'Completed' | 'Failed' | 'Cancelled';
  startedAt: string;
  completedAt?: string;
  tokensUsed: number;
  finalResponse?: string;
  plan: PlanStep[];
  steps: StepExecution[];
}

export interface PlanStep {
  stepId: string;
  toolName: string;
  parameters: Record<string, unknown>;
  rationale: string;
  requiresConfirmation: boolean;
}

export interface StepExecution {
  stepId: string;
  toolName: string;
  success: boolean;
  result?: string;
  error?: string;
  durationMs: number;
}

export interface DomainDefinition {
  id: string;
  name: string;
  description: string;
  owner: string;
  enabled: boolean;
  /** How KB schemas are surfaced to the model: 'inline' (full schema in prompt) or 'catalog' (names + on-demand kb_schema). */
  kbPromptMode?: 'inline' | 'catalog' | string;
  /** Extra domain-specific guidance injected into the system prompt (e.g. join rules). */
  agenticInstruction?: string;
  knowledgeBaseIds?: string[];
  suggestions?: string[];
  welcomeMessage?: string;
  /** CDO Gateway module key gating this domain. Ignored for child domains — see parentId. Null/empty = unmapped (governed by UnmappedDomainPolicy). */
  moduleKey?: string | null;
  /** Id of the parent domain, or null/undefined for a top-level domain. Nesting is capped at 2 tiers. */
  parentId?: string | null;
  /** Identifier in the source system for a child domain — e.g. the study ID. */
  externalRef?: string | null;
}

/** An AVA-native access grant for a child domain. `domainId === null` covers every child of `parentId`. */
export interface DomainGrant {
  id: string;
  /** The top-level domain this grant applies under. */
  parentId: string;
  /** The specific child domain this grant covers, or null for a wildcard (every child of parentId). */
  domainId: string | null;
  principalType: 'user' | 'group';
  principalId: string;
  browse: boolean;
  update: boolean;
  insert: boolean;
  delete: boolean;
}

export interface DomainFile {
  id: string;
  domainId: string;
  filename: string;
  title: string;
  description: string;
  tags: string[];
  contentType: string;
  fileSizeBytes: number;
  chunkCount: number;
  uploadedBy: string;
  uploadedAt: string;
}

export interface KBColumnDefinition {
  label: string;
  description: string;
  dataType: string;
}

export interface KBSchemaColumn {
  name: string;
  type: string;
}

export interface DomainKb {
  id: string;
  name: string;
  description?: string;
  type: string;
  domainId: string;
  source: { kind: string; toolId?: string; endpoint?: string };
  rowCount?: number;
  chunkCount?: number;
  schema?: KBSchemaColumn[];
  columnDefinitions?: Record<string, KBColumnDefinition>;
  lastRefreshedAt?: string;
  createdAt: string;
}

export interface KbDataUploadResult {
  id: string;
  name: string;
  domainId: string;
  rowCount?: number;
  columnCount: number;
  definedColumns: number;
  undefinedColumns: string[];
  created: boolean;
}

@Injectable({
  providedIn: 'root',
})
export class OrchestrationService {
  private apiUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  // ── Tools ────────────────────────────────────────────────────────────────────

  getTools(): Observable<{ count: number; tools: ToolDefinition[] }> {
    return this.http.get<{ count: number; tools: ToolDefinition[] }>(`${this.apiUrl}/api/tools`);
  }

  getArchivedTools(): Observable<{ count: number; tools: ToolDefinition[] }> {
    return this.http.get<{ count: number; tools: ToolDefinition[] }>(`${this.apiUrl}/api/tools/archived`);
  }

  getToolById(id: string): Observable<ToolDefinition> {
    return this.http.get<ToolDefinition>(`${this.apiUrl}/api/tools/${id}`);
  }

  createTool(tool: Partial<ToolDefinition>): Observable<ToolDefinition> {
    return this.http.post<ToolDefinition>(`${this.apiUrl}/api/tools`, tool);
  }

  updateTool(id: string, tool: Partial<ToolDefinition>): Observable<ToolDefinition> {
    return this.http.put<ToolDefinition>(`${this.apiUrl}/api/tools/${id}`, tool);
  }

  archiveTool(id: string): Observable<{ archived: boolean; id: string }> {
    return this.http.put<{ archived: boolean; id: string }>(`${this.apiUrl}/api/tools/${id}/archive`, {});
  }

  restoreTool(id: string): Observable<{ restored: boolean; id: string }> {
    return this.http.put<{ restored: boolean; id: string }>(`${this.apiUrl}/api/tools/${id}/restore`, {});
  }

  // ── Agent runs ───────────────────────────────────────────────────────────────

  getRun(runId: string): Observable<AgentRun> {
    return this.http.get<AgentRun>(`${this.apiUrl}/api/chat/runs/${runId}`);
  }

  getConversationRuns(conversationId: string): Observable<{ count: number; runs: AgentRun[] }> {
    return this.http.get<{ count: number; runs: AgentRun[] }>(
      `${this.apiUrl}/api/chat/conversations/${conversationId}/runs`
    );
  }

  // ── Health ───────────────────────────────────────────────────────────────────

  getHealth(): Observable<{ status: string; service: string; timestamp: string }> {
    return this.http.get<{ status: string; service: string; timestamp: string }>(
      `${this.apiUrl}/health`
    );
  }

  // ── Domains ──────────────────────────────────────────────────────────────────

  getDomains(): Observable<DomainDefinition[]> {
    return this.http.get<DomainDefinition[]>(`${this.apiUrl}/api/domains`);
  }

  getAllDomains(includeArchived = false): Observable<DomainDefinition[]> {
    const endpoint = includeArchived ? '/api/domains/all' : '/api/domains';
    return this.http.get<DomainDefinition[]>(`${this.apiUrl}${endpoint}`);
  }

  getDomainById(id: string): Observable<DomainDefinition> {
    return this.http.get<DomainDefinition>(`${this.apiUrl}/api/domains/${encodeURIComponent(id)}`);
  }

  createDomain(domain: Partial<DomainDefinition>): Observable<DomainDefinition> {
    return this.http.post<DomainDefinition>(`${this.apiUrl}/api/domains`, domain);
  }

  updateDomain(id: string, domain: Partial<DomainDefinition>): Observable<DomainDefinition> {
    return this.http.put<DomainDefinition>(`${this.apiUrl}/api/domains/${encodeURIComponent(id)}`, domain);
  }

  archiveDomain(id: string): Observable<{ archived: boolean; id: string }> {
    return this.http.put<{ archived: boolean; id: string }>(
      `${this.apiUrl}/api/domains/${encodeURIComponent(id)}/archive`, {}
    );
  }

  restoreDomain(id: string): Observable<{ restored: boolean; id: string }> {
    return this.http.put<{ restored: boolean; id: string }>(
      `${this.apiUrl}/api/domains/${encodeURIComponent(id)}/restore`, {}
    );
  }

  /** Hard delete — children only; top-level domains can only be archived. */
  deleteDomain(id: string): Observable<{ deleted: boolean; id: string }> {
    return this.http.delete<{ deleted: boolean; id: string }>(
      `${this.apiUrl}/api/domains/${encodeURIComponent(id)}`
    );
  }

  // ── Child domains & grants ───────────────────────────────────────────────────
  // Children (e.g. individual studies) are full DomainDefinition rows with parentId set —
  // create/update/archive/restore/delete reuse the generic domain methods above.

  getChildDomains(
    parentId: string,
    includeArchived = false,
    filterForSelection = false,
  ): Observable<DomainDefinition[]> {
    const params = new URLSearchParams();
    if (includeArchived) params.set('includeArchived', 'true');
    if (filterForSelection) params.set('filterForSelection', 'true');
    const q = params.size ? `?${params}` : '';
    return this.http.get<DomainDefinition[]>(
      `${this.apiUrl}/api/domains/${encodeURIComponent(parentId)}/children${q}`
    );
  }

  getGrants(parentId: string): Observable<DomainGrant[]> {
    return this.http.get<DomainGrant[]>(
      `${this.apiUrl}/api/domains/${encodeURIComponent(parentId)}/grants`
    );
  }

  upsertGrant(parentId: string, grant: Partial<DomainGrant>): Observable<DomainGrant> {
    return this.http.put<DomainGrant>(
      `${this.apiUrl}/api/domains/${encodeURIComponent(parentId)}/grants`, grant
    );
  }

  removeGrant(parentId: string, id: string): Observable<{ removed: boolean; id: string }> {
    return this.http.delete<{ removed: boolean; id: string }>(
      `${this.apiUrl}/api/domains/${encodeURIComponent(parentId)}/grants/${encodeURIComponent(id)}`
    );
  }

  // ── Domain Files ────────────────────────────────────────────────────────────

  getDomainFiles(domainId: string): Observable<DomainFile[]> {
    return this.http.get<DomainFile[]>(`${this.apiUrl}/api/domain-files?domainId=${domainId}`);
  }

  uploadDomainFile(formData: FormData): Observable<DomainFile> {
    return this.http.post<DomainFile>(`${this.apiUrl}/api/domain-files/upload`, formData);
  }

  updateDomainFileMetadata(
    id: string,
    data: { title?: string; description?: string; tags?: string[] }
  ): Observable<DomainFile> {
    return this.http.put<DomainFile>(`${this.apiUrl}/api/domain-files/${id}/metadata`, data);
  }

  deleteDomainFile(id: string): Observable<{ deleted: boolean }> {
    return this.http.delete<{ deleted: boolean }>(`${this.apiUrl}/api/domain-files/${id}`);
  }

  searchDomainFiles(domainId: string, query: string, topK?: number): Observable<{ chunks: any[] }> {
    return this.http.post<{ chunks: any[] }>(`${this.apiUrl}/api/domain-files/search`, {
      domainId,
      query,
      topK,
    });
  }

  // ── Domain Knowledge Bases ───────────────────────────────────────────────────

  getDomainKbs(domainId: string): Observable<DomainKb[]> {
    return this.http.get<DomainKb[]>(`${this.apiUrl}/api/kb/domain/${encodeURIComponent(domainId)}`);
  }

  createDomainKb(payload: {
    name: string;
    description?: string;
    domainId: string;
    toolId?: string;
    columnDefinitions?: Record<string, KBColumnDefinition>;
  }): Observable<DomainKb> {
    return this.http.post<DomainKb>(`${this.apiUrl}/api/kb/domain`, payload);
  }

  updateKbColumns(
    kbId: string,
    columnDefinitions: Record<string, KBColumnDefinition>,
    schema: KBSchemaColumn[]
  ): Observable<{ id: string; columnDefinitions: Record<string, KBColumnDefinition>; schema: KBSchemaColumn[] }> {
    return this.http.put<{ id: string; columnDefinitions: Record<string, KBColumnDefinition>; schema: KBSchemaColumn[] }>(
      `${this.apiUrl}/api/kb/${encodeURIComponent(kbId)}/columns`,
      { columnDefinitions, schema }
    );
  }

  updateKbDescription(
    kbId: string,
    description: string
  ): Observable<{ id: string; description: string }> {
    return this.http.put<{ id: string; description: string }>(
      `${this.apiUrl}/api/kb/${encodeURIComponent(kbId)}/description`,
      { description }
    );
  }

  updateKbType(
    kbId: string,
    type: string
  ): Observable<{ id: string; type: string }> {
    return this.http.put<{ id: string; type: string }>(
      `${this.apiUrl}/api/kb/${encodeURIComponent(kbId)}/type`,
      { type }
    );
  }

  refreshDomainKb(kbId: string): Observable<DomainKb> {
    return this.http.post<DomainKb>(`${this.apiUrl}/api/kb/${encodeURIComponent(kbId)}/refresh`, {});
  }

  /**
   * Upload a data file (Excel/CSV) to (re)populate a domain KB. Only `file` and `domainId`
   * are required; the KB's existing name/description/column definitions are preserved.
   */
  uploadKbDataFile(
    kbId: string,
    formData: FormData
  ): Observable<KbDataUploadResult> {
    return this.http.post<KbDataUploadResult>(
      `${this.apiUrl}/api/kb/${encodeURIComponent(kbId)}/upload-file`,
      formData
    );
  }

  deleteDomainKb(kbId: string): Observable<{ deleted: boolean }> {
    return this.http.delete<{ deleted: boolean }>(`${this.apiUrl}/api/kb/${encodeURIComponent(kbId)}`);
  }
}
