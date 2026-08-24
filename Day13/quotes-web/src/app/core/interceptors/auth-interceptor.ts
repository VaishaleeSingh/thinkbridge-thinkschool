import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';

import { SKIP_AUTH_HANDLING } from '../services/auth-api';
import { AuthStore } from '../services/auth-store';

/**
 * Attaches the bearer token, and recovers from exactly one thing: an access
 * token that expired mid-session.
 *
 * A functional interceptor (HttpInterceptorFn) rather than a class: there is no
 * state to hold -- all of it lives in AuthStore -- and a function can be
 * registered with `withInterceptors([...])` in app.config.ts without a DI
 * indirection whose only purpose is to satisfy an interface.
 *
 * THE 401 PATH, and why it is worth the complexity: the API's access tokens
 * last 15 minutes by design, and its refresh tokens last 7 days. Without this,
 * every user is dumped at the login screen a quarter of an hour after signing
 * in, with whatever they were typing lost -- while the credential that would
 * have silently fixed it sits unused in storage.
 *
 * Retried exactly once. If the refreshed token is also rejected, the failure is
 * real and is passed through to the caller, which is what makes the loop
 * terminate: a refresh-and-retry with no bound is how an expired session turns
 * into an infinite request storm.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const authStore = inject(AuthStore);

  // /login, /register, /refresh and /logout opt out (see SKIP_AUTH_HANDLING).
  // A 401 from those means "wrong credentials" or "dead refresh token", and
  // treating it as an expiry to recover from would hide the real answer.
  if (request.context.get(SKIP_AUTH_HANDLING)) {
    return next(request);
  }

  const token = authStore.accessToken();
  const authorized = token ? withBearer(request, token) : request;

  return next(authorized).pipe(
    catchError((error: unknown) => {
      const isExpiredAccessToken =
        error instanceof HttpErrorResponse && error.status === 401 && token !== null;

      if (!isExpiredAccessToken) {
        return throwError(() => error);
      }

      // AuthStore.refresh() de-duplicates concurrent callers, so ten requests
      // failing at once produce one refresh -- which matters because this API
      // treats a re-used refresh token as theft and revokes the whole family.
      return from(authStore.refresh()).pipe(
        switchMap((refreshed) => {
          const renewedToken = authStore.accessToken();

          if (!refreshed || !renewedToken) {
            // The session is already cleared by AuthStore at this point. The
            // original 401 is rethrown rather than a synthetic error, so the
            // caller sees what the API actually said.
            return throwError(() => error);
          }

          return next(withBearer(request, renewedToken));
        }),
      );
    }),
  );
};

/**
 * HttpRequest is immutable, so this clones. `setHeaders` rather than `headers`
 * so nothing else the caller set is dropped.
 */
function withBearer<T>(request: HttpRequest<T>, token: string): HttpRequest<T> {
  return request.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}
