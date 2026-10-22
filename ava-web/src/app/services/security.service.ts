import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, firstValueFrom, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import {
  Module, ModuleAccess, SecurityContext, DomainAccessInfo, CurrentUser, DomainDefinition,
} from '../models/chat.models';

/**
 * Security state for the embedded chat element.
 *
 * Unlike the orchestration UI this has no router to guard — its job is to supply the real
 * user ID for chat requests and to filter the domain/sub-domain pickers. The API enforces
 * access independently; this only keeps the UI from offering things that would be rejected.
 */
@Injectable({ providedIn: 'root' })
export class SecurityService {
  private http = inject(HttpClient);

  /** Set by the Chat component from its apiUrl input before load() is called. */
  apiUrl = '';

  modules: Module[] = [];
  user: CurrentUser | null = null;
  isSuperUser = false;
  domains: DomainAccessInfo[] = [];
  loaded = false;

  async load(): Promise<void> {
    const context = await firstValueFrom(
      this.http
        .get<SecurityContext>(`${this.apiUrl}/api/security/context`, { withCredentials: true })
        .pipe(catchError(() => of(null))),
    );

    if (!context) {
      // Chat still works — the API is the enforcement point. We just can't pre-filter.
      console.warn('[AVA] Security context unavailable; UI filtering disabled.');
      this.loaded = true;
      return;
    }

    this.modules = (context.modules ?? []).map((m: any) => ({
      moduleKey: m.moduleKey ?? m.ModuleKey ?? '',
      description: m.description ?? m.Description ?? '',
      browse: !!(m.browse ?? m.Browse),
      update: !!(m.update ?? m.Update),
      insert: !!(m.insert ?? m.Insert),
      delete: !!(m.delete ?? m.Delete),
    }));
    this.user = context.user;
    this.isSuperUser = context.isSuperUser;
    this.domains = context.domains ?? [];
    this.loaded = true;
  }

  /** User ID to stamp on chat requests. Falls back to 'user' when identity is unresolved. */
  get userId(): string {
    return this.user?.userId || 'user';
  }

  getDomainAccess(domainId: string): DomainAccessInfo | undefined {
    return this.domains.find(d => d.domainId?.toLowerCase() === domainId?.toLowerCase());
  }

  hasDomainAccess(domainId: string, access: ModuleAccess = ModuleAccess.BROWSE): boolean {
    if (!this.loaded || this.isSuperUser || this.domains.length === 0) return true;
    const info = this.getDomainAccess(domainId);
    return info ? info[access] : false;
  }

  hasChildDomainAccess(
    domainId: string,
    childId: string,
    access: ModuleAccess = ModuleAccess.BROWSE,
  ): boolean {
    if (!this.loaded || this.isSuperUser || this.domains.length === 0) return true;
    const info = this.getDomainAccess(domainId);
    if (!info || !info[access]) return false;
    return info.allChildDomains || info.childDomainIds.includes(childId);
  }

  /** Child domains for a domain — already access-filtered server-side. */
  getChildDomains(domainId: string): Observable<DomainDefinition[]> {
    return this.http
      .get<DomainDefinition[]>(
        `${this.apiUrl}/api/domains/${encodeURIComponent(domainId)}/children?filterForSelection=true`,
        { withCredentials: true },
      )
      .pipe(catchError(() => of([] as DomainDefinition[])));
  }
}
