/**
 * Auth shapes, taken from QuotesApi/Extensions/AuthEndpointExtensions.cs.
 */

/** Body of POST /api/auth/login. */
export interface LoginRequest {
  readonly email: string;
  readonly password: string;
}

/** Body of POST /api/auth/register. */
export interface RegisterRequest {
  readonly email: string;
  readonly password: string;
}

/**
 * What /login, /register and /refresh all return. The API calls it
 * LoginResponse; it is named for the pair of tokens here because /register
 * returns it too.
 */
export interface TokenPair {
  readonly accessToken: string;
  readonly refreshToken: string;

  /** Seconds until `accessToken` expires -- 900 with the API's default config. */
  readonly expiresIn: number;
  readonly tokenType: string;
}

/**
 * The signed-in session as the client holds it: the token pair, plus the two
 * facts read out of the access token's own payload.
 *
 * `userId` and `email` are NOT sent as separate fields by the API and are not
 * invented here -- they are the `sub` and `email` claims inside the JWT the API
 * issued (see AuthService.GenerateAccessToken). Deriving them from the token
 * means they cannot disagree with it.
 */
export interface Session {
  readonly accessToken: string;
  readonly refreshToken: string;

  /** The `sub` claim -- the API's own user id, as a string. */
  readonly userId: string;

  /** The `email` claim. Displayed in the header; never used to authorize. */
  readonly email: string;

  /** `exp`, in epoch milliseconds, for proactive refresh. */
  readonly expiresAtMs: number;
}

/** The subset of the access token's payload this client reads. */
export interface AccessTokenClaims {
  readonly sub: string;
  readonly email: string;
  readonly exp: number;
}

/**
 * Reads the claims out of a JWT WITHOUT verifying its signature.
 *
 * That is safe for exactly one purpose and no other: deciding what to draw. The
 * header greeting and the "is this quote mine, so should a delete button
 * appear" check both read this. Neither is a security boundary -- a user who
 * forged a token to make a delete button appear would still be refused by the
 * API, which does verify the signature, because that is where the decision
 * actually lives.
 *
 * Returns null rather than throwing on anything malformed: a corrupt token in
 * storage should sign the user out, not crash the application on boot.
 */
export function readAccessTokenClaims(accessToken: string): AccessTokenClaims | null {
  const parts = accessToken.split('.');
  if (parts.length !== 3) {
    return null;
  }

  try {
    // JWTs use base64url, which atob() does not accept: '-' and '_' have to
    // become '+' and '/', and the padding the encoder stripped has to come
    // back, or atob throws on any payload whose length is not a multiple of 4.
    const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');

    // decodeURIComponent(escape(...)) rather than atob alone: an email with a
    // non-ASCII character comes out of atob as mojibake otherwise.
    const json = decodeURIComponent(
      atob(padded)
        .split('')
        .map((character) => `%${`00${character.charCodeAt(0).toString(16)}`.slice(-2)}`)
        .join(''),
    );

    const payload = JSON.parse(json) as Partial<AccessTokenClaims>;

    if (typeof payload.sub !== 'string' || typeof payload.exp !== 'number') {
      return null;
    }

    return {
      sub: payload.sub,
      email: typeof payload.email === 'string' ? payload.email : '',
      exp: payload.exp,
    };
  } catch {
    return null;
  }
}

/** Builds a Session from a token pair, or null if the access token is unusable. */
export function toSession(tokens: TokenPair): Session | null {
  const claims = readAccessTokenClaims(tokens.accessToken);
  if (!claims) {
    return null;
  }

  return {
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    userId: claims.sub,
    email: claims.email,
    expiresAtMs: claims.exp * 1000,
  };
}
