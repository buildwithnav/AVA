import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, firstValueFrom, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { environment } from '../../environments/environment';

// ── Module access models ──────────────────────────────────────────────────────
// Mirrors the CDO Gateway's PascalCase payload from `user/app/{appKey}/modules`,
// the same contract the Angular Template's SecurityService consumes.

export interface Module {
  moduleKey: string;
  description: string;
  browse: boolean;
  update: boolean;
  insert: boolean;
  delete: boolean;
}

export enum ModuleAccess {
  BROWSE = 'browse',
  UPDATE = 'update',
  INSERT = 'insert',
  DELETE = 'delete',
}

/**
 * App-level module keys registered against AVA's appKey in CDS.
 *
 * Domain and sub-domain keys are deliberately absent: domains are database rows and
 * sub-domains (e.g. individual studies) are created at runtime, so their keys resolve
 * from `domains.module_key` and the sub_domain_grants table rather than being
 * enumerated at compile time. This is the one intentional divergence from the Angular
 * Template, where every module is a static enum member.
 */
export enum ModuleKey {
  SUPER_USER = 'MresjWO',
  DOMAIN_ADMIN = 'MoU6LMc',
  WORKFLOW_BUILDER = 'MwdTPIB',
  TOOL_MANAGER = 'MO3OxHd',
  DATA_TEMPLATES = 'MCT5pUl',
  USAGE_ANALYTICS = 'Mqr9XUr',
}

/** Resolved access for one domain, including which child domains the user can see. */
export interface DomainAccessInfo {
  domainId: string;
  name: string;
  moduleKey?: string | null;
  browse: boolean;
  update: boolean;
  insert: boolean;
  delete: boolean;
  childDomainIds: string[];
  allChildDomains: boolean;
}

export interface CurrentUser {
  userId: string;
  displayName: string;
  email?: string | null;
  isLocal: boolean;
}

/** Payload of GET /api/security/context. */
export interface SecurityContext {
  user: CurrentUser;
  modules: any[];
  isSuperUser: boolean;
  domains: DomainAccessInfo[];
}

// ── Service ────────────────────────────────────────────────────────────────────

/**
 * Port of the Angular Template's SecurityService.
 *
 * Loads the caller's module grants once at startup (APP_INITIALIZER) and answers access
 * questions synchronously thereafter. Adds two methods the template has no equivalent for:
 * `hasDomainAccess` and `hasChildDomainAccess`, because AVA's domains and studies are runtime
 * data rather than compile-time enum members.
 */
@Injectable({ providedIn: 'root' })
export class SecurityService {
  private http = inject(HttpClient);

  modules: Module[] = [];
  modulesSubject = new BehaviorSubject<Module[]>([]);

  user: CurrentUser | null = null;
  isSuperUser = false;
  domains: DomainAccessInfo[] = [];

  private loaded = false;

  get isLocal(): boolean {
    return !environment.production;
  }

  /** Called from APP_INITIALIZER — resolves before the first route renders. */
  async load(): Promise<void> {
    const context = await firstValueFrom(
      this.http
        .get<SecurityContext>(`${environment.apiUrl}/api/security/context`, { withCredentials: true })
        .pipe(catchError(() => of(null))),
    );

    if (!context) {
      // Fail closed: a gateway/API failure must not silently grant access.
      console.error('[AVA] Failed to load security context — all module access denied.');
      this.modules = [];
      this.domains = [];
      this.isSuperUser = false;
      this.modulesSubject.next(this.modules);
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
    this.modulesSubject.next(this.modules);
    this.loaded = true;
  }

  getModuleFromKey(moduleKey: string): Module | undefined {
    return this.modules.find(x => x.moduleKey === moduleKey);
  }

  getModuleAccess(key: string, moduleAccess: ModuleAccess, overrideIfSuper = true): boolean {
    if (overrideIfSuper && this.isSuperUser) {
      return true;
    }
    const requestedModule = this.getModuleFromKey(key);
    return requestedModule ? requestedModule[moduleAccess] : false;
  }

  getDomainAccess(domainId: string): DomainAccessInfo | undefined {
    return this.domains.find(d => d.domainId?.toLowerCase() === domainId?.toLowerCase());
  }

  hasDomainAccess(domainId: string, access: ModuleAccess = ModuleAccess.BROWSE): boolean {
    if (this.isSuperUser) return true;

    const info = this.getDomainAccess(domainId);
    return info ? info[access] : false;
  }

  hasChildDomainAccess(
    domainId: string,
    childId: string,
    access: ModuleAccess = ModuleAccess.BROWSE,
  ): boolean {
    if (this.isSuperUser) return true;

    const info = this.getDomainAccess(domainId);
    if (!info || !info[access]) return false;

    return info.allChildDomains || info.childDomainIds.includes(childId);
  }

  /** Domain IDs the user can browse — use to filter pickers and lists. */
  accessibleDomainIds(access: ModuleAccess = ModuleAccess.BROWSE): string[] {
    return this.domains.filter(d => d[access]).map(d => d.domainId);
  }

  /** Force a re-read after a grant change; clears the API-side gateway cache first. */
  async refresh(): Promise<void> {
    if (!this.isLocal) {
      await firstValueFrom(
        this.http
          .post(`${environment.apiUrl}/api/security/refresh`, {}, { withCredentials: true })
          .pipe(catchError(() => of(null))),
      );
    }
    this.loaded = false;
    await this.load();
  }

  get isLoaded(): boolean {
    return this.loaded;
  }
}
