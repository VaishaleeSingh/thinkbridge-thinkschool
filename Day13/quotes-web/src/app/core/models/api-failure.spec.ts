import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';

import { toApiFailure } from './api-failure';

/**
 * These five cases are the five shapes the API can fail in. They are tested
 * because every error message a user sees in this application comes out of this
 * one function -- if it falls through to a generic string, every screen shows a
 * generic string.
 */
describe('toApiFailure', () => {
  it('promotes the first field message out of a validation problem', () => {
    const failure = toApiFailure(
      new HttpErrorResponse({
        status: 400,
        error: {
          title: 'One or more validation errors occurred.',
          errors: { author: ['Author is required.'], text: ['Text is required.'] },
        },
      }),
    );

    expect(failure.status).toBe(400);

    // Not the generic title, which is what the body's own `title` says.
    expect(failure.message).toBe('Author is required.');
    expect(failure.fieldErrors['author']).toEqual(['Author is required.']);
  });

  it('uses ProblemDetails detail when there are no field errors', () => {
    const failure = toApiFailure(
      new HttpErrorResponse({
        status: 409,
        error: {
          title: 'Email already registered',
          detail: 'An account already exists for that email address.',
        },
      }),
    );

    expect(failure.message).toBe('An account already exists for that email address.');
    expect(failure.fieldErrors).toEqual({});
  });

  it('gives a bodiless 401 a message about the session, not about HTTP', () => {
    const failure = toApiFailure(new HttpErrorResponse({ status: 401 }));

    expect(failure.status).toBe(401);
    expect(failure.message).toContain('session');
  });

  it('explains status 0 as unreachable-or-CORS rather than as an API failure', () => {
    // What the browser reports when the API is not running, or when its CORS
    // policy did not name this origin -- the first thing a developer hits.
    const failure = toApiFailure(new HttpErrorResponse({ status: 0 }));

    expect(failure.status).toBe(0);
    expect(failure.message).toContain('Cors:AllowedOrigins');
  });

  it('never returns [object Object] for a non-HTTP error', () => {
    const failure = toApiFailure(new TypeError('undefined is not a function'));

    expect(failure.status).toBe(0);
    expect(failure.message).toBe('Something went wrong. Please try again.');
  });
});
