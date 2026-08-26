import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { appConfig } from '../../app.config';
import { AuthStore } from '../services/auth-store';

describe('registered HTTP interceptor pipeline', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let accessToken: string;
  let refresh: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers();
    accessToken = 'first-token';
    refresh = vi.fn().mockImplementation(async () => {
      accessToken = 'second-token';
      return true;
    });

    TestBed.configureTestingModule({
      providers: [
        ...appConfig.providers,
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

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  it('lets auth recover a raw 401, then lets retry recover a raw 503', async () => {
    const result = new Promise((resolve, reject) =>
      http.get('/api/quotes').subscribe({ next: resolve, error: reject }),
    );

    const original = httpMock.expectOne('/api/quotes');
    expect(original.request.headers.get('Authorization')).toBe('Bearer first-token');
    original.flush(null, { status: 401, statusText: 'Unauthorized' });

    // apiErrorInterceptor is outermost, so the 401 remains an HttpErrorResponse
    // until authInterceptor has had the opportunity to refresh.
    await Promise.resolve();
    const afterRefresh = httpMock.expectOne('/api/quotes');
    expect(afterRefresh.request.headers.get('Authorization')).toBe('Bearer second-token');
    afterRefresh.flush(null, { status: 503, statusText: 'Unavailable' });

    await vi.advanceTimersByTimeAsync(99);
    httpMock.expectNone('/api/quotes');
    await vi.advanceTimersByTimeAsync(1);

    const transientRetry = httpMock.expectOne('/api/quotes');
    expect(transientRetry.request.headers.get('Authorization')).toBe('Bearer second-token');
    transientRetry.flush({ items: [] });

    await expect(result).resolves.toEqual({ items: [] });
    expect(refresh).toHaveBeenCalledTimes(1);
  });
});
