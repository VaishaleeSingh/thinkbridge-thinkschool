import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withInMemoryScrolling } from '@angular/router';

import { authInterceptor } from './core/interceptors/auth-interceptor';
import { routes } from './app.routes';

/**
 * Every provider this application has, in one place, as functions. There is no
 * AppModule and no feature module anywhere in the project -- see app.routes.ts for
 * the per-route providers, which are the only other providers that exist.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    // Reports an unhandled rejection or error to Angular's ErrorHandler instead
    // of letting it disappear into the console. Without it, a rejected promise
    // inside an event handler is silent.
    provideBrowserGlobalErrorListeners(),

    /**
     * ZONELESS. Angular 21 creates new applications without zone.js at all --
     * `node_modules/zone.js` is not installed and no polyfill is configured, so
     * this application could not use Zone-based change detection even if it
     * wanted to. This provider is stated anyway, for two reasons: it is the
     * explicit, greppable declaration of the mode the application runs in, and it
     * makes the guarantee survive someone later adding a dependency that pulls
     * zone.js back in transitively.
     *
     * WHAT IT MEANS -- and does not mean. It does not mean "no change detection".
     * Change detection still runs; what changes is what SCHEDULES it. Zone.js
     * works by patching every asynchronous browser API (setTimeout, addEventListener,
     * fetch, promises) so that Angular is notified whenever any async work
     * finishes, and then checks the component tree because something MIGHT have
     * changed. Zoneless removes the patching and the guessing: Angular refreshes
     * a view when something actually tells it to -- a signal read in that template
     * changed, a template event listener fired, markForCheck was called, a view
     * was attached.
     *
     * The practical consequence for this codebase, and the reason every store here
     * is built on signals: state that is not a signal, mutated outside a template
     * listener, will not by itself cause a re-render. A promise resolving inside
     * QuotesStore.load() updates the UI because `quotes` is a signal that the
     * template reads -- not because the request finished.
     */
    provideZonelessChangeDetection(),

    provideRouter(
      routes,

      // Without this, navigating from the bottom of a long quotes list to a
      // collection lands halfway down the new page.
      withInMemoryScrolling({ scrollPositionRestoration: 'top', anchorScrolling: 'enabled' }),
    ),

    // One interceptor: bearer token, and a single silent retry after refreshing
    // an expired access token. See core/interceptors/auth-interceptor.ts.
    provideHttpClient(withInterceptors([authInterceptor])),
  ],
};
