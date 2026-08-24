import { Injectable, computed, effect, inject, signal } from '@angular/core';

import { ApiFailure, toApiFailure } from '../models/api-failure';
import { Session, TokenPair, toSession } from '../models/auth';
import { AuthApi } from './auth-api';

const STORAGE_KEY = 'quotes-web.session';

/**
 * Who is signed in, as one signal, plus the four operations that change it.
 *
 * This is the only writable owner of the session in the application. The
 * interceptor reads the token from here, the guard reads `isAuthenticated`, the
 * header reads `email` -- none of them can set any of it, because `session` is
 * private and only its readonly projections are exposed. "Who owns this state"
 * has exactly one answer.
 *
 * WHY sessionStorage AND NOT localStorage: the refresh token is a bearer
 * credential, and this API has no cookie-based option -- so the honest choice is
 * between two imperfect ones. sessionStorage dies with the tab, which bounds the
 * damage of a stale shared machine and still survives a reload (the flow a user
 * actually notices). localStorage would keep it indefinitely across every tab.
 * Neither survives an XSS; the real fix for that is a same-site, http-only
 * cookie, which would be an API change, not a client one.
 */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly api = inject(AuthApi);

  /** The single source of truth. Private: nothing outside this class may set it. */
  private readonly session = signal<Session | null>(readStoredSession());

  /** In flight state for the sign-in/register forms, so a button can disable itself. */
  private readonly submitting = signal(false);
  private readonly failure = signal<ApiFailure | null>(null);

  readonly isAuthenticated = computed(() => this.session() !== null);
  readonly email = computed(() => this.session()?.email ?? '');

  /**
   * The API's own user id for the signed-in caller, or null. Compared against
   * `Quote.createdByUserId` to decide whether to offer a delete control -- see
   * the note in core/models/auth.ts about why that is a display decision and
   * not an authorization one.
   */
  readonly userId = computed(() => this.session()?.userId ?? null);

  readonly isSubmitting = this.submitting.asReadonly();
  readonly error = this.failure.asReadonly();

  constructor() {
    // A genuine side effect, which is the only thing effect() is for here:
    // mirroring signal state into browser storage. It is not deriving anything
    // -- everything derived above is a computed() -- and it is not fetching
    // anything.
    effect(() => {
      writeStoredSession(this.session());
    });
  }

  /** Read synchronously by the interceptor on every outgoing request. */
  accessToken(): string | null {
    return this.session()?.accessToken ?? null;
  }

  /** True on success. On failure the reason is in `error()`. */
  async signIn(email: string, password: string): Promise<boolean> {
    return this.runCredentialsCall(() => this.api.login({ email, password }));
  }

  /** True on success. A 409 here means the email is already registered. */
  async register(email: string, password: string): Promise<boolean> {
    return this.runCredentialsCall(() => this.api.register({ email, password }));
  }

  /**
   * Clears the session locally and revokes the refresh token server-side.
   *
   * The local clear happens FIRST and unconditionally. If the network call
   * fails, the user is still signed out of this browser -- the alternative
   * (await, then clear) leaves someone who clicked "sign out" on a flaky
   * connection still signed in, which is the one outcome sign-out must never
   * have.
   */
  async signOut(): Promise<void> {
    const refreshToken = this.session()?.refreshToken;
    this.session.set(null);
    this.failure.set(null);

    if (!refreshToken) {
      return;
    }

    try {
      await this.api.logout(refreshToken);
    } catch {
      // Deliberately swallowed, and the only swallowed error in this class.
      // The token expires on its own in seven days, the user is already signed
      // out locally, and there is no action they could take on this failure.
    }
  }

  /**
   * Exchanges the refresh token for a new pair. Returns false if the session
   * cannot be recovered, in which case it has been cleared.
   *
   * CONCURRENCY: a page that fires three requests at once and gets three 401s
   * must not send three refreshes. The API rotates the refresh token on every
   * use and treats a re-presented one as theft -- it revokes the entire token
   * family -- so the naive version does not merely waste a request, it signs the
   * user out. The in-flight promise is shared so the second and third callers
   * await the first refresh instead of starting their own.
   */
  refresh(): Promise<boolean> {
    this.refreshInFlight ??= this.performRefresh().finally(() => {
      this.refreshInFlight = null;
    });

    return this.refreshInFlight;
  }

  private refreshInFlight: Promise<boolean> | null = null;

  private async performRefresh(): Promise<boolean> {
    const refreshToken = this.session()?.refreshToken;
    if (!refreshToken) {
      return false;
    }

    try {
      const tokens = await this.api.refresh(refreshToken);
      const session = toSession(tokens);

      if (!session) {
        this.session.set(null);
        return false;
      }

      this.session.set(session);
      return true;
    } catch {
      this.session.set(null);
      return false;
    }
  }

  /** The shared body of signIn and register -- identical but for one call. */
  private async runCredentialsCall(call: () => Promise<TokenPair>): Promise<boolean> {
    this.submitting.set(true);
    this.failure.set(null);

    try {
      const session = toSession(await call());

      if (!session) {
        this.failure.set({
          status: 0,
          message: 'The API returned a token this client could not read.',
          fieldErrors: {},
        });
        return false;
      }

      this.session.set(session);
      return true;
    } catch (error) {
      this.failure.set(toApiFailure(error));
      return false;
    } finally {
      // finally, not after each branch: a submit button that stays disabled
      // because an error path forgot to reset this is unrecoverable without a
      // reload.
      this.submitting.set(false);
    }
  }
}

/**
 * Storage access is wrapped because it genuinely throws: Safari in private mode
 * and browsers configured to block site data both raise on access, not on write.
 * A boot that crashes for that reason would be worse than a boot with no
 * remembered session.
 */
function readStoredSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw) as Partial<Session>;

    if (typeof parsed.accessToken !== 'string' || typeof parsed.refreshToken !== 'string') {
      return null;
    }

    // Rebuilt through toSession rather than trusted as stored, so `userId`,
    // `email` and `expiresAtMs` always come from the token itself -- a hand
    // edited storage entry cannot change who this client thinks it is.
    return toSession({
      accessToken: parsed.accessToken,
      refreshToken: parsed.refreshToken,
      expiresIn: 0,
      tokenType: 'Bearer',
    });
  } catch {
    return null;
  }
}

function writeStoredSession(session: Session | null): void {
  try {
    if (session) {
      sessionStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ accessToken: session.accessToken, refreshToken: session.refreshToken }),
      );
    } else {
      sessionStorage.removeItem(STORAGE_KEY);
    }
  } catch {
    // Nothing to do and nothing to tell the user: the session still works for
    // this page load, it just will not survive a reload.
  }
}
