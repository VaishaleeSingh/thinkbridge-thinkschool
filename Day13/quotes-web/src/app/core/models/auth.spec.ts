import { describe, expect, it } from 'vitest';

import { readAccessTokenClaims, toSession } from './auth';

/**
 * Builds a JWT-shaped string with the given payload. The signature is a
 * placeholder: this client reads the payload and never verifies it (the API does
 * that), so an unsigned token is exactly what these tests should use -- see the
 * note on readAccessTokenClaims about why reading without verifying is safe here
 * and only here.
 */
function makeToken(payload: Record<string, unknown>): string {
  const encode = (value: object) =>
    btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}.signature-not-checked`;
}

describe('readAccessTokenClaims', () => {
  it('reads sub, email and exp from a token the API would issue', () => {
    // The exact claim set AuthService.GenerateAccessToken puts in.
    const token = makeToken({ sub: '42', email: 'dev@example.com', exp: 1_800_000_000 });

    expect(readAccessTokenClaims(token)).toEqual({
      sub: '42',
      email: 'dev@example.com',
      exp: 1_800_000_000,
    });
  });

  it('survives base64url padding that atob would reject', () => {
    // A payload whose base64 length is not a multiple of four: the case that
    // throws if the padding is not restored before decoding.
    const token = makeToken({ sub: '1', email: 'a@b.co', exp: 1 });

    expect(readAccessTokenClaims(token)?.sub).toBe('1');
  });

  it('returns null rather than throwing for a corrupt token', () => {
    // A corrupt token in storage must sign the user out, not crash the app on
    // boot -- which is what AuthStore relies on.
    expect(readAccessTokenClaims('not-a-jwt')).toBeNull();
    expect(readAccessTokenClaims('a.b.c')).toBeNull();
    expect(readAccessTokenClaims(makeToken({ email: 'no-sub@example.com' }))).toBeNull();
  });
});

describe('toSession', () => {
  it('derives userId and email from the token rather than trusting the caller', () => {
    const session = toSession({
      accessToken: makeToken({ sub: '7', email: 'seneca@example.com', exp: 1_700_000_000 }),
      refreshToken: 'refresh-token',
      expiresIn: 900,
      tokenType: 'Bearer',
    });

    expect(session).not.toBeNull();
    expect(session?.userId).toBe('7');
    expect(session?.email).toBe('seneca@example.com');

    // exp is seconds; the session stores milliseconds.
    expect(session?.expiresAtMs).toBe(1_700_000_000_000);
  });

  it('is null when the access token cannot be read', () => {
    expect(
      toSession({
        accessToken: 'garbage',
        refreshToken: 'refresh-token',
        expiresIn: 900,
        tokenType: 'Bearer',
      }),
    ).toBeNull();
  });
});
