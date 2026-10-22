import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  PromptDefinition,
  ToolDefinition,
  TemplateDefinition,
  ToolboxBundle,
  ImportResult,
} from '../models';

/**
 * HTTP client for the workflow builder CRUD API.
 * Consumers must provide the API base URL via the WORKFLOW_API_URL injection token.
 */
@Injectable({ providedIn: 'root' })
export class WorkflowApiService {
  private readonly http = inject(HttpClient);
  private baseUrl = 'http://localhost:5010';

  /** Allow consumers to set the API base URL */
  configure(baseUrl: string): void {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
  }

  /** Get the configured API base URL */
  getBaseUrl(): string {
    return this.baseUrl;
  }

  // ─── Prompts ────────────────────────────────────────────────

  getPrompts(): Observable<{ count: number; prompts: PromptDefinition[] }> {
    return this.http.get<{ count: number; prompts: PromptDefinition[] }>(
      `${this.baseUrl}/api/prompts`
    );
  }

  getArchivedPrompts(): Observable<{ count: number; prompts: PromptDefinition[] }> {
    return this.http.get<{ count: number; prompts: PromptDefinition[] }>(
      `${this.baseUrl}/api/prompts/archived`
    );
  }

  getPrompt(id: string): Observable<PromptDefinition> {
    return this.http.get<PromptDefinition>(
      `${this.baseUrl}/api/prompts/${encodeURIComponent(id)}`
    );
  }

  createPrompt(prompt: PromptDefinition): Observable<PromptDefinition> {
    return this.http.post<PromptDefinition>(
      `${this.baseUrl}/api/prompts`,
      prompt
    );
  }

  updatePrompt(id: string, prompt: PromptDefinition): Observable<PromptDefinition> {
    return this.http.put<PromptDefinition>(
      `${this.baseUrl}/api/prompts/${encodeURIComponent(id)}`,
      prompt
    );
  }

  archivePrompt(id: string): Observable<{ archived: boolean; id: string }> {
    return this.http.put<{ archived: boolean; id: string }>(
      `${this.baseUrl}/api/prompts/${encodeURIComponent(id)}/archive`, {}
    );
  }

  restorePrompt(id: string): Observable<{ restored: boolean; id: string }> {
    return this.http.put<{ restored: boolean; id: string }>(
      `${this.baseUrl}/api/prompts/${encodeURIComponent(id)}/restore`, {}
    );
  }

  // ─── Templates ──────────────────────────────────────────────

  getTemplates(): Observable<{ count: number; templates: TemplateDefinition[] }> {
    return this.http.get<{ count: number; templates: TemplateDefinition[] }>(
      `${this.baseUrl}/api/templates`
    );
  }

  getArchivedTemplates(): Observable<{ count: number; templates: TemplateDefinition[] }> {
    return this.http.get<{ count: number; templates: TemplateDefinition[] }>(
      `${this.baseUrl}/api/templates/archived`
    );
  }

  getTemplate(id: string): Observable<TemplateDefinition> {
    return this.http.get<TemplateDefinition>(
      `${this.baseUrl}/api/templates/${encodeURIComponent(id)}`
    );
  }

  createTemplate(template: TemplateDefinition): Observable<TemplateDefinition> {
    return this.http.post<TemplateDefinition>(
      `${this.baseUrl}/api/templates`,
      template
    );
  }

  updateTemplate(id: string, template: TemplateDefinition): Observable<TemplateDefinition> {
    return this.http.put<TemplateDefinition>(
      `${this.baseUrl}/api/templates/${encodeURIComponent(id)}`,
      template
    );
  }

  archiveTemplate(id: string): Observable<{ archived: boolean; id: string }> {
    return this.http.put<{ archived: boolean; id: string }>(
      `${this.baseUrl}/api/templates/${encodeURIComponent(id)}/archive`, {}
    );
  }

  restoreTemplate(id: string): Observable<{ restored: boolean; id: string }> {
    return this.http.put<{ restored: boolean; id: string }>(
      `${this.baseUrl}/api/templates/${encodeURIComponent(id)}/restore`, {}
    );
  }

  // ─── Tools (read-only from builder) ─────────────────────────

  getTools(): Observable<{ count: number; tools: ToolDefinition[] }> {
    return this.http.get<{ count: number; tools: ToolDefinition[] }>(
      `${this.baseUrl}/api/tools`
    );
  }

  getToolById(id: string): Observable<ToolDefinition> {
    return this.http.get<ToolDefinition>(
      `${this.baseUrl}/api/tools/${encodeURIComponent(id)}`
    );
  }

  /** Returns the raw MCP tool names for the agentic stage domain/tool selector. */
  getMcpToolNames(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/api/tools/mcp-names`);
  }

  // ─── Domains ─────────────────────────────────────────────────

  getDomains(): Observable<{ id: string; name: string }[]> {
    return this.http.get<{ id: string; name: string }[]>(
      `${this.baseUrl}/api/domains`
    );
  }

  // ─── AI Generation ─────────────────────────────────────────

  generateSamplePrompt(prompt: PromptDefinition): Observable<{ samplePrompt: string }> {
    return this.http.post<{ samplePrompt: string }>(
      `${this.baseUrl}/api/prompts/generate-sample`,
      prompt
    );
  }

  // ─── Toolbox Bundles ────────────────────────────────────────

  exportToolbox(domain: string): Observable<ToolboxBundle> {
    return this.http.post<ToolboxBundle>(
      `${this.baseUrl}/api/toolbox/export/${encodeURIComponent(domain)}`,
      {}
    );
  }

  importToolbox(bundle: ToolboxBundle): Observable<ImportResult> {
    return this.http.post<ImportResult>(
      `${this.baseUrl}/api/toolbox/import`,
      bundle
    );
  }
}
