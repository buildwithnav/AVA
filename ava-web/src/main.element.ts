import { createApplication } from '@angular/platform-browser';
import { ɵSharedStylesHost as SharedStylesHost } from '@angular/platform-browser';
import { createCustomElement } from '@angular/elements';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideZonelessChangeDetection, DOCUMENT, APP_ID, CSP_NONCE, PLATFORM_ID, Injectable, inject } from '@angular/core';
import { Chat } from './app/components/chat/chat';

/**
 * Custom SharedStylesHost that prevents Angular from injecting component/CDK
 * styles into `document.head`. Styles are only placed into Shadow DOM roots
 * via `addHost()`, which keeps the host application's own styles untouched.
 */
@Injectable()
class ShadowDomStylesHost extends SharedStylesHost {
  constructor() {
    const doc = inject(DOCUMENT);
    const appId = inject(APP_ID);
    const nonce = inject(CSP_NONCE, { optional: true });
    const platformId = inject(PLATFORM_ID);
    super(doc, appId, nonce, platformId);
    // The parent constructor adds doc.head as a style host, which causes
    // Material/CDK styles to leak into the host page. Remove it so styles
    // are only added to shadow roots registered via addHost().
    (this as any).hosts.delete(doc.head);
  }
}

(async () => {
  const app = await createApplication({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideAnimations(),
      { provide: SharedStylesHost, useClass: ShadowDomStylesHost },
    ],
  });

  if (!customElements.get('ava-chat')) {
    const AvaChatElement = createCustomElement(Chat, { injector: app.injector });
    customElements.define('ava-chat', AvaChatElement);
  }
})();
