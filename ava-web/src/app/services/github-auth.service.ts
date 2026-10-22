import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable, interval, Subscription } from 'rxjs';
import { environment } from '../../environments/environment';

export interface ModelOption {
  id: string;
  label: string;
  publisher: string;
}

export type GitHubConnectState = 'disconnected' | 'pending' | 'connected';

@Injectable({
  providedIn: 'root',
})
export class GitHubAuthService {
  private readonly apiUrl = environment.apiUrl;

  /** Stable session ID — generated once and kept in sessionStorage. */
  readonly sessionId: string;

  private _connectState = new BehaviorSubject<GitHubConnectState>('disconnected');
  readonly connectState$ = this._connectState.asObservable();

  private _selectedModel = new BehaviorSubject<string | null>(null);
  readonly selectedModel$ = this._selectedModel.asObservable();

  private _availableModels = new BehaviorSubject<ModelOption[]>([]);
  readonly availableModels$ = this._availableModels.asObservable();

  /** Shown to the user during the pending phase (e.g. "ABCD-1234") */
  pendingUserCode: string | null = null;
  pendingVerificationUri: string | null = null;

  private _pollSub?: Subscription;
  private _currentDeviceCode?: string;
  private _pollIntervalSecs = 5;

  constructor(private http: HttpClient) {
    this.sessionId = localStorage.getItem('ava-session-id') ?? this._newSessionId();
    this._loadModels(); // static/unauthenticated list on startup
    this._checkStatus();
  }

  // ── Public API ─────────────────────────────────────────────────────────────

  get isConnected(): boolean {
    return this._connectState.value === 'connected';
  }

  get selectedModel(): string | null {
    return this._selectedModel.value;
  }

  /**
   * Step 1 — kick off the device flow. Backend returns user_code + device_code.
   */
  startDeviceFlow(): Observable<void> {
    return new Observable(obs => {
      this.http.post<{
        userCode: string;
        deviceCode: string;
        verificationUri: string;
        expiresIn: number;
        interval: number;
      }>(`${this.apiUrl}/api/auth/github/start`, {}).subscribe({
        next: (res) => {
          this.pendingUserCode       = res.userCode;
          this.pendingVerificationUri = res.verificationUri;
          this._currentDeviceCode    = res.deviceCode;
          this._pollIntervalSecs     = res.interval ?? 5;
          this._connectState.next('pending');
          this._startPolling();
          obs.next();
          obs.complete();
        },
        error: (err) => obs.error(err),
      });
    });
  }

  selectModel(modelId: string): void {
    this.http.put(`${this.apiUrl}/api/auth/github/session`, {
      sessionId: this.sessionId,
      modelId,
    }).subscribe({
      next: () => this._selectedModel.next(modelId),
      error: (err) => console.error('selectModel failed', err),
    });
  }

  disconnect(): void {
    this._stopPolling();
    this.pendingUserCode        = null;
    this.pendingVerificationUri = null;
    this._currentDeviceCode     = undefined;
    this._selectedModel.next(null);
    this._availableModels.next([]);
    this._connectState.next('disconnected');

    this.http.delete(`${this.apiUrl}/api/auth/github/session`, {
      params: { sessionId: this.sessionId },
    }).subscribe();
  }

  // ── Private ────────────────────────────────────────────────────────────────

  private _newSessionId(): string {
    const id = crypto.randomUUID();
    localStorage.setItem('ava-session-id', id);
    return id;
  }

  private _checkStatus(): void {
    this.http.get<{ connected: boolean; selectedModel?: string }>(
      `${this.apiUrl}/api/auth/github/status`,
      { params: { sessionId: this.sessionId } }
    ).subscribe({
      next: (res) => {
        if (res.connected) {
          this._connectState.next('connected');
          if (res.selectedModel) this._selectedModel.next(res.selectedModel);
          this._loadModels(this.sessionId); // fetch live model list for this session
        }
      },
      error: () => { /* API not up yet — stay disconnected */ }
    });
  }

  private _loadModels(sessionId?: string): void {
    const params: Record<string, string> = sessionId ? { sessionId } : {};
    this.http.get<ModelOption[]>(`${this.apiUrl}/api/auth/github/models`, { params }).subscribe({
      next: (models) => this._availableModels.next(models),
      error: () => { /* non-fatal */ }
    });
  }

  private _startPolling(): void {
    this._stopPolling();
    this._pollSub = interval(this._pollIntervalSecs * 1000).subscribe(() => {
      if (!this._currentDeviceCode) { this._stopPolling(); return; }
      this.http.post<{ status: string; newInterval?: number }>(
        `${this.apiUrl}/api/auth/github/poll`,
        { deviceCode: this._currentDeviceCode, sessionId: this.sessionId }
      ).subscribe({
        next: (res) => {
          if (res.status === 'authorized') {
            this._stopPolling();
            this.pendingUserCode = null;
            this._connectState.next('connected');
            this._loadModels(this.sessionId); // fetch live model list now that we have a token
          } else if (res.status === 'expired' || res.status === 'denied') {
            this._stopPolling();
            this._connectState.next('disconnected');
          } else if (res.status === 'slow_down' && res.newInterval) {
            this._pollIntervalSecs = res.newInterval;
            this._startPolling(); // restart at new interval
          }
          // 'pending' → keep polling
        },
        error: () => { /* transient network error — keep polling */ }
      });
    });
  }

  private _stopPolling(): void {
    this._pollSub?.unsubscribe();
    this._pollSub = undefined;
  }
}
