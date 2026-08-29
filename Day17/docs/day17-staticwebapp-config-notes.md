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

## `responseOverrides.404` — REMOVED, and why it was wrong

This file originally carried:

```jsonc
"responseOverrides": { "404": { "rewrite": "/index.html", "statusCode": 200 } }
```

on the reasoning that client-side routing needs it, because a hard refresh on
`/quotes/100001` is a real GET for a path with no file behind it.

That reasoning was wrong twice over, and it was caught against the deployed site
rather than by reading the file:

```
GET /api/quotes            -> 200 text/html   (should be 404)
GET /api/diagnostics/stats -> 200 text/html   (should be 404)
GET /quotes-hero-bg.jpg    -> 200 text/html   (should be 404 - deleted asset)
GET /nonexistent-asset.webp-> 200 text/html   (should be 404)
```

1. **It is redundant.** `navigationFallback.rewrite` already serves `index.html`
   for a navigation request with no file behind it. Deep links worked without
   this entry.
2. **It defeats `navigationFallback.exclude`.** The exclusion does its job —
   `/api/*` and the asset globs are *not* rewritten, so they 404 correctly — and
   then `responseOverrides` catches that 404 and rewrites it to `index.html` with
   a **200**. The two rules fight and `responseOverrides` wins, which reinstates
   the exact failure this file's `exclude` section exists to prevent: an API error
   arriving as HTML with a success status, `HttpClient` failing to parse it, and
   the list rendering empty with no error anywhere.

Removed. `navigationFallback` handles SPA deep links; everything genuinely
missing now returns a real 404. This mattered immediately: with a backend linked
to `/api`, a masked 404 would have made every backend failure look like an empty
response.

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
