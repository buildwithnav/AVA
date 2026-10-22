import { inject } from '@angular/core';
import { CanActivateFn, Router, ActivatedRouteSnapshot } from '@angular/router';
import { SecurityService, ModuleAccess } from '../services/security.service';

/**
 * Blocks navigation to a route whose module the user cannot browse.
 *
 * Reads the required module key from the route's own `data.moduleKey` (set in app.routes.ts)
 * rather than a separate path→moduleKey config file — Angular's router already carries
 * per-route data, so a parallel lookup table would just be duplicating it. A separate
 * RouteConfig file only earns its keep when routes have nested sub-links to resolve (as in
 * the Angular Template's multi-level nav); this app's nav is flat.
 */
export const authGuard: CanActivateFn = (route: ActivatedRouteSnapshot) => {
  const security = inject(SecurityService);
  const router = inject(Router);

  const moduleKey = route.data['moduleKey'] as string | undefined;
  if (!moduleKey || security.getModuleAccess(moduleKey, ModuleAccess.BROWSE)) {
    return true;
  }

  return router.createUrlTree(['/not-authorized']);
};
