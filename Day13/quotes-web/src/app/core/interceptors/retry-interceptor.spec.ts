import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { retryInterceptor } from './retry-interceptor';

describe('retryInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([retryInterceptor])),
        provideHttpClientTesting(),
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  it('retries GET twice with 100 ms then 200 ms backoff before succeeding', async () => {
    const result = new Promise((resolve, reject) =>
      http.get('/api/quotes').subscribe({ next: resolve, error: reject }),
    );

    httpMock.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Unavailable' });

    await vi.advanceTimersByTimeAsync(99);
    httpMock.expectNone('/api/quotes');
    await vi.advanceTimersByTimeAsync(1);
    httpMock.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Unavailable' });

    await vi.advanceTimersByTimeAsync(199);
    httpMock.expectNone('/api/quotes');
    await vi.advanceTimersByTimeAsync(1);
    httpMock.expectOne('/api/quotes').flush({ items: [] });

    await expect(result).resolves.toEqual({ items: [] });
  });

  it('stops after two GET retries and rethrows the final failure', async () => {
    const result = new Promise((resolve, reject) =>
      http.get('/api/quotes').subscribe({ next: resolve, error: reject }),
    );

    httpMock.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Unavailable' });
    await vi.advanceTimersByTimeAsync(100);
    httpMock.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Unavailable' });
    await vi.advanceTimersByTimeAsync(200);
    httpMock.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Still unavailable' });

    const error = await result.catch((reason: unknown) => reason);
    expect(error).toBeInstanceOf(HttpErrorResponse);
    expect((error as HttpErrorResponse).statusText).toBe('Still unavailable');

    await vi.runAllTimersAsync();
    httpMock.expectNone('/api/quotes');
  });

  it('does not retry a non-idempotent POST after a transient failure', async () => {
    const result = new Promise((resolve, reject) =>
      http.post('/api/quotes', { author: 'Rumi' }).subscribe({ next: resolve, error: reject }),
    );

    httpMock.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Unavailable' });
    const error = await result.catch((reason: unknown) => reason);

    expect(error).toBeInstanceOf(HttpErrorResponse);
    await vi.runAllTimersAsync();
    httpMock.expectNone('/api/quotes');
  });

  it('does not retry an ordinary GET 400', async () => {
    const result = new Promise((resolve, reject) =>
      http.get('/api/quotes?page=0&size=101').subscribe({ next: resolve, error: reject }),
    );

    httpMock
      .expectOne('/api/quotes?page=0&size=101')
      .flush({ title: 'Validation failed' }, { status: 400, statusText: 'Bad Request' });
    const error = await result.catch((reason: unknown) => reason);

    expect(error).toBeInstanceOf(HttpErrorResponse);
    await vi.runAllTimersAsync();
    httpMock.expectNone('/api/quotes?page=0&size=101');
  });
});
