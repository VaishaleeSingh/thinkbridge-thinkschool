# Day 17 — Deploy to Azure Static Web Apps: agent brief

This is the brief handed to the coding agent. It is written to be executable
without me answering follow-up questions, and to make the two things that are
easy to fake — the managed-identity call and the Lighthouse number — impossible
to fake.

## Role and working agreement

Build and deploy the deployment layer. Work only in `Day13/quotes-web`,
`Day17/`, `.github/workflows/`, and the auth wiring in
`Day7/piece2/QuotesApi`. Do not commit, push, branch, or touch
`node_modules`, `.angular/`, or `dist/`. Read the real source before changing
anything; the repo has load-bearing comments and several of them record
regressions that were already caught once.

Do not invent hostnames, tenant ids, or endpoints. Every value you need is
listed below or is in the repo.

## Target

- **Live URL:** `https://quotes.<my-domain>` (custom domain on Azure Static Web
  Apps, Standard plan). The `*.azurestaticapps.net` default hostname must also work.
- **Front end:** `Day13/quotes-web` — Angular 21, zoneless, signals only.
  Build with `ng build` (production is the default configuration); output is
  `dist/quotes-web/browser`.
- **Lighthouse:** ≥ 95 on all four categories, mobile preset, measured against
  the custom domain.

## The API to call — real endpoints, no mocks

Live Week-1 `QuotesApi`:
`https://quotes-api-cowork.whitestone-71ebd55e.centralindia.azurecontainerapps.io`

Endpoints in use by the front end (all under `/api`):

- `POST /api/auth/register`, `/api/auth/login`, `/api/auth/refresh`, `/api/auth/logout`
- `GET|POST /api/quotes`, `GET|PUT|DELETE /api/quotes/{id}`
- `GET|POST /api/collections`, `GET /api/collections/{id}`,
  `POST /api/collections/{id}/items`, `DELETE /api/collections/{id}/items/{quoteId}`,
  `DELETE /api/collections/{id}`
- `GET /health`, `/health/live`, `/health/ready` — for verification only, not proxied

`/api/diagnostics/*` exists on the API. It must **not** be reachable through the
deployment. Proxy an allowlist of path prefixes (`auth`, `quotes`,
`collections`), not a blanket pass-through.

## Auth model: managed identity, not a secret

Entra facts (from `Day7/piece2/QuotesApi/appsettings.json` — all non-secret):

- Tenant: `f774bb68-0575-4cd2-9d4c-3b4e593d1110`
- API app registration client id: `91566dbd-d857-488a-858d-475e60b309b7`
- API audience: `api://quotes-api/access`

**A browser cannot hold a managed identity.** Do not attempt an SPA-side MSAL or
client-credentials flow; do not put a client secret, certificate, or client
assertion anywhere in `Day13/quotes-web`, in `staticwebapp.config.json`, in SWA
app settings, or in the repo. If you conclude you need a secret, stop and say so
rather than adding one.

The required shape:

1. A **linked** Azure Functions backend (bring-your-own, .NET 8 isolated) with a
   **system-assigned managed identity**. Not SWA's built-in managed functions —
   those run in a Microsoft-managed subscription and have no identity you can
   assign or use. Linked backends require the SWA **Standard** plan.
2. That Function App exposes one catch-all `ANY /api/{*path}` proxy. It acquires
   a token with `DefaultAzureCredential().GetTokenAsync(new TokenRequestContext(
   ["api://quotes-api/access/.default"]))` and sends it as
   `Authorization: Bearer …` to the Container App.
3. The **user's** first-party JWT (issued by `/api/auth/login`, already handled
   by `core/interceptors/auth-interceptor.ts`) is forwarded in
   `X-Forwarded-Authorization`. The API must ignore that header unless the
   request also carried a valid app-only MI token — otherwise it is an
   impersonation hole.
4. On the API side: add an app role (`Quotes.Proxy`) to the existing app
   registration, assign it to the Function App's MI service principal, and gate
   the app-only path on that role **plus** an allowlist of caller object ids.
   Do not weaken or bypass the Day 3 resource-based ownership checks on
   collections — the user principal for those comes from the forwarded token.

Only non-secret app settings on the Function App: `Api__BaseUrl`,
`Api__Audience`. Nothing else.

`environment.production.ts` already has `apiBaseUrl: ''` (same-origin) — leave it
alone. That is what makes the SPA hit the BFF with no code change and removes
CORS from production entirely.

## Lighthouse: fix this before deploying, not after

`Day13/quotes-web/public` is 2.0 MB of unoptimised JPEG:
`quotes-hero-bg.jpg` is 417 KB and `quote-backgrounds/mountain-{1..6}.jpg` are
149–359 KB each. That is the whole reason a ≥ 95 performance score is currently
at risk — the JS side is fine (largest chunk 178 KB against a 500 KB budget).

Required:

- AVIF + WebP with JPEG fallback via `<picture>`; hero ≤ 60 KB at 1x.
- 640/1024/1600 px variants with `srcset`/`sizes`.
- Hero: `fetchpriority="high"` + `<link rel="preload">` (it is the LCP element).
  The six card backgrounds: `loading="lazy" decoding="async"`.
- Explicit dimensions or `aspect-ratio` on all of them (CLS).
- Add a `<meta name="description">` to `src/index.html` — it is missing, and SEO
  also has to clear 95.
- Security headers via `globalHeaders` in `staticwebapp.config.json` for
  Best-practices: `X-Content-Type-Options`, `Referrer-Policy`, and a CSP.
  `style-src` needs `'unsafe-inline'` (Angular inlines component styles);
  `script-src` must not.

Do not remove images or replace them with solid colours to win the score. The
design stays.

## SWA config gotchas that must be handled deliberately

- `navigationFallback.exclude` **must** include `/api/*`. Without it the SPA
  fallback returns `index.html` with a 200 for failed API calls, and the app
  shows an empty list with no error.
- `host.json` must keep the default `routePrefix: "api"` — the SWA link depends on it.
- `api_location` in the deploy workflow must be `''`. A linked backend and a
  managed API cannot coexist.
- PR preview environments do not support linked backends. Expected; document it
  rather than debugging it.
- Do not put IP restrictions or Private Link on the Function App — unsupported
  for linked backends.

## CI

New workflow `.github/workflows/day17-swa-deploy.yml`. Do **not** modify
`ci.yml`; it is the .NET gate and has no business knowing about the front end.
The workflow lints, runs the Angular tests, enforces the image budget, deploys
via `Azure/static-web-apps-deploy@v1`, then runs Lighthouse CI against the
deployed URL and **fails the build below 0.95** on any category. Deploy the
Function App separately (OIDC federated credentials, not a publish profile
secret).

## Required verification — I will not take the diff's word for any of this

1. **Live URL loads.** `curl -sI` on the custom domain: 200 + valid cert. Plus a
   screenshot of real quotes rendering.
2. **The outbound call carries a managed-identity token.** Log the decoded header
   and payload (never the signature) of the token the BFF sends, and show:
   `aud = api://quotes-api/access`, `roles = ["Quotes.Proxy"]`, **no `scp`, no
   `upn`** (that is what proves app-only rather than a user token), and `oid`
   equal to `az functionapp identity show --query principalId`.
3. **Zero secrets.** `gitleaks detect` clean on the branch;
   `az functionapp config appsettings list` showing only the two non-secret
   settings plus platform keys; `az staticwebapp appsettings list` empty.
4. **Negative test.** Remove the app-role assignment and confirm the call 403s.
   If it still works, the MI was not what authorised it and the whole claim is
   false.
5. **Ownership not regressed.** Signed in as user A, `GET /api/collections/{id}`
   for a collection owned by user B still returns 403/404 through the BFF.
6. **Lighthouse.** Mobile, four runs, median, against the custom domain, on both
   `/` and `/quotes`. Commit the full JSON report, not a screenshot of the
   circles.

## Done means

- The custom-domain URL serves the real app and it talks to the real API.
- The API call is authorised by a managed identity, proven by a decoded token
  whose `oid` matches the Function App's identity — not by assertion.
- No secret exists in the repo or in any app setting, proven by a scan and by a
  negative test.
- All four Lighthouse categories ≥ 95, with the JSON to check it.
- Every gap is written down as a gap. An unverified claim is worse than a
  missing one.
