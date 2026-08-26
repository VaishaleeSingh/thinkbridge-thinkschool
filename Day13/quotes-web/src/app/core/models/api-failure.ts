import { HttpErrorResponse } from '@angular/common/http';

/**
 * One normalised failure type for the whole application.
 *
 * WHY THIS EXISTS: the API can fail in five shapes that look nothing alike --
 * an RFC 7807 ProblemDetails (its ExceptionHandlingMiddleware), a
 * ValidationProblemDetails with a per-field `errors` dictionary
 * (Results.ValidationProblem), a bare 401/403/404 with no body at all, a 409
 * with a title and detail, and a browser-level failure with status 0 when the
 * API is not running or CORS refused the response. Left unnormalised, every
 * component that shows an error would need to know all five.
 *
 * So HTTP shapes are translated exactly once (see `toApiFailure`) and every
 * component consumes this instead.
 */
export interface ApiFailure {
  /** HTTP status, or 0 when the request never reached the API. */
  readonly status: number;

  /** A sentence fit to show a user. Never a stack trace, never `[object Object]`. */
  readonly message: string;

  /**
   * Per-field messages from a ValidationProblemDetails, keyed by the field name
   * the API used ("author", "text", "email", "credentials"). Empty for every
   * other kind of failure.
   */
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
}

/** The RFC 7807 body ASP.NET Core returns, as far as this client reads it. */
interface ProblemDetailsBody {
  readonly title?: string;
  readonly detail?: string;
  readonly errors?: Record<string, string[]>;
}

/**
 * Turns anything HttpClient can hand back into an ApiFailure.
 *
 * The status-by-status messages are deliberate: "Http failure response for
 * http://localhost:5059/api/quotes: 401 Unauthorized" is what Angular produces
 * by default, and it tells a user nothing they can act on.
 */
export function toApiFailure(error: unknown): ApiFailure {
  // Interceptors normalise HTTP errors before stores see them. Stores still
  // call this helper at their boundary, so accepting the typed result unchanged
  // keeps that existing code useful without replacing a specific message with
  // the generic non-HTTP fallback.
  if (isApiFailure(error)) {
    return error;
  }

  if (!(error instanceof HttpErrorResponse)) {
    return {
      status: 0,
      message: 'Something went wrong. Please try again.',
      fieldErrors: {},
    };
  }

  const body: ProblemDetailsBody | null =
    error.error && typeof error.error === 'object' ? (error.error as ProblemDetailsBody) : null;

  const fieldErrors: Record<string, readonly string[]> = body?.errors ?? {};

  // Status 0 is the case worth naming precisely, because it is the one a
  // developer running this app will hit first and the browser's own message for
  // it ("Http failure response ... 0 Unknown Error") actively misleads: the
  // request never left, so the API cannot have failed. It means the API is not
  // running, or it is running and its CORS policy did not name this origin.
  if (error.status === 0) {
    return {
      status: 0,
      message:
        'Could not reach the API. Check that it is running on the configured address, and that this origin is listed in the API’s Cors:AllowedOrigins.',
      fieldErrors,
    };
  }

  const message =
    firstFieldError(fieldErrors) ??
    body?.detail ??
    body?.title ??
    defaultMessageForStatus(error.status);

  return { status: error.status, message, fieldErrors };
}

function isApiFailure(error: unknown): error is ApiFailure {
  if (error === null || typeof error !== 'object') {
    return false;
  }

  const candidate = error as Partial<ApiFailure>;

  return (
    typeof candidate.status === 'number' &&
    typeof candidate.message === 'string' &&
    candidate.fieldErrors !== null &&
    typeof candidate.fieldErrors === 'object'
  );
}

/**
 * A validation problem's own `title` is the generic "One or more validation
 * errors occurred." -- true, and useless. The first field message is the one
 * that says what to fix, so it is what gets promoted to the summary.
 */
function firstFieldError(fieldErrors: Readonly<Record<string, readonly string[]>>): string | null {
  for (const messages of Object.values(fieldErrors)) {
    if (messages.length > 0) {
      return messages[0];
    }
  }
  return null;
}

function defaultMessageForStatus(status: number): string {
  switch (status) {
    case 400:
      return 'That request was not valid.';
    case 401:
      return 'Your session has expired. Please sign in again.';
    case 403:
      return 'You do not have permission to do that.';
    case 404:
      return 'That item no longer exists.';
    case 409:
      return 'That conflicts with something that already exists.';
    default:
      return status >= 500
        ? 'The API had a problem handling that request. Please try again.'
        : 'That request could not be completed.';
  }
}
