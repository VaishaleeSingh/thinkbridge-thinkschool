# Why `staticwebapp.config.json` looks the way it does

Every entry in that file is there for a reason that cost something to learn.
Recorded here rather than as JSON comments, because SWA parses it as strict JSON.

## `navigationFallback.exclude` must list `/api/*`

This is the entry most likely to be dropped by someone tidying the file, and the
one whose absence is hardest to diagnose. Without it, the SPA fallback catches
API responses: a 404 or 500 from the backend comes back as `index.html` with
status **200**. Angular's `HttpClient` then tries to parse HTML as JSON, and the
`api-error-interceptor` never sees a failure status to report — so the quotes
list renders empty with no error message and no console entry that points at the
API. The static-asset globs are excluded for the same reason: a missing image
should 404, not silently return the app shell.

## `responseOverrides.404` → `index.html` with 200

Required for client-side routing: a hard refresh on `/quotes/100001` is a real
GET for a path with no file behind it. Combined with the `exclude` above, this is
scoped to navigation requests only.

## Cache-Control split

`index.html` is `no-store`; the hashed build output is `immutable` for a year.
Angular's production build sets `outputHashing: "all"` (see `angular.json`), so
every JS/CSS filename changes when its content does — they are safe to cache
forever, and `index.html` is the one file that must not be, or a deploy ships new
chunks that no browser asks for. The `/quote-backgrounds/*` assets are **not**
content-hashed (they live in `public/` and their paths are stored in the API's
data), so they get a week rather than a year.

## `Content-Security-Policy`

`style-src 'unsafe-inline'` is unavoidable: Angular injects component styles as
inline `<style>` elements. It is deliberately **not** granted to `script-src` —
that is the half that actually matters for XSS. `connect-src 'self'` is only
correct because the front end talks to the API same-origin through the SWA
`/api` route; it is what would break first if anyone re-pointed
`environment.production.ts` at a cross-origin host.

## `/api/diagnostics/*` → 404

The Week-1 API exposes `/api/diagnostics/*` (the Day 5 N+1 and seeding
endpoints). Blocking it at the edge is defence in depth — the BFF proxy already
allowlists `auth`, `quotes` and `collections` — so that a future change to the
proxy cannot quietly expose it.

## What is deliberately absent

- **`platform.apiRuntime`** — that setting configures SWA's *built-in managed*
  functions. This deployment uses a **linked** backend, which is a separate
  Function App with its own runtime and its own managed identity; setting
  `apiRuntime` here would suggest a managed API that does not exist.
- **`auth` / `identityProviders`** — user authentication is the Week-1 API's
  first-party JWT, handled in `core/interceptors/auth-interceptor.ts`. SWA's
  Easy Auth is not in the path, and configuring it would create a second,
  conflicting notion of "signed in".
