import { ApplicationConfig, provideAppInitializer, provideBrowserGlobalErrorListeners, provideZoneChangeDetection, inject } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { WorkflowApiService } from '@ava/workflow-builder/services/workflow-api.service';

import { routes } from './app.routes';
import { environment } from '../environments/environment';
import { SecurityService } from './services/security.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideHttpClient(),
    provideAnimations(),
    provideAppInitializer(() => {
      inject(WorkflowApiService).configure(environment.apiUrl);
      // Awaited: routes are guarded on module access, so grants must land before the first render.
      return inject(SecurityService).load();
    }),
  ]
};
