import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { ApiFailure } from '../models/api-failure';
import { apiErrorInterceptor } from './api-error-interceptor';

describe('apiErrorInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  it('maps ValidationProblemDetails to a typed actionable failure with all fields', async () => {
    const result = new Promise((resolve, reject) =>
      http.get('/api/quotes?page=0&size=101').subscribe({ next: resolve, error: reject }),
    );

    httpMock.expectOne('/api/quotes?page=0&size=101').flush(
      {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: {
          page: ['Page must be at least 1.'],
          size: ['Size must be between 1 and 100.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const failure = (await result.catch((reason: unknown) => reason)) as ApiFailure;

    expect(failure).toEqual({
      status: 400,
      message: 'Page must be at least 1.',
      fieldErrors: {
        page: ['Page must be at least 1.'],
        size: ['Size must be between 1 and 100.'],
      },
    });
    httpMock.verify();
  });

  it('uses ProblemDetails detail as the friendly message', async () => {
    const result = new Promise((resolve, reject) =>
      http.get('/api/quotes/404').subscribe({ next: resolve, error: reject }),
    );

    httpMock.expectOne('/api/quotes/404').flush(
      {
        status: 404,
        title: 'Resource not found.',
        detail: 'Quote 404 no longer exists.',
      },
      { status: 404, statusText: 'Not Found' },
    );

    const failure = (await result.catch((reason: unknown) => reason)) as ApiFailure;

    expect(failure.status).toBe(404);
    expect(failure.message).toBe('Quote 404 no longer exists.');
    expect(failure.message).not.toContain('Http failure response');
    expect(failure.fieldErrors).toEqual({});
    httpMock.verify();
  });
});
