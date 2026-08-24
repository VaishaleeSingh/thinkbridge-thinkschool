import { InjectionToken } from '@angular/core';

import { environment } from '../../../environments/environment';

/**
 * Where the Week-1 API lives.
 *
 * An injection token rather than services importing `environment` directly, for
 * two reasons. A test can provide a different value without touching the
 * filesystem or the build configuration -- which is what
 * `quotes-api.spec.ts` does. And it makes the dependency visible: a service that
 * takes API_BASE_URL declares that it talks to the API, where one that imports
 * a constant hides it.
 *
 * The value itself comes from src/environments; see the comments there for why
 * development points at an absolute cross-origin URL and production does not.
 */
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => environment.apiBaseUrl,
});
