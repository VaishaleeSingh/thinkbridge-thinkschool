import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { retry, throwError, timer } from 'rxjs';

const TRANSIENT_STATUSES = new Set([0, 408, 429, 500, 502, 503, 504]);
const MAX_RETRIES = 2;
const BACKOFF_MS = 100;

/**
 * Retries only safe reads, and only when another attempt can plausibly help.
 * The original request plus two retries is the hard upper bound.
 */
export const retryInterceptor: HttpInterceptorFn = (request, next) => {
  if (request.method !== 'GET') {
    return next(request);
  }

  return next(request).pipe(
    retry({
      count: MAX_RETRIES,
      delay: (error: unknown, retryCount: number) => {
        if (!isTransientHttpFailure(error)) {
          return throwError(() => error);
        }

        return timer(retryCount * BACKOFF_MS);
      },
    }),
  );
};

function isTransientHttpFailure(error: unknown): error is HttpErrorResponse {
  return error instanceof HttpErrorResponse && TRANSIENT_STATUSES.has(error.status);
}
