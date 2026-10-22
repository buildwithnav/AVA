import { bootstrapApplication } from '@angular/platform-browser';
import { createCustomElement } from '@angular/elements';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { Chat } from './app/components/chat/chat';

bootstrapApplication(App, appConfig)
  .then((appRef) => {
    // Register <ava-chat> as a custom element (web component)
    if (!customElements.get('ava-chat')) {
      const AvaChatElement = createCustomElement(Chat, { injector: appRef.injector });
      customElements.define('ava-chat', AvaChatElement);
    }
  })
  .catch((err) => console.error(err));
