import { Component, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { SecurityService } from '../../services/security.service';

@Component({
  selector: 'app-not-authorized',
  standalone: true,
  imports: [MatIconModule],
  template: `
    <div class="not-authorized">
      <mat-icon class="lock-icon">lock</mat-icon>
      <h1>Not Authorized</h1>
      <p>You don't have access to this area of AVA Orchestration.</p>
      @if (security.user?.userId) {
        <p class="user-hint">Signed in as {{ security.user?.displayName || security.user?.userId }}</p>
      }
      <p class="help">Contact your CDS administrator to request the required module access.</p>
    </div>
  `,
  styles: [`
    .not-authorized {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      height: 100%;
      min-height: 60vh;
      text-align: center;
      padding: 2rem;
    }
    .lock-icon {
      font-size: 64px;
      width: 64px;
      height: 64px;
      opacity: 0.4;
      margin-bottom: 1rem;
    }
    h1 { margin: 0 0 0.5rem; font-size: 1.5rem; }
    p { margin: 0.25rem 0; opacity: 0.75; }
    .user-hint { font-size: 0.875rem; opacity: 0.6; }
    .help { margin-top: 1rem; font-size: 0.875rem; }
  `],
})
export class NotAuthorized {
  security = inject(SecurityService);
}
