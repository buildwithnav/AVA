import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, Subject, map } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  ChatRequest, StreamEvent, ChatMessage, PlanStep, StepExecution,
  SelectRequest, DomainDefinition, PromptDefinitionData, DomainDefinitionData,
  SimilarPromptMatch, WorkflowSuggestion, DomainKb,
} from '../models/chat.models';
import { GitHubAuthService } from './github-auth.service';

@Injectable({
  providedIn: 'root',
})
export class ChatService {
  apiUrl = environment.apiUrl;
  authToken: string | null = null;

  constructor(
    private http: HttpClient,
    private githubAuth: GitHubAuthService,
  ) {}

  /**
   * Sends a chat message and returns a stream of parsed SSE events.
   * Uses the Fetch API directly since Angular's HttpClient doesn't support
   * streaming SSE responses natively.
   * Automatically injects the current sessionId into every request.
   */
  streamChat(request: ChatRequest): Observable<StreamEvent> {
    const subject = new Subject<StreamEvent>();
    const body: ChatRequest = { ...request, sessionId: this.githubAuth.sessionId };

    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      'Accept': 'text/event-stream',
    };
    if (this.authToken) {
      headers['Authorization'] = `Bearer ${this.authToken}`;
    }

    fetch(`${this.apiUrl}/api/chat`, {
      method: 'POST',
      headers,
      credentials: 'include',
      body: JSON.stringify(body),
    })
      .then(async (response) => {
        if (!response.ok || !response.body) {
          subject.error(new Error(`HTTP ${response.status}`));
          return;
        }
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });

          // SSE lines end with \n\n
          const parts = buffer.split('\n\n');
          buffer = parts.pop() ?? '';

          for (const part of parts) {
            const line = part.trim();
            if (line.startsWith('data: ')) {
              try {
                const json = line.slice(6);
                const evt = JSON.parse(json) as StreamEvent;
                subject.next(evt);
                if (evt.type === 'done' || evt.type === 'error') {
                  subject.complete();
                  return;
                }
              } catch {
                // ignore malformed lines
              }
            }
          }
        }
        subject.complete();
      })
      .catch((err) => subject.error(err));

    return subject.asObservable();
  }

  /**
   * POST /api/chat/select
   * Merges a user selection into a paused workflow run.
   * Returns { resumeRunId } on success.
   */
  select(request: SelectRequest): Observable<{ resumeRunId: string }> {
    return this.http.post<{ resumeRunId: string }>(
      `${this.apiUrl}/api/chat/select`, request, { withCredentials: true }
    );
  }

  /**
   * POST /api/chat/rewind
   * Rewinds a paused workflow to a previous user-select stage.
   * Returns { resumeRunId, revision } so the client can resume.
   */
  rewind(request: { agentRunId: string; targetStageId: string }): Observable<{ resumeRunId: string; revision: number }> {
    return this.http.post<{ resumeRunId: string; revision: number }>(
      `${this.apiUrl}/api/chat/rewind`, request
    );
  }

  /**
   * GET /api/domains
   * Returns all enabled domains for the domain selector.
   */
  getDomains(): Observable<DomainDefinition[]> {
    return this.http.get<DomainDefinition[]>(`${this.apiUrl}/api/domains`, { withCredentials: true });
  }

  /**
   * GET /api/prompts/domain/{domain}
   * Returns workflow suggestions for a given domain, mapped to lightweight cards.
   */
  getWorkflowSuggestions(domain: string): Observable<WorkflowSuggestion[]> {
    return this.http.get<{ count: number; prompts: PromptDefinitionData[] }>(
      `${this.apiUrl}/api/prompts/domain/${encodeURIComponent(domain)}`,
      { withCredentials: true },
    ).pipe(
      map(res => res.prompts.map(p => ({
        id: p.id,
        description: p.description,
        starterPrompt: (p.samplePrompt && p.samplePrompt.trim()) || p.description,
        icon: this.getPromptIcon(p),
      }))),
    );
  }

  private getPromptIcon(prompt: PromptDefinitionData): string {
    const id = prompt.id.toLowerCase();
    if (id.includes('create') || id.includes('generate') || id.includes('build')) return 'add_circle';
    if (id.includes('query') || id.includes('search')) return 'search';
    if (id.includes('update')) return 'edit';
    if (id.includes('sprint')) return 'directions_run';
    if (id.includes('pipeline')) return 'linear_scale';
    if (id.includes('pull-request')) return 'fork_right';
    if (id.includes('backlog')) return 'list';
    if (id.includes('analysis') || id.includes('report') || id.includes('drilldown')) return 'bar_chart';
    if (id.includes('monitor') || id.includes('log')) return 'assignment';
    if (id.includes('study') || id.includes('trial')) return 'science';
    return 'bolt';
  }

  /**
   * POST /api/chat/detect-domain
   * Detects the best domain for a user message using semantic/keyword search.
   */
  detectDomain(message: string): Observable<{ domain: string | null; confidence: number; promptId: string | null }> {
    return this.http.post<{ domain: string | null; confidence: number; promptId: string | null }>(
      `${this.apiUrl}/api/chat/detect-domain`, { message }
    );
  }

  /**
   * Get a specific agent run.
   */
  getRun(runId: string): Observable<unknown> {
    return this.http.get(`${this.apiUrl}/api/chat/runs/${runId}`, { withCredentials: true });
  }

  // ── Prompt/Template/Domain CRUD (for auto-save flow) ──────────────────────

  /**
   * POST /api/prompts/search — Search for similar existing prompts.
   */
  searchPrompts(query: string, domain?: string, topK = 5): Observable<{ count: number; matches: SimilarPromptMatch[] }> {
    return this.http.post<{ count: number; matches: SimilarPromptMatch[] }>(
      `${this.apiUrl}/api/prompts/search`,
      { query, domain, topK },
      { withCredentials: true },
    );
  }

  /**
   * GET /api/prompts — List all prompt definitions.
   */
  getPrompts(): Observable<{ count: number; prompts: PromptDefinitionData[] }> {
    return this.http.get<{ count: number; prompts: PromptDefinitionData[] }>(
      `${this.apiUrl}/api/prompts`,
      { withCredentials: true },
    );
  }

  /**
   * POST /api/prompts — Create a new prompt definition.
   */
  createPrompt(prompt: PromptDefinitionData): Observable<{ prompt: PromptDefinitionData; warnings: string[] }> {
    return this.http.post<{ prompt: PromptDefinitionData; warnings: string[] }>(
      `${this.apiUrl}/api/prompts`,
      prompt,
      { withCredentials: true },
    );
  }

  /**
   * PUT /api/prompts/{id} — Update an existing prompt definition.
   */
  updatePrompt(id: string, prompt: PromptDefinitionData): Observable<{ prompt: PromptDefinitionData; warnings: string[] }> {
    return this.http.put<{ prompt: PromptDefinitionData; warnings: string[] }>(
      `${this.apiUrl}/api/prompts/${encodeURIComponent(id)}`,
      prompt,
      { withCredentials: true },
    );
  }

  /**
   * GET /api/templates — List all template definitions.
   */
  getTemplates(): Observable<{ count: number; templates: { id: string; description: string }[] }> {
    return this.http.get<{ count: number; templates: { id: string; description: string }[] }>(
      `${this.apiUrl}/api/templates`,
      { withCredentials: true },
    );
  }

  /**
   * POST /api/templates — Create a new template definition.
   */
  createTemplate(template: unknown): Observable<unknown> {
    return this.http.post(
      `${this.apiUrl}/api/templates`,
      template,
      { withCredentials: true },
    );
  }

  /**
   * PUT /api/templates/{id} — Update an existing template.
   */
  updateTemplate(id: string, template: unknown): Observable<unknown> {
    return this.http.put(
      `${this.apiUrl}/api/templates/${encodeURIComponent(id)}`,
      template,
      { withCredentials: true },
    );
  }

  /**
   * POST /api/domains — Create a new domain definition.
   */
  createDomain(domain: DomainDefinitionData): Observable<DomainDefinitionData> {
    return this.http.post<DomainDefinitionData>(
      `${this.apiUrl}/api/domains`,
      domain,
      { withCredentials: true },
    );
  }

  /**
   * PUT /api/domains/{id} — Update an existing domain.
   */
  updateDomain(id: string, domain: DomainDefinitionData): Observable<DomainDefinitionData> {
    return this.http.put<DomainDefinitionData>(
      `${this.apiUrl}/api/domains/${encodeURIComponent(id)}`,
      domain,
      { withCredentials: true },
    );
  }

  // ── Domain Knowledge Bases ─────────────────────────────────────────────────

  /**
   * GET /api/kb/domain/{domainId} — List domain KBs for the KB status bar.
   * Returns empty array if none configured so the bar stays hidden.
   */
  getDomainKbs(domainId: string): Observable<DomainKb[]> {
    return this.http.get<DomainKb[]>(
      `${this.apiUrl}/api/kb/domain/${encodeURIComponent(domainId)}`,
      { withCredentials: true },
    );
  }

  /**
   * POST /api/kb/{id}/refresh — Re-fetch KB data from its source tool endpoint.
   */
  refreshDomainKb(kbId: string): Observable<DomainKb> {
    return this.http.post<DomainKb>(
      `${this.apiUrl}/api/kb/${encodeURIComponent(kbId)}/refresh`,
      {},
      { withCredentials: true },
    );
  }
}
