import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject } from 'rxjs';
import { environment } from '../../environments/environment';
import { GitHubAuthService } from './github-auth.service';

@Injectable({
  providedIn: 'root',
})
export class AdoAuthService {
  private readonly apiUrl = environment.apiUrl;

  private _isConnected = new BehaviorSubject<boolean>(false);
  readonly isConnected$ = this._isConnected.asObservable();

  constructor(
    private http: HttpClient,
    private githubAuth: GitHubAuthService,
  ) {
    this._checkStatus();
  }

  get isConnected(): boolean {
    return this._isConnected.value;
  }

  /**
   * Stores the provided PAT in the user's session on the backend.
   * The PAT is sent over HTTPS and never returned to the browser afterwards.
   */
  connectAdo(pat: string): void {
    this.http.post(`${this.apiUrl}/api/auth/ado/connect`, {
      sessionId: this.githubAuth.sessionId,
      pat,
    }).subscribe({
      next: () => this._isConnected.next(true),
      error: (err) => console.error('ADO connect failed', err),
    });
  }

  disconnectAdo(): void {
    this.http.delete(`${this.apiUrl}/api/auth/ado/session`, {
      params: { sessionId: this.githubAuth.sessionId },
    }).subscribe({
      next: () => this._isConnected.next(false),
      error: (err) => console.error('ADO disconnect failed', err),
    });
  }

  private _checkStatus(): void {
    this.http.get<{ connected: boolean }>(
      `${this.apiUrl}/api/auth/ado/status`,
      { params: { sessionId: this.githubAuth.sessionId } }
    ).subscribe({
      next: (res) => this._isConnected.next(res.connected),
      error: () => { /* API not up yet */ }
    });
  }
}
