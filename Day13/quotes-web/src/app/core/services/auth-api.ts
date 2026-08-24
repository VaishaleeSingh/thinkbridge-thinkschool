import { HttpClient, HttpContext, HttpContextToken } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { LoginRequest, RegisterRequest, TokenPair } from '../models/auth';
import { API_BASE_URL } from './api-base-url';

/**
 * Marks a request as one of the token-issuing calls.
 *
 * The auth interceptor reads this and leaves such requests alone: attaching an
 * expired bearer token to /login is pointless, and -- more importantly -- a 401
 * from /login means "wrong password", so trying to refresh and retry it would
 * turn a clear credentials error into a silent sign-out.
 *
 * An HttpContext token rather than a URL check in the interceptor: the request
 * declares its own nature at the call site, instead of the interceptor pattern
 * matching on strings it would have to keep in sync with these methods.
 */
export const SKIP_AUTH_HANDLING = new HttpContextToken<boolean>(() => false);

/** The four endpoints under /api/auth. Nothing else in the app calls them. */
@Injectable({ providedIn: 'root' })
export class AuthApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  private get context(): HttpContext {
    return new HttpContext().set(SKIP_AUTH_HANDLING, true);
  }

  login(request: LoginRequest): Promise<TokenPair> {
    return firstValueFrom(
      this.http.post<TokenPair>(`${this.baseUrl}/api/auth/login`, request, {
        context: this.context,
      }),
    );
  }

  /** POST /api/auth/register -- added to the API on Day 13; returns 201 + tokens. */
  register(request: RegisterRequest): Promise<TokenPair> {
    return firstValueFrom(
      this.http.post<TokenPair>(`${this.baseUrl}/api/auth/register`, request, {
        context: this.context,
      }),
    );
  }

  /**
   * Trades a refresh token for a new pair. The API rotates on every use: the
   * token passed in is dead afterwards, so the caller must store what comes
   * back or lose the session.
   */
  refresh(refreshToken: string): Promise<TokenPair> {
    return firstValueFrom(
      this.http.post<TokenPair>(
        `${this.baseUrl}/api/auth/refresh`,
        { refreshToken },
        { context: this.context },
      ),
    );
  }

  /** Revokes the refresh token server-side. Returns 204 with no body. */
  logout(refreshToken: string): Promise<void> {
    return firstValueFrom(
      this.http.post<void>(
        `${this.baseUrl}/api/auth/logout`,
        { refreshToken },
        { context: this.context },
      ),
    );
  }
}
