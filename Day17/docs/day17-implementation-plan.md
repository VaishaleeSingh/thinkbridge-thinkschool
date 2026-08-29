# Day 17 — Deploy to Azure Static Web Apps: implementation plan

Branch: `day17-deploy-azure-static-web-apps`
Front end under deployment: `Day13/quotes-web` (Angular 21, zoneless, signals)
API being called: the Week-1 `QuotesApi`, already live on Azure Container Apps

---

## 0. The part of this exercise that is a trap, and how this plan gets out of it

The brief says the front end must call the Week-1 API "via Managed Identity (no
stored client secret)". Taken literally against an Angular SPA, that is not
possible, and any plan that does not say so out loud is going to produce a
deploy that quietly fails its own acceptance criterion.

A managed identity is a credential issued by the Azure IMDS/identity endpoint to
a **compute resource**. It is reachable only from inside that resource. A
browser is not an Azure resource; it has no identity endpoint, and anything
shipped to it to obtain a token would by definition be a secret in the bundle —
which is the exact thing the requirement forbids. Every Angular-SPA-calls-Entra
pattern (MSAL, auth-code + PKCE) is a *user* credential flow, not managed
identity.

So the token has to be minted by something server-side that sits between the
browser and the API. On Azure Static Web Apps that server-side thing is the
`/api` backend. This plan therefore deploys a **BFF (backend-for-frontend)**:

```
Browser (Angular)                 SWA edge              linked Functions BFF                 Week-1 QuotesApi
  fetch('/api/quotes')  ──────►  same-origin  ──────►  DefaultAzureCredential  ──────►  https://quotes-api-cowork
  Authorization: Bearer          route /api/*          .GetToken(              Authorization:   ...azurecontainerapps.io
  <first-party JWT>                                     "api://quotes-api/     Bearer <MI token>
                                                         access/.default")     X-Forwarded-
                                                       ▲ no secret anywhere      Authorization:
                                                       │ system-assigned MI      <user's JWT>
                                                       └── IMDS
```

Two credentials, two distinct jobs, and this separation is the thing to defend
in the write-up:

- **The managed-identity token authenticates the caller *application*.** It is
  app-only (client-credentials), carries an app role rather than a user, and is
  what makes "the call to my API carries a managed-identity token" true.
- **The first-party JWT still carries the *user*.** It has to: Day 3 built
  resource-based ownership checks on `/api/collections`, and those need to know
  *which* user is asking. Replacing the user's token with the MI token would
  silently turn every collection into everyone's collection — a security
  regression dressed up as a deployment.

### Why a *linked* backend and not SWA's built-in managed functions

This is the single most expensive thing to get wrong today, so it is decided
before any resource is created.

SWA offers two API shapes. The built-in **managed functions** run in a
Microsoft-managed subscription — they are not a Function App in your resource
group, you cannot assign them an identity, and `DefaultAzureCredential` inside
them has nothing to bind to. The SWA resource itself *can* have a
system-assigned identity on the Standard plan, but that identity is for
platform features (Key Vault references for auth provider secrets), and Key
Vault integration is explicitly unavailable for apps using managed functions.

Therefore: **bring-your-own / linked backend**, a real Azure Functions app in
my own resource group with its own system-assigned managed identity. Consequences
to plan for, not discover:

| Constraint | Consequence for this plan |
|---|---|
| Linked backends are **Standard plan only** | The SWA must be created with `--sku Standard`. Free tier is a dead end. |
| Route prefix must be `/api` | Keep `host.json`'s default `routePrefix`. Do not "tidy" it. |
| `api_location` must be `""` in the deploy workflow | The generated workflow will have it set; it must be blanked or SWA tries to build a managed API too. |
| One backend per environment | No second backend later; the BFF must own all of `/api/*`. |
| Not supported in PR preview environments | PR previews will 404 on `/api`. Expected, and must be stated rather than debugged. |
| Backend cannot use IP restrictions or Private Link | Don't lock the Functions app down that way. |

### Why this fits the existing code with almost no change

`Day13/quotes-web/src/environments/environment.production.ts` already sets
`apiBaseUrl: ''` — deliberately, per its own comment, so production requests are
same-origin `/api/...`. That is exactly what the BFF shape wants. The SPA needs
**zero** code change to point at the BFF, and CORS disappears entirely in
production because nothing is cross-origin any more.

---

## 1. Facts this plan is built on (read out of the repo, not assumed)

| Thing | Value | Source |
|---|---|---|
| Angular app | `Day13/quotes-web`, Angular 21.2, `@angular/build:application` | `package.json`, `angular.json` |
| Prod build output | `dist/quotes-web/browser` | `angular.json` |
| Prod API base | `''` (same-origin) | `src/environments/environment.production.ts` |
| API base injection | `API_BASE_URL` token | `core/services/api-base-url.ts` |
| User auth today | first-party JWT, 15-min access + refresh, in `authInterceptor` | `core/interceptors/auth-interceptor.ts` |
| Live Week-1 API | `https://quotes-api-cowork.whitestone-71ebd55e.centralindia.azurecontainerapps.io` | `Day5/piece2/docs/day5-azd-submission.md` |
| Entra tenant | `f774bb68-0575-4cd2-9d4c-3b4e593d1110` | `Day7/piece2/QuotesApi/appsettings.json` |
| Entra app (API) client id | `91566dbd-d857-488a-858d-475e60b309b7` | same |
| API audience | `api://quotes-api/access` | same |
| Existing CI | `.github/workflows/ci.yml` (.NET build/test + container smoke) | repo root |

### The real Week-1 endpoints the BFF must proxy

Read from `Day7/piece2/QuotesApi/Extensions/*EndpointExtensions.cs`:

- `POST /api/auth/register`, `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout`
- `GET /api/quotes`, `POST /api/quotes`, `GET /api/quotes/{id}`, `PUT /api/quotes/{id}`, `DELETE /api/quotes/{id}`
- `GET /api/collections`, `POST /api/collections`, `GET /api/collections/{id}`,
  `POST /api/collections/{id}/items`, `DELETE /api/collections/{id}/items/{quoteId}`, `DELETE /api/collections/{id}`
- `GET /health`, `/health/live`, `/health/ready` (not under `/api` — used for verification, not proxied)
- The `diagnostics` endpoints exist but are **not** to be exposed through the BFF.

The BFF is a single catch-all proxy (`/api/{*path}`), not one function per
endpoint. One function, one place where the token is attached, no chance of an
endpoint being added later and silently missing its auth wiring.

---

## 2. Lighthouse ≥ 95: the blocker that already exists

Measured before writing any deployment code, because "deploy then run Lighthouse
and hope" is how this requirement gets missed:

```
public/quotes-hero-bg.jpg            417 KB
public/quote-backgrounds/*.jpg     1,559 KB across 6 files (149–359 KB each)
public/favicon.ico                    15 KB
                                   ───────
public/ total                        2.0 MB
```

Seven unoptimised JPEGs, no modern format, no responsive variants. On a
Lighthouse mobile run this reliably costs points on *Largest Contentful Paint*,
*Serve images in modern formats*, and *Properly size images* — and LCP is the
heaviest single weight in the performance score. The JS side is fine (largest
chunk 178 KB, well inside the 500 KB initial budget in `angular.json`), so
**images are the whole problem**.

### What was actually done (this section was revised after doing it)

The plan above assumed `<picture>`/`srcset`. That was wrong about this codebase:
**all seven images are CSS backgrounds**, not `<img>` elements — the hero via
`quotes-page.scss`, the six card backgrounds via a URL the API stores in
`Quote.backgroundImageUrl`. A CSS background cannot use `<picture>`, cannot take
`srcset`, and cannot take `fetchpriority` or `loading="lazy"`. Half the planned
fix did not apply.

What was done instead:

1. **JPEG → WebP, and nothing else.** One format, plain `url(...)`, no
   `image-set()` and no multi-tier negotiation. An AVIF tier and a three-format
   `image-set()` were both built and measured first; both were removed in favour
   of this. The reasoning is in "Formats considered" below, because the simpler
   thing needs the argument, not the complicated one.
2. **Resize to what is actually rendered**: hero 1920 → 1280 px (a banner that
   never exceeds ~1200 CSS px); cards 1600 → 900 px (they render at most ~600 CSS
   px in the grid, so the sources were 2.7x oversized).
3. **Paths updated on both sides.** Front end:
   `QUOTE_BACKGROUND_OPTIONS` in `core/models/quote.ts` and the hero in
   `quotes-page.scss`. API: `DefaultBackgroundImageUrls` in
   `Day7/piece2/QuotesApi/Models/Quote.cs`, so new rows store `.webp`.
   `Quote.ResolveBackgroundImageUrl` validates the `/quote-backgrounds/` prefix
   only, not the extension, so no validation change was needed.
4. **Rows already in the database keep working.** The migration that seeded them
   (`20260824070320_AddQuoteBackgroundImage`) has already run, and editing an
   applied migration would not re-run it. `resolveQuoteBackgroundUrl` rewrites a
   bundled `.jpg` path to `.webp` on the way out instead — no data migration, and
   a no-op for anything written since. A remote URL is left exactly as stored.
5. **One resolver, three call sites.** The card, the preview dialog and the
   detail page each had their own inlined copy of the URL-resolution logic,
   despite the comment on `resolveQuoteBackgroundUrl` saying it exists so that
   three copies do not. They all call it now.

Result: `public/` 2.0 MB → 428 kB. The hero goes 417 kB → 96.8 kB.
Per-file numbers are in `Day17/verification/day17-verification-log.md` §3.

### Formats considered, and why WebP alone

AVIF is genuinely smaller — 48.9 kB against WebP's 96.8 kB for the hero — and a
four-variant Lighthouse experiment confirmed it: offering AVIF and WebP together,
Chromium fetched the AVIF every time and scored 100 mobile performance against 97
for WebP, with ~740 ms better LCP.

It was still dropped, for three reasons that outweigh 3 points on a synthetic
page:

- **A single format needs no negotiation mechanism.** `image-set()` was the only
  way to offer two formats to a CSS background, and it has a nasty failure mode:
  if a browser cannot parse the value, the entire `background-image` declaration
  is invalid and the element gets *no background at all* — not the fallback, not
  even the gradient layered with it. Guarding that took a `CSS.supports` probe, a
  duplicated cascade declaration in the SCSS, and five unit tests. All of that
  existed to protect a fallback that a single format does not need.
- **WebP still clears the requirement**, which is what actually had to be true:
  96 mobile performance, 100 accessibility, 100 best-practices, 100 SEO.
- **The payload win was mostly the resize, not the codec.** 2.0 MB → 428 kB is
  the bulk of it; AVIF would have taken 428 kB to ~247 kB, on assets that are not
  on the first paint of the measured route.

If the score ever needs the last few points, the AVIF tier is a known, measured
option — not a guess.

### CLS and the LCP element

CLS measured 0 on every run without any dimension work: these are backgrounds on
elements that already have layout, so there was nothing to reserve space for.
The planned `width`/`height` pass was unnecessary and was not done.

Accessibility / Best-practices / SEO also have to clear 95. Cheap, known items:
a `<meta name="description">` (absent from `src/index.html` today), and
`Content-Security-Policy` + `X-Content-Type-Options` headers from
`staticwebapp.config.json`. `lang="en"` and `color-scheme` are already there.

One of those turned out not to be cheap. A strict `script-src 'self'` puts
Best-practices at 92, not 100, because Angular's `inlineCritical` optimisation
emits `<link rel="stylesheet" media="print" onload="this.media='all'">` and an
inline event handler violates it. Turning `inlineCritical` off fixes the audit
and makes the stylesheet render-blocking instead — trading one audit for another.
The resolution is to keep the optimisation and allow exactly that one handler:
`'unsafe-hashes'` plus the SHA-256 of the literal `this.media='all'`.
`'unsafe-hashes'` is required because a plain hash does not apply to
event-handler attributes. Details in the verification log §6.

---

## 3. What gets built

### 3.1 `Day13/quotes-web/public/staticwebapp.config.json`

**In `public/`, not the project root.** SWA reads this file from the deployed
artifact, and the Angular build only copies `public/` into
`dist/quotes-web/browser/`. A copy at the project root is never uploaded and is
silently ignored — which looks exactly like the config having no effect. The
deploy workflow asserts the file is present in the build output for this reason.

The shipped file also needs a `"/"` route, not just `"/index.html"`: `/` is what
a browser actually requests, and with only the `/index.html` rule it was served
`public, max-age=3600` — so a deploy would ship new chunk hashes that returning
browsers cache straight past. That was caught by the local edge emulator, not by
reading the file.

Sketch (the committed file is authoritative; see also
`day17-staticwebapp-config-notes.md`):

```jsonc
{
  "navigationFallback": {
    "rewrite": "/index.html",
    "exclude": ["/api/*", "/*.{css,js,jpg,jpeg,png,svg,avif,webp,ico,webmanifest}"]
  },
  "globalHeaders": {
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "strict-origin-when-cross-origin",
    "Content-Security-Policy": "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'"
  },
  "routes": [
    { "route": "/index.html", "headers": { "Cache-Control": "no-cache" } },
    { "route": "/chunk-*.js", "headers": { "Cache-Control": "public, max-age=31536000, immutable" } }
  ],
  "responseOverrides": { "404": { "rewrite": "/index.html", "statusCode": 200 } },
  "platform": { "apiRuntime": "dotnet-isolated:8.0" }
}
```

Notes that matter: `navigationFallback.exclude` **must** list `/api/*`, or the
SPA fallback swallows API 404s and every failed call returns `index.html` with a
200 — a failure mode that looks like the API returning HTML. `unsafe-inline` for
styles is required because Angular inlines component styles; it is not required
for scripts and is deliberately not granted there.

### 3.2 The BFF — `Day17/api-bff/` (.NET 8 isolated Azure Functions)

One HTTP-triggered catch-all function. Behaviour:

```
Trigger:  ANY /api/{*path}
1. Reject any path not on an allowlist (auth|quotes|collections). Diagnostics stay unreachable.
2. token = credential.GetTokenAsync(new TokenRequestContext(
             new[] { $"{Audience}/.default" }))         // Audience from app setting, NOT a secret
3. Build downstream request to {ApiBaseUrl}/api/{path}, method + query + body copied.
4. Authorization: Bearer <token.Token>                  // the managed-identity token
5. X-Forwarded-Authorization: <inbound Authorization>   // the user's first-party JWT, if present
6. Copy back status, body, and a filtered set of response headers.
```

Deliberate choices:

- `DefaultAzureCredential`, not `ManagedIdentityCredential`, so the same code
  runs locally under `az login` / VS Code credential and in Azure under IMDS,
  with no `#if DEBUG` and no local secret.
- A `static readonly HttpClient` (or `IHttpClientFactory`) — one client for the
  process. A per-invocation `HttpClient` in a Function is the classic socket
  exhaustion bug.
- `Azure.Identity` caches the token and refreshes it before expiry; no hand-rolled
  cache, no token written anywhere.
- `host.json` keeps `routePrefix: "api"`. Non-negotiable for the SWA link.
- App settings: `Api__BaseUrl`, `Api__Audience`. Both are non-secret. Nothing
  else. This is checked in verification.

### 3.3 API-side change: accept the MI token *and* the forwarded user token

Smallest honest change to `Day7/piece2/QuotesApi`:

1. Add an app role (e.g. `Quotes.Proxy`) to the existing Entra app registration
   `91566dbd-…` and assign it to the BFF's managed-identity service principal.
   The MI token then arrives with `roles: ["Quotes.Proxy"]` and no `scp`.
2. Extend the existing `AzureAd` scheme to accept app-only tokens, gated on
   that role plus an allowlist of permitted caller object ids (`oid`/`azp`), so
   any other principal in the tenant that somehow gets a token still cannot
   call in.
3. When the request is app-only *and* carries `X-Forwarded-Authorization`,
   validate that inner token with the **existing** first-party JWT validator and
   use its principal as the user. If the outer app-only token is absent, the
   forwarded header is ignored entirely — otherwise the header becomes a free
   impersonation primitive.

That third point is the one to state explicitly in the submission. A forwarded
identity header is only safe when the transport is itself authenticated, and
here it is: the MI token proves the request came from the BFF.

**Alternative considered and rejected for today:** put a federated identity
credential on the app registration so the managed identity can perform a proper
on-behalf-of exchange, giving a single token carrying the real user. That is the
more correct long-term design, but it needs SWA Entra Easy Auth in front (so the
browser identity is an Entra identity, replacing the Day 3 first-party JWT
login), which is a front-end auth rewrite, not a deployment. Noted as follow-up.

### 3.4 CI/CD — `.github/workflows/day17-swa-deploy.yml`

A **new** workflow, not an edit to `ci.yml`. `ci.yml` is the .NET gate and has
no reason to know about the front end.

```yaml
on:
  push:
    branches: [ main, day17-deploy-azure-static-web-apps ]
    paths: [ 'Day13/quotes-web/**', 'Day17/api-bff/**', '.github/workflows/day17-swa-deploy.yml' ]
  pull_request:
    types: [opened, synchronize, reopened, closed]
    branches: [ main ]

jobs:
  build_and_deploy:
    steps:
      - checkout
      - setup-node 22
      - npm ci                  (working-directory Day13/quotes-web)
        # This fails on ubuntu-latest until package-lock.json is regenerated on
        # Linux: the Windows-generated lock is missing @emnapi/core and
        # @emnapi/runtime, which only the linux-x64 optional graph pulls in.
        # Fixed on this branch; see verification log §2.
      - npm run lint && npx ng test --watch=false
      - image optimisation step (sharp) — fails if hero > 60 KB
      - Azure/static-web-apps-deploy@v1
          app_location: 'Day13/quotes-web'
          output_location: 'dist/quotes-web/browser'
          api_location: ''            # linked backend — must be empty
          azure_static_web_apps_api_token: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN }}
      - lighthouse-ci against the deployed URL, assert >= 0.95 on all four categories
```

The BFF deploys separately (`Azure/functions-action@v1` with OIDC federated
credentials, or `func azure functionapp publish`) — **not** via the SWA action,
because `api_location` is empty by design.

`secrets.AZURE_STATIC_WEB_APPS_API_TOKEN` is a deployment token, not an
application secret: it authorises CI to upload static content and grants nothing
against the API. It is created by Azure when the SWA resource is made, lives only
in GitHub secrets, and the distinction gets one sentence in the write-up so the
"zero secrets" claim is precise rather than overstated.

### 3.5 Custom domain

Assuming an owned domain `quotes.<your-domain>` (subdomain, not apex — apex on
SWA needs ALIAS/ANAME support or Azure DNS, and a subdomain is a plain CNAME):

1. `az staticwebapp hostname set -n <swa> -g <rg> --hostname quotes.<domain>`
   → returns a `TXT` validation token.
2. At the registrar: `TXT` record for validation, then
   `CNAME quotes → <swa-default-hostname>.azurestaticapps.net`.
3. Wait for validation; SWA provisions a free managed certificate automatically.
4. Re-run Lighthouse against the **custom** domain, not the default hostname —
   the cert and any redirect chain are part of the score.

---

## 4. Execution order

| # | Step | Gate before moving on |
|---|---|---|
| 1 | Fix the images + add `<meta description>` in `Day13/quotes-web` | Local `npx lighthouse` on the prod build ≥ 95 in all four categories, *before* any Azure resource exists |
| 2 | Write `staticwebapp.config.json` | `swa start` locally serves the SPA and 404s `/api/*` rather than returning `index.html` |
| 3 | Scaffold + run the BFF locally | `curl localhost:7071/api/quotes` returns real quotes; the token it used decodes to an app-only Entra token |
| 4 | Create Azure resources: `az staticwebapp create --sku Standard`; Function App; `az functionapp identity assign` | `az functionapp identity show` returns a principal id |
| 5 | Entra: add `Quotes.Proxy` app role, assign to the MI principal | `az rest` app-role-assignment listing shows the MI |
| 6 | API-side auth change + redeploy the Container App | A hand-fetched MI token calls the live API and gets 200; a token without the role gets 403 |
| 7 | `az staticwebapp backends link` | `/api/quotes` on the SWA default hostname returns real data |
| 8 | Add the GitHub workflow + deployment-token secret; push | Green run, live default hostname |
| 9 | Custom domain + DNS | `https://quotes.<domain>` serves with a valid cert |
| 10 | Verification sweep (§5) | All evidence captured in `Day17/verification/` |

Steps 1–3 need no Azure subscription at all and are done first on purpose: every
requirement that can be proven locally is proven before spending cloud
round-trips on it.

---

## 5. Verification — what will actually be proven, and how

Recorded to `Day17/verification/`, one file per claim. A claim with no artefact
is written up as unverified rather than asserted.

**A. The live URL loads.**
`curl -sI https://quotes.<domain>` → 200, TLS issuer, HSTS. Plus a browser
screenshot of the quotes list rendering real rows.

**B. The call to the API carries a managed-identity token.**
This is the claim most likely to be hand-waved, so three independent pieces:
1. BFF log line with the *decoded header and payload* of the outbound token
   (never the signature): `iss` = `https://sts.windows.net/f774bb68-…/`,
   `aud` = `api://quotes-api/access`, `roles` = `["Quotes.Proxy"]`,
   **no `scp`, no `upn`** — that combination is what makes it app-only, i.e.
   managed identity rather than a user.
2. `oid` in that token equals the output of
   `az functionapp identity show --query principalId` — the token belongs to
   *this* Function App's identity and nothing else.
3. QuotesApi-side log of the same request showing the role-gated scheme accepted
   it, and the user resolved from `X-Forwarded-Authorization`.

**C. Zero secrets in the repo.**
- `gitleaks detect` (or `git log -p -S` for the known values) across the branch → clean.
- `az functionapp config appsettings list` → only `Api__BaseUrl`, `Api__Audience`
  and the platform's own `AzureWebJobsStorage`/runtime keys. **No** `CLIENT_SECRET`,
  no `AZURE_CLIENT_SECRET`, no connection string to the API.
- `az staticwebapp appsettings list` → empty.
- The negative test that makes it real: temporarily remove the app-role
  assignment, confirm the call 403s. If it still succeeded, something other than
  the MI was authorising it.

**D. Lighthouse ≥ 95.**
Mobile preset, four runs, median reported, against the custom domain. Full JSON
report committed, not just a screenshot of the four circles — the JSON is what
lets someone else check the number. Both `/` and `/quotes` measured.

Two things learned doing this locally, both of which change how the number is
read:

- **Compression is not optional in the harness.** Serving the build uncompressed
  scored 85; brotli-compressing it — which is what SWA's edge does — scored 96–97
  on the identical build. A local run against an uncompressed dev server
  under-reports SWA by roughly 12 points on the throttled mobile profile.
- **The unauthenticated run does not measure the page with the images on it.**
  `authGuard` redirects to `/sign-in`, which has no images at all. So a green
  unauthenticated Lighthouse run says nothing about the hero. Measuring `/quotes`
  requires a signed-in session, which means a Puppeteer login step in Lighthouse
  CI. Any Lighthouse claim for this app that does not say which route it measured
  is not a claim about the app.

**E. The things that will be honestly reported as limitations.**
- PR preview environments have no `/api` (linked-backend limitation) — the
  preview URLs are static-only.
- The deployment token is a real secret in GitHub Actions; the "no stored client
  secret" claim is about the API credential specifically.
- If the API-side change cannot be compiled here (no .NET SDK is available in
  this environment — the same gap Day 13 hit and documented), that is stated as
  unverified rather than glossed.

---

## 6. Risks, ranked by how likely they are to eat the day

1. **SWA created on Free tier.** Linked backends silently unavailable; the fix is
   to recreate the resource. Mitigated by `--sku Standard` in step 4 and an
   explicit `az staticwebapp show --query sku` check.
2. **Lighthouse stuck in the high 80s on images.** Mitigated by doing step 1
   first and gating on a local run.
3. **`api_location` left non-empty** in the generated workflow → SWA tries to
   build a managed API, conflicts with the link.
4. **`navigationFallback.exclude` missing `/api/*`** → API errors return
   `index.html` with 200, and the front end shows an empty list with no error.
5. **Entra app-role propagation delay.** Role assignments and token caching mean
   a fresh 403 can be stale rather than wrong. Wait, restart the Function App,
   re-check — do not "fix" it by adding a secret.
6. **`routePrefix` changed in `host.json`** → link works, every route 404s.
7. **Custom-domain TXT validation slow.** Start step 9 early; it is the only step
   whose latency is not under my control.

---

## 7. Deliverables for submission

- `Day17/docs/day17-swa-agent-brief.md` — the brief given to the agent
- `Day17/docs/day17-implementation-plan.md` — this file
- `Day13/quotes-web/staticwebapp.config.json`
- `.github/workflows/day17-swa-deploy.yml`
- `Day17/api-bff/` — the BFF, including the token acquisition
- The QuotesApi auth diff
- `Day17/verification/` — logs, decoded token headers, appsettings dumps, Lighthouse JSON
- `Day17/screenshots/` — live site, Lighthouse panel, Entra role assignment
