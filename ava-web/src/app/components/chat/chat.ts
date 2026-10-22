import {
  Component, OnInit, OnDestroy, ViewChild, ElementRef, AfterViewChecked,
  ViewEncapsulation, Input, ChangeDetectorRef, HostListener, inject
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { MatInputModule } from '@angular/material/input';
import { TextFieldModule } from '@angular/cdk/text-field';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatCardModule } from '@angular/material/card';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatDividerModule } from '@angular/material/divider';
import { Subscription } from 'rxjs';
import { marked } from 'marked';
import { ChatService } from '../../services/chat';
import { GitHubAuthService, GitHubConnectState, ModelOption } from '../../services/github-auth.service';
import { AdoAuthService } from '../../services/ado-auth.service';
import { SecurityService } from '../../services/security.service';
import { AgentRunPanel } from '../agent-run-panel/agent-run-panel';
import { WorkflowBuilderPanel } from '../workflow-builder-panel/workflow-builder-panel';
import { SaveConfirmationPanel, SaveResult } from '../save-confirmation-panel/save-confirmation-panel';
import {
  ChatMessage, ChatRequest, PlanStep, StepExecution, StreamEvent, DomainDefinition,
  WelcomeData, StatusData, ToolCallEvent, RunDiagnostics, TableExport,
  SelectionBreadcrumb, RewindableStage, SelectionOption,
  WorkflowBuilderPayload, SaveConfirmationPayload, PromptDefinitionData, SaveArtifact,
  SimilarPromptMatch, WorkflowStageData, WorkflowSuggestion, DomainKb,
} from '../../models/chat.models';

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatInputModule,
    TextFieldModule,
    MatButtonModule,
    MatProgressBarModule,
    MatCardModule,
    MatSelectModule,
    MatFormFieldModule,
    MatTooltipModule,
    MatChipsModule,
    MatIconModule,
    MatDividerModule,
    AgentRunPanel,
    WorkflowBuilderPanel,
    SaveConfirmationPanel,
  ],
  templateUrl: './chat.html',
  styleUrl: './chat.scss',
  encapsulation: ViewEncapsulation.ShadowDom,
})
export class Chat implements OnInit, AfterViewChecked, OnDestroy {
  @Input() apiUrl?: string;
  @Input() appContext?: string;
  @Input() authToken?: string;

  @ViewChild('messageContainer') private messageContainer!: ElementRef<HTMLElement>;

  get isEmbedded(): boolean {
    return !!this.apiUrl;
  }

  /** Resolves the base URL for static assets (images etc.) served alongside ava-chat.js */
  get assetBase(): string {
    if (this._assetBase !== undefined) return this._assetBase;
    try {
      // Detect where ava-chat.js was loaded from using the <script> tag in the document
      const scripts = document.querySelectorAll('script[src*="ava-chat"]');
      if (scripts.length) {
        const src = (scripts[scripts.length - 1] as HTMLScriptElement).src;
        this._assetBase = src.substring(0, src.lastIndexOf('/') + 1);
        return this._assetBase;
      }
    } catch {}
    this._assetBase = '';
    return this._assetBase;
  }
  private _assetBase?: string;

  messages: ChatMessage[] = [];
  inputText = '';
  isLoading = false;
  conversationId = crypto.randomUUID();

  /** Sentinel for the "All Domains" dropdown entry — never sent to the API as a real domain. */
  readonly ALL_DOMAINS = '__all__';

  domains: DomainDefinition[] = [];
  selectedDomain: string = this.ALL_DOMAINS;
  private domainExplicitlySet = false;

  private security = inject(SecurityService);

  /** Sub-domains (e.g. studies) for the selected domain. Empty when the domain has none. */
  subDomains: DomainDefinition[] = [];
  /** Sentinel for "no specific sub-domain" — the whole domain is in scope. */
  readonly ALL_SUB_DOMAINS = '__all_sub__';
  selectedSubDomain: string = this.ALL_SUB_DOMAINS;

  get hasSubDomains(): boolean {
    return this.subDomains.length > 0;
  }

  get selectedSubDomainDef(): DomainDefinition | undefined {
    return this.subDomains.find(s => s.id === this.selectedSubDomain);
  }

  /** Sub-domain to put on the wire — the sentinel is never sent. */
  private get apiSubDomain(): string | undefined {
    return this.selectedSubDomain === this.ALL_SUB_DOMAINS ? undefined : this.selectedSubDomain;
  }

  get isAllDomains(): boolean {
    return this.selectedDomain === this.ALL_DOMAINS;
  }

  get selectedDomainDef(): DomainDefinition | undefined {
    return this.selectedSubDomainDef ?? this.domains.find(d => d.id === this.selectedDomain);
  }

  get selectedDomainDescription(): string {
    return this.selectedDomainDef?.description ?? '';
  }

  /** Domain to put on the wire — the ALL_DOMAINS sentinel is never a real backend domain. */
  private get apiDomain(): string {
    return this.isAllDomains ? (this.domains[0]?.id ?? 'ado') : this.selectedDomain;
  }

  /** Workflow suggestion cards shown after domain selection */
  workflowSuggestions: WorkflowSuggestion[] = [];
  showAllSuggestions = false;
  suggestionsLoading = false;

  // ── Theme ────────────────────────────────────────────────────────────────
  themes = [
    { id: 'ocean',    name: 'Ocean Blue',  swatch: 'linear-gradient(135deg, #0d47a1, #42a5f5)' },
    { id: 'midnight', name: 'Midnight',    swatch: 'linear-gradient(135deg, #1e1b4b, #6366f1)' },
    { id: 'forest',   name: 'Forest',      swatch: 'linear-gradient(135deg, #1b5e20, #66bb6a)' },
    { id: 'sunset',   name: 'Sunset',      swatch: 'linear-gradient(135deg, #bf360c, #ffb74d)' },
    { id: 'rose',     name: 'Rosé',        swatch: 'linear-gradient(135deg, #880e4f, #ec407a)' },
  ];
  currentTheme = this.getThemeCookie() || localStorage.getItem('ava-theme') || 'ocean';

  // ── Menu & Workflow View ─────────────────────────────────────────────────────
  menuOpen = false;
  currentView: 'chat' | 'workflows' = 'chat';
  workflowBuilderUrl: SafeResourceUrl;

  /** Breadcrumbs for completed user-select stages in the current workflow */
  breadcrumbs: SelectionBreadcrumb[] = [];

  /** Stages that support rewind (received from backend at workflow start) */
  rewindableStages: RewindableStage[] = [];

  /** Tracks the current agentRunId for rewind support */
  currentAgentRunId: string | null = null;

  /** Tracks the agentRunId of the workflow run (for breadcrumb rewind) */
  workflowRunId: string | null = null;

  /** Tracks the resumeRunId from the last `done` event, if the run was paused. */
  private pendingResumeRunId: string | null = null;

  private subscription?: Subscription;
  private shouldScrollToBottom = false;

  // ── GitHub Copilot state (bound in template) ────────────────────────────────
  githubState: GitHubConnectState = 'disconnected';
  githubUserCode: string | null = null;
  githubVerificationUri: string | null = null;
  availableModels: ModelOption[] = [];
  selectedModelId: string | null = null;

  // ── ADO PAT state (bound in template) ──────────────────────────────────────
  adoConnected = false;
  adoPatInput = '';
  adoPatVisible = false;

  // ── Domain KB status bar ────────────────────────────────────────────────────
  domainKbs: DomainKb[] = [];
  kbModalOpen = false;
  kbRefreshingId: string | null = null;

  private subs: Subscription[] = [];
  private themeChannel: BroadcastChannel | null = (typeof BroadcastChannel !== 'undefined')
    ? new BroadcastChannel('ava-theme') : null;

  constructor(
    private chatService: ChatService,
    readonly githubAuth: GitHubAuthService,
    readonly adoAuth: AdoAuthService,
    private cdr: ChangeDetectorRef,
    private hostRef: ElementRef<HTMLElement>,
    private sanitizer: DomSanitizer,
  ) {
    // Derive orchestration-ui URL from current origin (same host, port 6201)
    const loc = window.location;
    const adminUrl = `${loc.protocol}//${loc.hostname}:6201`;
    this.workflowBuilderUrl = this.sanitizer.bypassSecurityTrustResourceUrl(
      `${adminUrl}/workflow-builder?embed=true`
    );
  }

  ngOnInit(): void {
    // Inject Material Icons font via <link> (reliable in Shadow DOM, unlike @import in <style>)
    const sr = this.hostRef.nativeElement.shadowRoot;
    if (sr && !sr.querySelector('link[href*="Material+Icons"]')) {
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = 'https://fonts.googleapis.com/icon?family=Material+Icons';
      sr.prepend(link);
    }

    // Apply web component configuration before any API calls
    if (this.apiUrl) {
      this.chatService.apiUrl = this.apiUrl;
    }
    this.security.apiUrl = this.chatService.apiUrl;
    // Not awaited — chat must render immediately. Filtering kicks in once the context lands;
    // the API enforces access regardless.
    this.security.load().then(() => this.cdr.detectChanges());
    if (this.appContext) {
      this.selectedDomain = this.appContext;
    }
    if (this.authToken) {
      this.chatService.authToken = this.authToken;
    }

    // Apply saved theme
    this.applyTheme(this.currentTheme);

    // Listen for theme changes from ava-loader (same page + same-origin tabs)
    if (this.themeChannel) {
      this.themeChannel.onmessage = (e: MessageEvent) => {
        const themeId = e.data;
        if (themeId && themeId !== this.currentTheme) {
          this.currentTheme = themeId;
          localStorage.setItem('ava-theme', themeId);
          this.setThemeCookie(themeId);
          this.applyTheme(themeId);
          this.cdr.detectChanges();
        }
      };
    }

    // Cross-origin sync: check cookie when tab gains focus
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') {
        const cookieTheme = this.getThemeCookie();
        if (cookieTheme && cookieTheme !== this.currentTheme) {
          this.currentTheme = cookieTheme;
          localStorage.setItem('ava-theme', cookieTheme);
          this.applyTheme(cookieTheme);
          this.cdr.detectChanges();
        }
      }
    });

    this.chatService.getDomains().subscribe({
      next: (domains) => {
        this.domains = domains;
        this.cdr.detectChanges();
        if (this.isAllDomains) {
          this.domainKbs = [];
          return;
        }
        // Embedded hosts pin a domain via appContext — load its suggestions and KBs up front
        this.loadDomain(this.selectedDomain);
      },
      error: () => {
        this.domains = [
          { id: 'ado', name: 'Azure DevOps', description: '', owner: '', enabled: true },
          { id: 'study', name: 'Clinical Study', description: '', owner: '', enabled: true },
          { id: 'ctms', name: 'CTMS', description: '', owner: '', enabled: true },
          { id: 'support', name: 'Support', description: '', owner: '', enabled: true },
        ];
        this.cdr.detectChanges();
      }
    });

    this.subs.push(
      this.githubAuth.connectState$.subscribe(s => {
        this.githubState = s;
        this.githubUserCode       = this.githubAuth.pendingUserCode;
        this.githubVerificationUri = this.githubAuth.pendingVerificationUri;
        this.cdr.detectChanges();
      }),
      this.githubAuth.selectedModel$.subscribe(m => { this.selectedModelId = m; this.cdr.detectChanges(); }),
      this.githubAuth.availableModels$.subscribe(ms => { this.availableModels = ms; this.cdr.detectChanges(); }),
      this.adoAuth.isConnected$.subscribe(c => { this.adoConnected = c; this.cdr.detectChanges(); }),
    );

    // Listen for "new-chat" event from ava-loader panel header
    this.hostRef.nativeElement.addEventListener('new-chat', () => {
      this.resetToHome();
      this.cdr.detectChanges();
    });
  }

  ngAfterViewChecked(): void {
    if (this.shouldScrollToBottom) {
      this.scrollToBottom();
      this.shouldScrollToBottom = false;
    }
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
    this.subs.forEach(s => s.unsubscribe());
    this.themeChannel?.close();
  }

  // ── Domain ──────────────────────────────────────────────────────────────────

  onDomainChange(): void {
    this.conversationId = crypto.randomUUID();
    this.messages = [];
    this.pendingResumeRunId = null;
    this.breadcrumbs = [];
    this.rewindableStages = [];
    this.currentAgentRunId = null;
    this.loadDomain(this.selectedDomain);
  }

  /**
   * Loads suggestions + KBs for a domain, or clears them for the "All Domains" welcome screen.
   * Does not touch conversation state — callers decide whether to reset the thread.
   */
  private loadDomain(domainId: string): void {
    this.selectedDomain = domainId;
    this.showAllSuggestions = false;
    this.workflowSuggestions = [];
    this.domainKbs = [];
    this.kbModalOpen = false;
    this.subDomains = [];
    this.selectedSubDomain = this.ALL_SUB_DOMAINS;

    if (domainId === this.ALL_DOMAINS) {
      this.domainExplicitlySet = false;
      this.suggestionsLoading = false;
      this.cdr.detectChanges();
      return;
    }

    this.domainExplicitlySet = true;
    this.suggestionsLoading = true;

    this.chatService.getWorkflowSuggestions(domainId).subscribe({
      next: (suggestions) => {
        this.workflowSuggestions = suggestions;
        this.suggestionsLoading = false;
        this.cdr.detectChanges();
      },
      error: () => {
        this.suggestionsLoading = false;
        this.cdr.detectChanges();
      },
    });

    this.loadDomainKbs(domainId);
    this.loadSubDomains(domainId);
  }

  /** Loads the domain's sub-domains (studies). The API returns only ones the user can browse. */
  private loadSubDomains(domainId: string): void {
    this.security.getChildDomains(domainId).subscribe({
      next: (subs) => { this.subDomains = subs; this.cdr.detectChanges(); },
      error: () => { this.subDomains = []; },
    });
  }

  /** Starts a fresh thread when the user switches study — prior context belongs to the old scope. */
  onSubDomainChange(): void {
    this.conversationId = crypto.randomUUID();
    this.messages = [];
    this.pendingResumeRunId = null;
    this.breadcrumbs = [];
    this.rewindableStages = [];
    this.currentAgentRunId = null;
    const scopeId = this.apiSubDomain ?? this.selectedDomain;
    this.suggestionsLoading = true;
    this.chatService.getWorkflowSuggestions(scopeId).subscribe({
      next: (suggestions) => {
        this.workflowSuggestions = suggestions;
        this.suggestionsLoading = false;
        this.cdr.detectChanges();
      },
      error: () => {
        this.workflowSuggestions = [];
        this.suggestionsLoading = false;
        this.cdr.detectChanges();
      },
    });
    this.loadDomainKbs(scopeId);
    this.cdr.detectChanges();
  }

  private loadDomainKbs(domainId: string): void {
    this.chatService.getDomainKbs(domainId).subscribe({
      next: (kbs) => { this.domainKbs = kbs; this.cdr.detectChanges(); },
      error: () => { this.domainKbs = []; },
    });
  }

  openKbModal(): void {
    this.kbModalOpen = true;
  }

  closeKbModal(): void {
    this.kbModalOpen = false;
  }

  refreshKb(kb: DomainKb, event: Event): void {
    event.stopPropagation();
    this.kbRefreshingId = kb.id;
    this.cdr.detectChanges();
    this.chatService.refreshDomainKb(kb.id).subscribe({
      next: (updated) => {
        this.domainKbs = this.domainKbs.map(k => k.id === updated.id ? { ...k, ...updated } : k);
        this.kbRefreshingId = null;
        this.cdr.detectChanges();
      },
      error: () => {
        this.kbRefreshingId = null;
        this.cdr.detectChanges();
      },
    });
  }

  formatKbDate(iso: string): string {
    const d = new Date(iso);
    const now = new Date();
    const diffMs = now.getTime() - d.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    return d.toLocaleDateString();
  }

  resetToHome(): void {
    this.conversationId = crypto.randomUUID();
    this.messages = [];
    this.pendingResumeRunId = null;
    this.breadcrumbs = [];
    this.rewindableStages = [];
    this.currentAgentRunId = null;
    this.workflowRunId = null;
    this.isLoading = false;
    // Embedded hosts pin a domain via appContext — don't drop them into the domain picker
    this.loadDomain(this.appContext || this.ALL_DOMAINS);
  }

  // ── Theme ───────────────────────────────────────────────────────────────────

  setTheme(themeId: string): void {
    this.currentTheme = themeId;
    localStorage.setItem('ava-theme', themeId);
    this.setThemeCookie(themeId);
    this.applyTheme(themeId);
    // Notify ava-loader (same page + same-origin tabs)
    if (this.themeChannel) this.themeChannel.postMessage(themeId);
  }

  private applyTheme(themeId: string): void {
    this.hostRef.nativeElement.setAttribute('data-theme', themeId);
  }

  private setThemeCookie(themeId: string): void {
    document.cookie = `ava-theme=${encodeURIComponent(themeId)};path=/;max-age=31536000;SameSite=Lax`;
  }

  private getThemeCookie(): string | null {
    const match = document.cookie.match('(^|;)\\s*ava-theme=([^;]*)');
    return match ? decodeURIComponent(match[2]) : null;
  }

  // ── Menu & Workflow View ───────────────────────────────────────────────────────

  toggleMenu(event: Event): void {
    event.stopPropagation();
    this.menuOpen = !this.menuOpen;
  }

  @HostListener('document:click')
  onDocumentClick(): void {
    if (this.menuOpen) this.menuOpen = false;
  }

  @HostListener('document:keydown.escape')
  onEscapeKey(): void {
    if (this.menuOpen) this.menuOpen = false;
    if (this.kbModalOpen) this.kbModalOpen = false;
  }

  toggleWorkflowView(): void {
    this.currentView = this.currentView === 'chat' ? 'workflows' : 'chat';
    this.menuOpen = false;
  }

  /** Handle clicking a suggestion chip from the welcome card */
  sendSuggestion(text: string): void {
    this.inputText = text;
    this.send();
  }

  // ── GitHub Copilot handlers ─────────────────────────────────────────────────

  connectGitHub(): void {
    this.githubAuth.startDeviceFlow().subscribe({
      next: () => {
        this.githubUserCode       = this.githubAuth.pendingUserCode;
        this.githubVerificationUri = this.githubAuth.pendingVerificationUri;
      },
      error: (err) => console.error('GitHub device flow start failed', err),
    });
  }

  disconnectGitHub(): void {
    this.githubAuth.disconnect();
    this.selectedModelId = null;
  }

  onModelChange(modelId: string): void {
    this.githubAuth.selectModel(modelId);
  }

  openGitHubVerification(): void {
    if (this.githubVerificationUri) {
      window.open(this.githubVerificationUri, '_blank', 'noopener');
    }
  }

  // ── ADO PAT handlers ────────────────────────────────────────────────────────

  connectAdo(): void {
    const pat = this.adoPatInput.trim();
    if (!pat) return;
    this.adoAuth.connectAdo(pat);
    this.adoPatInput = '';
    this.adoPatVisible = false;
  }

  disconnectAdo(): void {
    this.adoAuth.disconnectAdo();
  }

  toggleAdoPatInput(): void {
    this.adoPatVisible = !this.adoPatVisible;
    if (!this.adoPatVisible) this.adoPatInput = '';
  }

  // ── Chat ────────────────────────────────────────────────────────────────────

  send(): void {
    const text = this.inputText.trim();
    if (!text || this.isLoading) return;

    // Auto-detect domain when user types from the home page (no conversation yet)
    const isHomePage = this.messages.length === 0;
    if (isHomePage && !this.domainExplicitlySet) {
      // Clear suggestion cards before async detection
      this.workflowSuggestions = [];
      this.showAllSuggestions = false;
      this.inputText = '';
      this.isLoading = true;

      this.chatService.detectDomain(text).subscribe({
        next: (result) => {
          if (result.domain) {
            this.selectedDomain = result.domain;
            this.loadDomainKbs(result.domain);
          }
          this.isLoading = false;
          this.sendMessage(text);
        },
        error: () => {
          this.isLoading = false;
          this.sendMessage(text);
        },
      });
      return;
    }

    // Clear suggestion cards synchronously (domain already set, no async gap)
    this.workflowSuggestions = [];
    this.showAllSuggestions = false;

    // Auto-save: when user confirms in ava-prompts domain, try to parse & save JSON
    if (this.selectedDomain === 'ava-prompts' && /^(yes|confirm|save|approve)/i.test(text)) {
      const parsed = this.tryParseWorkflowFromMessages();
      if (parsed) {
        this.inputText = '';
        this.triggerAutoSave(parsed);
        return;
      }
    }

    this.inputText = '';
    this.sendMessage(text);
  }

  /** Internal: sends a message without domain detection */
  private sendMessage(text: string): void {
    this.addMessage({ role: 'user', content: text });

    const assistantMsg = this.addMessage({
      role: 'assistant',
      content: '',
      isStreaming: true,
    });

    const request: ChatRequest = {
      message: text,
      conversationId: this.conversationId,
      context: {
        domain: this.apiDomain,
        roles: [],
        userId: this.security.userId,
        ...(this.apiSubDomain ? { subDomain: this.apiSubDomain } : {}),
      },
      ...(this.pendingResumeRunId ? { resumeRunId: this.pendingResumeRunId } : {}),
    };

    this.pendingResumeRunId = null;
    this.isLoading = true;

    this.subscription = this.chatService.streamChat(request).subscribe({
      next: (evt: StreamEvent) => {
        this.handleEvent(evt, assistantMsg);
        this.cdr.detectChanges();
      },
      error: (err) => {
        assistantMsg.content = 'An error occurred. Please try again.';
        assistantMsg.isStreaming = false;
        this.isLoading = false;
        this.cdr.detectChanges();
        console.error('Stream error', err);
      },
      complete: () => {
        assistantMsg.isStreaming = false;
        this.isLoading = false;
        this.cdr.detectChanges();
      },
    });
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  useSuggestion(text: string): void {
    this.inputText = text;
    this.send();
  }

  selectDomainChip(domainId: string): void {
    this.loadDomain(domainId);
  }

  /** Returns to the "All Domains" welcome screen without clearing the conversation. */
  backToAllDomains(): void {
    this.loadDomain(this.ALL_DOMAINS);
  }

  selectSuggestion(suggestion: WorkflowSuggestion): void {
    this.inputText = suggestion.starterPrompt;
    // Keep suggestions visible — they clear on first send()
  }

  get visibleSuggestions(): WorkflowSuggestion[] {
    return this.showAllSuggestions
      ? this.workflowSuggestions
      : this.workflowSuggestions.slice(0, 3);
  }

  getDomainIcon(domainId: string): string {
    const icons: Record<string, string> = {
      ado: 'fork_right',
      study: 'science',
      'study-pi-insight': 'medical_services',
      ctms: 'favorite',
      support: 'support_agent',
      'mon-log': 'assignment',
      'ava-prompts': 'settings',
    };
    return icons[domainId] ?? 'grid_view';
  }

  private getDomainPrompt(domainId: string): string {
    const prompts: Record<string, string> = {
      ado: 'Show me recent work items',
      study: 'Give me physician insight',
      'study-pi-insight': 'Give me physician insight',
      ctms: 'Show me clinical trial status',
      support: 'Help me with a support request',
      'mon-log': 'Show monitoring logs',
      'ava-prompts': 'Help me configure AVA',
    };
    return prompts[domainId] ?? `Help me with ${this.domains.find(d => d.id === domainId)?.name ?? domainId}`;
  }

  getFilteredOptions(msg: ChatMessage): SelectionOption[] {
    const options = msg.selectionPayload?.options ?? [];
    const filter = (msg.selectionFilter ?? '').trim().toLowerCase();
    if (!filter) return options;
    return options.filter(o => o.label.toLowerCase().includes(filter));
  }

  getOriginalIndex(msg: ChatMessage, option: SelectionOption): number {
    return msg.selectionPayload?.options.indexOf(option) ?? 0;
  }

  renderMarkdown(text: string): string {
    if (!text) return '';
    return marked.parse(text, { async: false, breaks: true, gfm: true }) as string;
  }

  /** Tracks which export is currently downloading (by queryId) to show a spinner/disable. */
  downloadingQueryId: string | null = null;

  /** Per-export error messages (keyed by queryId) shown inline under the button. */
  exportErrors: Record<string, string> = {};

  /**
   * Downloads the full CSV for a SQL-backed table export. Uses fetch() with the auth
   * header (a plain <a href> can't carry the Bearer token), then triggers an in-browser
   * Blob download of the streamed CSV.
   */
  async downloadCsv(exp: TableExport): Promise<void> {
    this.downloadingQueryId = exp.queryId;
    delete this.exportErrors[exp.queryId];
    try {
      const headers: Record<string, string> = {};
      if (this.authToken) {
        headers['Authorization'] = `Bearer ${this.authToken}`;
      }
      const base = this.apiUrl ?? this.chatService.apiUrl;
      const response = await fetch(`${base}${exp.downloadUrl}`, {
        method: 'GET',
        headers,
        credentials: 'include',
      });
      if (!response.ok) {
        // Surface a friendly message for expected cases (dataset changed / expired).
        let message = `Download failed (HTTP ${response.status})`;
        if (response.status === 409) {
          message = 'The data changed since this query ran. Please re-run the query to download current results.';
        } else if (response.status === 410) {
          message = 'This export is no longer available. Please re-run the query.';
        } else {
          try {
            const body = await response.json();
            if (body?.message) message = body.message;
          } catch { /* keep default message */ }
        }
        this.exportErrors[exp.queryId] = message;
        return;
      }
      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `ava-export-${exp.queryId.slice(0, 8)}.csv`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    } catch (err) {
      console.error('CSV download failed', err);
      this.exportErrors[exp.queryId] = 'Download failed. Please try again.';
    } finally {
      this.downloadingQueryId = null;
    }
  }

  /** Label for the CSV download button. Avoids showing a misleading capped count. */
  exportLabel(exp: TableExport): string {
    if (exp.limitRemoved) {
      // The in-chat query had a LIMIT we stripped for the export, so rowCount is the
      // capped count, not the CSV's true size. Don't show a misleading number.
      return 'Download full CSV (all rows)';
    }
    return `Download CSV (${exp.rowCount}${exp.truncated ? '+' : ''} rows)`;
  }

  hasDiagnostics(msg: ChatMessage): boolean {
    const d = msg.diagnostics;
    if (!d) return false;
    return (d.toolsCalled?.length > 0) || (d.workflowsExecuted?.length > 0) || (d.domainFilesUsed?.length > 0) || !!d.retrievalStrategy;
  }

  selectOption(agentRunId: string, selectedIndex: number): void {
    this.isLoading = true;

    // Mark the selection message as consumed so buttons become disabled
    const selMsg = this.messages.find(m => m.agentRunId === agentRunId && m.selectionPayload && !m.superseded && !m.selectionConsumed);
    if (selMsg) selMsg.selectionConsumed = true;

    this.chatService.select({
      agentRunId,
      inputType: 'select',
      selectedIndex,
    }).subscribe({
      next: (response) => {
        const resumeRequest: ChatRequest = {
          message: 'continue',
          conversationId: this.conversationId,
          context: {
            domain: this.apiDomain,
            roles: [],
            userId: this.security.userId,
            ...(this.apiSubDomain ? { subDomain: this.apiSubDomain } : {}),
          },
          resumeRunId: response.resumeRunId,
        };

        const assistantMsg = this.addMessage({
          role: 'assistant',
          content: '',
          isStreaming: true,
        });

        this.subscription = this.chatService.streamChat(resumeRequest).subscribe({
          next: (evt: StreamEvent) => {
            this.handleEvent(evt, assistantMsg);
            this.cdr.detectChanges();
          },
          error: (err) => {
            assistantMsg.content = 'An error occurred while resuming. Please try again.';
            assistantMsg.isStreaming = false;
            this.isLoading = false;
            this.cdr.detectChanges();
            console.error('Resume stream error', err);
          },
          complete: () => {
            assistantMsg.isStreaming = false;
            this.isLoading = false;
            this.cdr.detectChanges();
          },
        });
      },
      error: (err) => {
        // Undo consumed mark so user can retry
        if (selMsg) selMsg.selectionConsumed = false;
        this.isLoading = false;
        this.cdr.detectChanges();
        console.error('Selection error', err);
        this.addMessage({ role: 'system', content: 'Failed to process selection. Please try again.' });
      },
    });
  }

  rewindToStage(stageId: string): void {
    // Use the workflow run ID (not the latest run, which might be a conversational fallback)
    const runId = this.workflowRunId || this.currentAgentRunId;
    if (!runId || this.isLoading) return;

    this.isLoading = true;

    this.chatService.rewind({
      agentRunId: runId,
      targetStageId: stageId,
    }).subscribe({
      next: (response) => {
        // Mark messages after the rewind point as superseded
        for (const msg of this.messages) {
          if (msg.superseded) continue;
          if (msg.selectionPayload?.stageId === stageId) {
            const idx = this.messages.indexOf(msg);
            for (let i = idx; i < this.messages.length; i++) {
              this.messages[i].superseded = true;
            }
            break;
          }
        }

        // Remove breadcrumbs from the rewound stage onward
        const stageIdx = this.breadcrumbs.findIndex(b => b.stageId === stageId);
        if (stageIdx >= 0) {
          this.breadcrumbs = this.breadcrumbs.slice(0, stageIdx);
        }

        // Add a divider message
        const rawLabel = this.rewindableStages.find(s => s.stageId === stageId)?.label ?? stageId;
        const breadcrumbLabel = rawLabel.replace(/\s*\{\{[^}]+\}\}/g, '').replace(/:\s*$/, '').trim();
        this.addMessage({
          role: 'system',
          content: `↩ Selection changed — rewound to ${breadcrumbLabel}`,
        });

        // Resume the workflow
        const resumeRequest: ChatRequest = {
          message: 'continue',
          conversationId: this.conversationId,
          context: {
            domain: this.apiDomain,
            roles: [],
            userId: this.security.userId,
            ...(this.apiSubDomain ? { subDomain: this.apiSubDomain } : {}),
          },
          resumeRunId: response.resumeRunId,
        };

        const assistantMsg = this.addMessage({
          role: 'assistant',
          content: '',
          isStreaming: true,
        });

        this.subscription = this.chatService.streamChat(resumeRequest).subscribe({
          next: (evt: StreamEvent) => this.handleEvent(evt, assistantMsg),
          error: (err) => {
            assistantMsg.content = 'An error occurred while resuming. Please try again.';
            assistantMsg.isStreaming = false;
            this.isLoading = false;
            console.error('Rewind resume error', err);
          },
          complete: () => {
            assistantMsg.isStreaming = false;
            this.isLoading = false;
          },
        });
      },
      error: (err) => {
        this.isLoading = false;
        console.error('Rewind error', err);
        this.addMessage({ role: 'system', content: 'Failed to rewind. Please try again.' });
      },
    });
  }

  private handleEvent(evt: StreamEvent, msg: ChatMessage): void {
    switch (evt.type) {
      case 'run_started': {
        const d = evt.data as { agentRunId: string };
        msg.agentRunId = d.agentRunId;
        this.currentAgentRunId = d.agentRunId;
        break;
      }

      case 'rewindable_stages': {
        const d = evt.data as { stages: RewindableStage[] };
        this.rewindableStages = d.stages;
        // Remember which run is the workflow run (for breadcrumb rewind)
        this.workflowRunId = this.currentAgentRunId;
        break;
      }

      case 'selection_summary': {
        const d = evt.data as SelectionBreadcrumb;
        // Remove any existing breadcrumb for this stageId (in case of rewind + re-select)
        this.breadcrumbs = this.breadcrumbs.filter(b => b.stageId !== d.stageId);
        this.breadcrumbs.push(d);
        break;
      }

      case 'plan':
        msg.plan = evt.data as PlanStep[];
        break;

      case 'step_done':
        if (!msg.steps) msg.steps = [];
        msg.steps.push(evt.data as StepExecution);
        break;

      case 'token':
        // APPEND tokens (streaming) — not replace
        msg.content += evt.data as string;
        msg.statusText = undefined; // clear status indicator on first token
        this.shouldScrollToBottom = true;
        break;

      case 'welcome': {
        // Welcome message suppressed — the empty state with domain chips serves this purpose
        break;
      }

      case 'status': {
        const status = evt.data as StatusData;
        const statusLabels: Record<string, string> = {
          thinking: 'Thinking...',
          querying: 'Running SQL query...',
          searching: 'Searching documents...',
          inspecting: 'Inspecting schema...',
          responding: 'Responding...',
          calling_tool: `Calling ${status.detail ?? 'tool'}...`,
          starting_workflow: 'Starting workflow...',
        };
        msg.statusText = statusLabels[status.state] ?? `${status.state}...`;
        this.shouldScrollToBottom = true;
        break;
      }

      case 'tool_call': {
        const tc = evt.data as { tool: string; params?: string };
        if (!msg.toolCalls) msg.toolCalls = [];
        msg.toolCalls.push({ tool: tc.tool, params: tc.params });
        this.shouldScrollToBottom = true;
        break;
      }

      case 'tool_result': {
        const tr = evt.data as { tool: string; summary?: string };
        if (msg.toolCalls) {
          const last = [...msg.toolCalls].reverse().find((tc: ToolCallEvent) => tc.tool === tr.tool && !tc.result);
          if (last) last.result = tr.summary ?? 'OK';
        }
        this.shouldScrollToBottom = true;
        break;
      }

      case 'tool_error': {
        const te = evt.data as { tool: string; error: string; message: string };
        if (msg.toolCalls) {
          const last = [...msg.toolCalls].reverse().find((tc: ToolCallEvent) => tc.tool === te.tool && !tc.result && !tc.error);
          if (last) { last.error = te.message; last.isError = true; }
        }
        this.shouldScrollToBottom = true;
        break;
      }

      case 'undo': {
        const undoData = evt.data as { restoredMessageCount: number; lastUserMessage?: string };
        // Remove last assistant + user message pair from UI
        if (this.messages.length >= 2) {
          this.messages.splice(this.messages.length - 2, 2);
        }
        // Re-populate input with the restored user message
        if (undoData.lastUserMessage) {
          this.inputText = undoData.lastUserMessage;
        }
        msg.isStreaming = false;
        this.isLoading = false;
        // Remove the placeholder assistant message we added for this undo response
        const undoMsgIdx = this.messages.indexOf(msg);
        if (undoMsgIdx >= 0) this.messages.splice(undoMsgIdx, 1);
        break;
      }

      case 'user_input_required': {
        const d = evt.data as { agentRunId: string; inputType: string; payload?: any };
        if (d.inputType === 'select' && d.payload) {
          this.addMessage({
            role: 'system',
            content: d.payload.prompt || 'Please select an option:',
            agentRunId: d.agentRunId,
            selectionPayload: d.payload,
            inputType: d.inputType,
          });
        } else {
          this.addMessage({ role: 'system', content: 'Please review the above and reply to confirm or describe changes.' });
        }
        this.isLoading = false;
        break;
      }

      case 'confirmation_required': {
        const d = evt.data as { agentRunId: string; prompt?: string };
        this.addMessage({ role: 'system', content: d.prompt ?? 'Please confirm to proceed.' });
        this.isLoading = false;
        break;
      }

      case 'workflow_builder': {
        const payload = evt.data as WorkflowBuilderPayload;
        this.addMessage({
          role: 'system',
          content: '',
          workflowBuilderPayload: payload,
          agentRunId: this.currentAgentRunId ?? undefined,
        });
        this.isLoading = false;
        this.shouldScrollToBottom = true;
        break;
      }

      case 'save_confirmation': {
        const payload = evt.data as SaveConfirmationPayload;
        this.addMessage({
          role: 'system',
          content: '',
          saveConfirmationPayload: payload,
          agentRunId: this.currentAgentRunId ?? undefined,
        });
        this.isLoading = false;
        this.shouldScrollToBottom = true;
        break;
      }

      case 'done': {
        const d = evt.data as { agentRunId: string; resumeRunId?: string; diagnostics?: RunDiagnostics; tableExports?: TableExport[] };
        if (d.resumeRunId) {
          this.pendingResumeRunId = d.resumeRunId;
        } else {
          // Flow completed — mark all active selection messages as consumed
          for (const m of this.messages) {
            if (m.selectionPayload && !m.superseded && !m.selectionConsumed) {
              m.selectionConsumed = true;
            }
          }

          // Auto-show workflow builder when ava-prompts domain completes with JSON
          if (this.selectedDomain === 'ava-prompts') {
            this.tryShowWorkflowBuilder();
          }
        }
        // Attach diagnostics to the current assistant message
        if (d.diagnostics) {
          msg.diagnostics = d.diagnostics;
        }
        // Attach reproducible CSV export links (SQL-backed) to the message
        if (d.tableExports && d.tableExports.length > 0) {
          msg.tableExports = d.tableExports;
        }
        msg.statusText = undefined;
        break;
      }

      case 'error':
        msg.content = `Error: ${evt.data}`;
        msg.isStreaming = false;
        msg.statusText = undefined;
        this.isLoading = false;
        break;
    }
  }

  private addMessage(partial: Partial<ChatMessage> & Pick<ChatMessage, 'role' | 'content'>): ChatMessage {
    const msg: ChatMessage = {
      id: crypto.randomUUID(),
      timestamp: new Date(),
      ...partial,
    };
    this.messages.push(msg);
    this.shouldScrollToBottom = true;
    return msg;
  }

  // ── Auto-save: parse JSON from AI output and trigger save ──────────────────

  /**
   * Scans recent assistant messages for JSON code blocks containing
   * a prompts.json definition. Returns the parsed prompt if found.
   * Handles multiple JSON formats the AI might produce (stages vs flow,
   * domain vs domainId, description vs goal, etc.).
   */
  private tryParseWorkflowFromMessages(): PromptDefinitionData | null {
    // Look through recent assistant messages (newest first)
    const assistantMsgs = [...this.messages]
      .filter(m => m.role === 'assistant' && m.content && !m.superseded)
      .reverse();

    for (const msg of assistantMsgs) {
      const jsonBlocks = this.extractJsonBlocks(msg.content);

      // First pass: try to find a block with stages (ideal case)
      for (const block of jsonBlocks) {
        try {
          let parsed = JSON.parse(block);
          if (Array.isArray(parsed)) {
            if (parsed.length === 0) continue;
            parsed = parsed[0];
          }
          const normalized = this.normalizePromptDefinition(parsed, true);
          if (normalized) return normalized;
        } catch { /* not valid JSON, skip */ }
      }

      // Second pass: accept blocks without stages (prompt-only definitions)
      // The builder panel will let users add stages interactively
      // Prefer blocks with promptId/prompt fields over generic domain entries
      let bestCandidate: PromptDefinitionData | null = null;
      for (const block of jsonBlocks) {
        try {
          let parsed = JSON.parse(block);
          if (Array.isArray(parsed)) {
            if (parsed.length === 0) continue;
            parsed = parsed[0];
          }
          const normalized = this.normalizePromptDefinition(parsed, false);
          if (normalized) {
            // Prefer entries that look most like prompts
            const hasPromptId = 'promptId' in parsed || 'prompt' in parsed;
            if (hasPromptId) return normalized;
            if (!bestCandidate) bestCandidate = normalized;
          }
        } catch { /* not valid JSON, skip */ }
      }
      if (bestCandidate) return bestCandidate;
    }
    return null;
  }

  /**
   * Normalizes various AI output formats into PromptDefinitionData.
   * Handles: stages/flow/steps, id/name/promptId, domain/domainId,
   * description/goal/displayName/workflowGoal, etc.
   * When requireStages=false, accepts entries without stages (builder lets users add them).
   */
  private normalizePromptDefinition(parsed: Record<string, unknown>, requireStages: boolean): PromptDefinitionData | null {
    if (!parsed || typeof parsed !== 'object') return null;

    // Accept various id field names
    const id = (parsed['id'] ?? parsed['name'] ?? parsed['promptId'] ?? parsed['domainId']) as string;
    if (!id) return null;

    // Accept 'stages', 'flow', or 'steps' as the stage array
    const rawStages = (parsed['stages'] ?? parsed['flow'] ?? parsed['steps']) as unknown[];
    const hasStages = Array.isArray(rawStages) && rawStages.length > 0;

    if (requireStages && !hasStages) return null;

    // For non-stage blocks, require it to look like a prompt/workflow definition
    // (not a tool, template, or appsettings entry)
    if (!hasStages) {
      const hasPromptIndicator = parsed['prompt'] || parsed['displayName'] || parsed['workflowGoal']
        || parsed['userFlowDescription'] || parsed['description'] || parsed['goal'];
      const isToolEntry = parsed['endpoint'] || parsed['httpMethod'] || parsed['serviceName'];
      const isTemplateEntry = parsed['template'] || parsed['templateId'];
      const isSettingsEntry = parsed['Services'] || parsed['baseUrl'];
      if (!hasPromptIndicator || isToolEntry || isTemplateEntry || isSettingsEntry) return null;
    }

    // Normalize stage items
    let normalizedStages: WorkflowStageData[] = [];
    if (hasStages) {
      normalizedStages = (rawStages as Record<string, unknown>[]).map((s, i) => ({
        stageId: (s['stageId'] ?? s['step'] ?? s['name'] ?? `stage-${i + 1}`) as string,
        type: (s['type'] ?? 'ai-analyze') as string,
        prompt: (s['prompt'] ?? s['content'] ?? s['description'] ?? '') as string,
        toolId: (s['toolId'] ?? s['tool'] ?? s['submitTool']) as string | undefined,
        templateRef: (s['templateRef'] ?? s['template']) as string | undefined,
        required: s['required'] as string[] | undefined,
        parameters: s['parameters'] as Record<string, string> | undefined,
        outputMap: s['outputMap'] as Record<string, string> | undefined,
        selectPrompt: s['selectPrompt'] as string | undefined,
        optionsFrom: (s['optionsFrom'] ?? s['optionLabel']) as string | undefined,
        confirmPrompt: (s['confirmPrompt'] ?? s['previewTemplate']) as string | undefined,
        ...(s['condition'] ? { condition: s['condition'] } : {}),
        ...(s['fields'] ? { fields: s['fields'] } : {}),
        ...(s['options'] ? { options: s['options'] } : {}),
        ...(s['token'] ? { token: s['token'] } : {}),
        ...(s['prefill'] ? { prefill: s['prefill'] } : {}),
        ...(s['values'] ? { values: s['values'] } : {}),
      }));
    }

    // Normalize context tags from various formats
    let contextTags: string[] = [];
    if (Array.isArray(parsed['contextTags'])) {
      contextTags = parsed['contextTags'] as string[];
    } else if (typeof parsed['contextTags'] === 'string') {
      contextTags = (parsed['contextTags'] as string).split(',').map(t => t.trim());
    }

    return {
      id,
      domain: (parsed['domain'] ?? parsed['domainId'] ?? '') as string,
      description: (parsed['description'] ?? parsed['goal'] ?? parsed['displayName']
        ?? parsed['workflowGoal'] ?? parsed['userFlowDescription'] ?? '') as string,
      contextTags,
      systemPrompt: (parsed['systemPrompt'] ?? parsed['prompt'] ?? '') as string,
      piiCheck: (parsed['piiCheck'] ?? false) as boolean,
      stages: normalizedStages,
    };
  }

  /**
   * Extracts JSON objects/arrays from markdown content.
   * Handles: ```json ... ```, plain ``` ... ```, and unfenced JSON after headers.
   */
  private extractJsonBlocks(content: string): string[] {
    const blocks: string[] = [];

    // 1. Match fenced code blocks (with or without 'json' language tag)
    const fencedRegex = /```(?:json)?\s*\n([\s\S]*?)```/gi;
    let match;
    while ((match = fencedRegex.exec(content)) !== null) {
      const block = match[1].trim();
      if (block.startsWith('{') || block.startsWith('[')) {
        blocks.push(block);
      }
    }

    // 2. If no fenced blocks found, try to extract unfenced JSON
    //    Find lines starting with { or [ and try to parse from there
    if (blocks.length === 0) {
      const lines = content.split('\n');
      for (let i = 0; i < lines.length; i++) {
        const trimmed = lines[i].trim();
        if (trimmed === '{' || trimmed === '[' || trimmed.startsWith('{\n') || trimmed.startsWith('[\n')
            || trimmed.startsWith('{\"') || trimmed.startsWith('[{') || trimmed.startsWith('[ {')
            || trimmed.startsWith('{ \"') || trimmed.startsWith('[\"') || trimmed.startsWith('[\n')) {
          // Try progressively larger slices until we get valid JSON
          for (let end = i + 1; end <= Math.min(i + 200, lines.length); end++) {
            const candidate = lines.slice(i, end).join('\n').trim();
            const lastChar = candidate[candidate.length - 1];
            // Only try parsing if it ends with } or ]
            if (lastChar === '}' || lastChar === ']') {
              try {
                JSON.parse(candidate);
                blocks.push(candidate);
                i = end - 1; // skip past this block
                break;
              } catch { /* keep trying with more lines */ }
            }
          }
        }
      }
    }

    return blocks;
  }

  /**
   * Called when an ava-prompts workflow completes. Parses the AI-generated
   * JSON and shows the inline WorkflowBuilderPanel pre-filled with the data.
   */
  private tryShowWorkflowBuilder(): void {
    const parsed = this.tryParseWorkflowFromMessages();
    if (!parsed) return;

    // Search for similar prompts to offer reuse/clone options
    this.chatService.searchPrompts(parsed.description || parsed.id).subscribe({
      next: (result) => {
        this.showBuilderPanel(parsed, result.matches ?? []);
      },
      error: () => {
        // Show builder even if similarity search fails
        this.showBuilderPanel(parsed, []);
      },
    });
  }

  private showBuilderPanel(prompt: PromptDefinitionData, similarPrompts: SimilarPromptMatch[]): void {
    const payload: WorkflowBuilderPayload = {
      prompt,
      similarPrompts,
      availableDomains: this.domains.map(d => d.id),
    };

    this.addMessage({
      role: 'system',
      content: '',
      workflowBuilderPayload: payload,
    });

    this.shouldScrollToBottom = true;
    this.cdr.detectChanges();
  }

  /**
   * Triggers auto-save by showing the save confirmation panel
   * with the parsed workflow definition.
   */
  private triggerAutoSave(prompt: PromptDefinitionData): void {
    this.addMessage({ role: 'user', content: 'Yes, save the workflow.' });

    const artifacts: SaveArtifact[] = [];

    artifacts.push({
      type: 'prompt',
      id: prompt.id,
      action: 'create',
      data: prompt,
    });

    // Check if domain exists
    const domainExists = this.domains.some(d => d.id === prompt.domain);
    if (!domainExists && prompt.domain) {
      artifacts.push({
        type: 'domain',
        id: prompt.domain,
        action: 'create',
        data: {
          id: prompt.domain,
          name: prompt.domain.split('-').map((w: string) => w.charAt(0).toUpperCase() + w.slice(1)).join(' '),
          description: `Domain for ${prompt.description}`,
          owner: 'platform-team',
          enabled: true,
        },
      });
    }

    const savePayload: SaveConfirmationPayload = {
      artifacts,
      agentRunId: this.currentAgentRunId ?? '',
    };

    this.addMessage({
      role: 'system',
      content: '',
      saveConfirmationPayload: savePayload,
    });

    this.shouldScrollToBottom = true;
    this.cdr.detectChanges();
  }

  // ── Workflow Builder Panel handlers ─────────────────────────────────────────

  /**
   * Called when the user completes the inline workflow builder form.
   * Transitions to a save confirmation panel showing all artifacts.
   */
  onWorkflowBuilderSubmit(prompt: PromptDefinitionData, sourceMsg: ChatMessage): void {
    // Mark builder message as consumed
    sourceMsg.workflowBuilderPayload = undefined;

    // Build artifact list for the save confirmation
    const artifacts: SaveArtifact[] = [];

    // Always include the prompt itself
    artifacts.push({
      type: 'prompt',
      id: prompt.id,
      action: 'create',
      data: prompt,
    });

    // Check if domain exists
    const domainExists = this.domains.some(d => d.id === prompt.domain);
    if (!domainExists && prompt.domain) {
      artifacts.push({
        type: 'domain',
        id: prompt.domain,
        action: 'create',
        data: {
          id: prompt.domain,
          name: prompt.domain.split('-').map(w => w.charAt(0).toUpperCase() + w.slice(1)).join(' '),
          description: `Domain for ${prompt.description}`,
          owner: 'platform-team',
          enabled: true,
        },
      });
    }

    // Add a save confirmation message
    const savePayload: SaveConfirmationPayload = {
      artifacts,
      agentRunId: sourceMsg.agentRunId ?? '',
    };

    this.addMessage({
      role: 'system',
      content: '',
      saveConfirmationPayload: savePayload,
    });

    this.shouldScrollToBottom = true;
    this.cdr.detectChanges();
  }

  /**
   * Called when the user cancels the workflow builder.
   */
  onWorkflowBuilderCancel(sourceMsg: ChatMessage): void {
    sourceMsg.workflowBuilderPayload = undefined;
    this.addMessage({ role: 'system', content: 'Workflow builder cancelled.' });
    this.cdr.detectChanges();
  }

  /**
   * Called when the user clones a similar prompt in the builder.
   */
  onWorkflowBuilderClone(cloned: PromptDefinitionData): void {
    this.addMessage({
      role: 'system',
      content: `Cloned workflow '${cloned.id}' as starting point. Edit the form and save when ready.`,
    });
    this.cdr.detectChanges();
  }

  /**
   * Called when save completes (success or failure).
   */
  onSaveComplete(result: SaveResult, sourceMsg: ChatMessage): void {
    sourceMsg.saveConsumed = true;
    if (result.success) {
      // Refresh domains in case a new one was created
      this.chatService.getDomains().subscribe({
        next: (domains) => { this.domains = domains; this.cdr.detectChanges(); },
        error: () => {},
      });

      const savedList = result.saved.map(a => `${a.type} '${a.id}'`).join(', ');
      this.addMessage({
        role: 'assistant',
        content: `✅ **All artifacts saved successfully!**\n\nSaved: ${savedList}\n\nThe workflow is now active and can be triggered from the chat. Try it out by typing one of the context tags you defined!`,
      });
    } else {
      const failedList = result.errors.map(e => `${e.artifact.type} '${e.artifact.id}': ${e.error}`).join('\n');
      this.addMessage({
        role: 'assistant',
        content: `⚠️ Some artifacts failed to save:\n\n${failedList}\n\nYou can try again or manually add the failed entries.`,
      });
    }
    this.cdr.detectChanges();
  }

  /**
   * Called when user cancels the save confirmation.
   */
  onSaveCancel(sourceMsg: ChatMessage): void {
    sourceMsg.saveConfirmationPayload = undefined;
    this.addMessage({ role: 'system', content: 'Save cancelled. You can still edit and re-submit from the builder.' });
    this.cdr.detectChanges();
  }

  private scrollToBottom(): void {
    try {
      const el = this.messageContainer.nativeElement;
      el.scrollTop = el.scrollHeight;
    } catch { /* ignore */ }
  }
}
