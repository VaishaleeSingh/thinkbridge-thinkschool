import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { AuthStore } from '../services/auth-store';
import { authInterceptor } from './auth-interceptor';

/**
 * The refresh-and-retry path, which is the one piece of this application whose
 * failure mode is silent: get it wrong and users are signed out fifteen minutes
 * into every session, or -- worse -- three concurrent 401s each send their own
 * refresh, the API sees a re-used refresh token, treats it as theft and revokes
 * the whole family.
 */
describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let accessToken: string | null;
  let refresh: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    accessToken = 'first-token';

    // Resolves true and swaps the token, which is what the real AuthStore does
    // after a successful refresh.
    refresh = vi.fn().mockImplementation(async () => {
      accessToken = 'second-token';
      return true;
    });

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        {
          provide: AuthStore,
          useValue: {
            accessToken: () => accessToken,
            refresh,
          },
        },
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  it('attaches the bearer token', () => {
    http.get('/api/quotes').subscribe();

    const request = httpMock.expectOne('/api/quotes');
    expect(request.request.headers.get('Authorization')).toBe('Bearer first-token');

    request.flush({});
    httpMock.verify();
  });

  it('refreshes once on 401 and retries the original request with the new token', async () => {
    const response = new Promise((resolve) => http.get('/api/quotes').subscribe(resolve));

    httpMock.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });

    // The retry is a second request, carrying the refreshed token.
    await Promise.resolve();
    const retry = httpMock.expectOne('/api/quotes');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer second-token');

    retry.flush({ items: [] });

    await expect(response).resolves.toEqual({ items: [] });
    expect(refresh).toHaveBeenCalledTimes(1);
    httpMock.verify();
  });

  it('gives up after one retry rather than looping', async () => {
    let failure: unknown = null;
    http.get('/api/quotes').subscribe({ error: (error: unknown) => (failure = error) });

    httpMock.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();

    // The retry fails the same way. There must be no third request: an unbounded
    // refresh-and-retry turns an expired session into a request storm.
    httpMock.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();

    expect(failure).not.toBeNull();
    expect(refresh).toHaveBeenCalledTimes(1);
    httpMock.verify();
  });

  it('does not refresh when there was no token to expire', async () => {
    accessToken = null;

    let failure: unknown = null;
    http.get('/api/quotes').subscribe({ error: (error: unknown) => (failure = error) });

    const request = httpMock.expectOne('/api/quotes');
    expect(request.request.headers.has('Authorization')).toBe(false);

    request.flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();

    expect(failure).not.toBeNull();
    expect(refresh).not.toHaveBeenCalled();
    httpMock.verify();
  });
});
