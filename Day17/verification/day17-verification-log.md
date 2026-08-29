# Day 17 — verification log

What was actually run, what it returned, and — the part that matters more — what
could **not** be verified from where this was done and why.

Environment note up front, because it shapes everything below: this was built
from a cloud sandbox bridged to the Windows machine holding the repo. Neither
side can reach Azure. The bridge VM's egress allows the npm registry and nothing
else (`login.microsoftonline.com`, `pypi.org`, `aka.ms` and the live Container
App all fail to connect); the cloud sandbox reaches pypi and npm but its proxy
returns `403` for `login.microsoftonline.com`. There is no `az`, `func` or
`dotnet` on either. **No Azure resource was created, and no live URL exists yet.**
Every step below is either genuinely verified locally or marked NOT VERIFIED.

---

## 1. Front end builds, lints and tests with every Day 17 change in place

VERIFIED. Run in a Linux copy of `Day13/quotes-web` (the repo's `node_modules`
is a Windows install — `@esbuild/win32-x64` — so it cannot be built in the bridge
VM; a separate `npm ci` tree was used and the repo's own `node_modules` was left
untouched).

```
npx ng lint          → All files pass linting.
npx ng test          → Test Files 14 passed (14) | Tests 83 passed (83)
npx ng build         → Application bundle generation complete.
                       Initial total 282.62 kB raw / 78.89 kB transfer
```

Nothing was relaxed to make these pass. The 83 tests are the pre-existing suite.

## 2. `npm ci` was broken on Linux, and is now fixed

VERIFIED, and worth calling out because it would have failed the very first CI
run rather than anything later.

```
npm error `npm ci` can only install packages when your package.json and
npm error package-lock.json ... are in sync.
npm error Missing: @emnapi/core@1.11.3 from lock file
npm error Missing: @emnapi/runtime@1.11.3 from lock file
```

`package-lock.json` was generated on Windows and never contained the two
packages that only the `linux-x64` optional-dependency graph pulls in. The
symptom reads like a dependency mistake; it is a platform one. Regenerating the
lockfile on Linux was checked to be **purely additive** before committing it:

```
scratch-only (added by the Linux install): ['node_modules/@emnapi/core', 'node_modules/@emnapi/runtime']
repo-only  (would be lost):                []
lockfileVersion 3 → 3
```

`rm -rf node_modules && npm ci` then succeeded (584 packages). The Windows
install is unaffected — nothing was removed.

## 3. Image payload

VERIFIED by measurement, not by eye.

| | before | after | what a browser downloads |
|---|---|---|---|
| `quotes-hero-bg.jpg` | 417.1 kB | 93.6 kB JPEG + 48.9 kB AVIF | **48.9 kB** (AVIF) |
| `mountain-1` | 211.6 kB | 37.6 + 22.0 | 22.0 kB |
| `mountain-2` | 317.1 kB | 61.5 + 35.7 | 35.7 kB |
| `mountain-3` | 288.4 kB | 47.6 + 28.8 | 28.8 kB |
| `mountain-4` | 234.0 kB | 40.5 + 24.5 | 24.5 kB |
| `mountain-5` | 359.0 kB | 108.5 + 62.8 | 62.8 kB |
| `mountain-6` | 148.8 kB | 30.0 + 16.7 | 16.7 kB |
| `public/` total on disk | **2.0 MB** | **712 kB** | |

Re-encoded from the committed originals with `sharp` (hero to 1280 px wide,
cards to 900 px — the cards render at most ~600 CSS px in the grid, so the
1600 px sources were 2.7x oversized). The originals were recovered from
`git show` rather than kept as a second copy on the branch.

### What was deleted, and the argument for each

- **All seven `.webp` files.** Provably never served. `image-set()` with `type()`
  needs Chrome/Edge 113+, Firefox 113+, Safari 17+; AVIF is supported from
  Chrome 85+, Firefox 93+, Safari 16.4+. AVIF support is therefore a strict
  superset of the support needed to read the `image-set()` at all, so the WebP
  tier could never once be the format chosen — ~300 kB of build output no
  browser would ever request. Confirmed against the build:
  `grep -c webp dist/quotes-web/browser/*` → no matches anywhere.
- **`quotes-hero-bg.svg`.** Unreferenced: `grep -rn "hero-bg.svg" src/` returns
  nothing. The hero is the `.jpg`/`.avif` pair.
- **`Day17/original-images/`** and **`Day17/verification/dist-snapshot.tgz`.**
  Working copies and a build artefact; neither belongs on the branch.

Kept: `favicon.ico` and `quotes-brand-mark.svg` (both referenced —
`index.html` and `main-layout.html`), and every `.jpg`, because the JPEG is the
value the API actually stores in `backgroundImageUrl` and the only tier a
non-supporting browser can reach.

## 4. Three components rendered a quote background; only one had been fixed

VERIFIED and corrected. The first pass added `image-set()` to `quote-card` only.
`grep` for the raw-URL consumers found two more — `quote-preview-dialog.html` and
`quote-detail-page.html` — both still building `url(...)` from the `.jpg` path,
so the detail page (a Lighthouse-measurable route) would have kept downloading
the JPEG and quietly undone the optimisation there.

`quote-card` and `quote-preview-dialog` also each held their own inlined copy of
the URL-resolution logic, despite the comment on `resolveQuoteBackgroundUrl` in
`core/models/quote.ts` explaining that it lives there precisely so three copies
do not exist. The rule now lives in one place — `quoteBackgroundImageCss`, beside
`resolveQuoteBackgroundUrl` — and all three components call it. Lint and all 83
tests still pass after the refactor.

## 5. `staticwebapp.config.json` behaves as intended

VERIFIED against the real production build through `swa-emulate.mjs`
(`Day17/verification/swa-emulate.mjs`), which applies the actual config file:
`globalHeaders`, the per-route `Cache-Control` rules, `navigationFallback` with
its `/api/*` exclusion, and the `.avif` MIME type.

```
GET /                      → 200, Cache-Control: no-cache, no-store, must-revalidate
GET /api/quotes            → 404            (NOT index.html with a 200)
GET /quotes/100001         → 200 index.html (SPA deep link)
GET /quotes-hero-bg.avif   → Content-Type: image/avif
security headers present   → X-Content-Type-Options, Referrer-Policy,
                             Permissions-Policy, Strict-Transport-Security, CSP
```

The `/api/quotes` → 404 line is the one that matters. Without `/api/*` in
`navigationFallback.exclude`, a failed API call returns the app shell with status
200, `HttpClient` tries to parse HTML as JSON, and the list renders empty with no
error anywhere — a failure mode that looks like the API returning HTML.

**A bug this run actually caught:** the first config had a `no-store` rule for
`/index.html` but none for `/`, and `/` is what a browser requests. The emulator
showed `/` served with `public, max-age=3600` — meaning a deploy would ship new
chunk hashes that returning browsers cache past. A `"/"` route was added.

## 6. Lighthouse

VERIFIED, with a stated limitation. Full JSON reports are committed beside this
file (`lighthouse-mobile.report.json`, `lighthouse-desktop.report.json`,
`lighthouse-summary.json`) rather than a screenshot of the four circles, because
the JSON is what lets someone else check the number.

Lighthouse 13.4.1, headless Chromium, four mobile runs plus one desktop:

| run | perf | a11y | best-practices | SEO | FCP | LCP | CLS |
|---|---|---|---|---|---|---|---|
| mobile 1 | 97 | 100 | 100 | 100 | 1.9 s | 2.3 s | 0 |
| mobile 2 | 96 | 100 | 100 | 100 | 1.9 s | 2.4 s | 0 |
| mobile 3 | 97 | 100 | 100 | 100 | 1.9 s | 2.3 s | 0 |
| mobile 4 | 97 | 100 | 100 | 100 | 1.9 s | 2.3 s | 0 |
| desktop | 100 | 100 | 100 | 100 | 0.6 s | 0.7 s | 0 |

**Median mobile performance 97. All four categories ≥ 95 on every run.**

### Two things the number depends on, both found by measuring

**Brotli.** The first mobile runs scored **85**, and the reason was the harness,
not the app: the emulator served uncompressed, so Lighthouse saw 338 kB of
JS/CSS where Azure SWA's edge would send ~79 KB — and on the throttled slow-4G
profile that alone cost 12 points (FCP 3.1 s → 1.9 s once brotli was added).
Recorded because a local Lighthouse run against an uncompressed dev server will
under-report SWA by roughly that much, and "we lost 12 points" is a very
plausible thing to spend a day chasing in the application.

**A real CSP violation.** Best-practices sat at **92** with:

```
Refused to execute inline event handler because it violates the following
Content Security Policy directive: "script-src 'self'"
```

Angular's `inlineCritical` optimisation emits
`<link rel="stylesheet" ... media="print" onload="this.media='all'">`, and an
inline event handler is blocked by `script-src 'self'`. Two fixes were tried:

1. `inlineCritical: false` — removed the violation (best-practices → 100) but
   made `styles.css` render-blocking for 152 ms. Trading one audit for another.
2. Keep `inlineCritical` on and allow exactly that handler:
   `script-src 'self' 'unsafe-hashes' 'sha256-MhtPZXr7+LpJUY5qtMutB+qWfQtMaPccfe7QXtCcEYc='`
   — the SHA-256 of the literal string `this.media='all'`.

Option 2 was taken: best-practices 100 **and** the critical CSS still inlined.
Note `'unsafe-hashes'` is required for event-handler attributes specifically; the
hash alone does not apply to them.

### The limitation, stated plainly

These runs are **unauthenticated**. `authGuard` redirects to `/sign-in`, so what
is measured is the app shell plus the sign-in page — which has no images at all.
The hero and the six card backgrounds live on `/quotes`, behind the guard, and
the API is unreachable from here, so **the authenticated `/quotes` page has not
been measured**. The image work above is verified as a payload reduction, not yet
as a score on the page that carries it. Running Lighthouse against `/quotes`
needs a signed-in session against the live API and a Puppeteer login script in
Lighthouse CI; that is step 10 of the plan and is not done.

## 7. NOT VERIFIED — everything requiring Azure

Stated as gaps rather than glossed. An unverified claim is worse than a missing
one.

| Claim | Status | Why |
|---|---|---|
| Live URL loads over the custom domain | **NOT VERIFIED** | No SWA resource exists. No Azure CLI, and `login.microsoftonline.com` is blocked from both machines. |
| The call to the API carries a managed-identity token (`roles` present, no `scp`/`upn`, `oid` = Function App `principalId`) | **NOT VERIFIED** | Needs a deployed Function App with its identity assigned. `LogTokenShape` in `ApiProxyFunction.cs` exists specifically to produce this evidence on the first live call. |
| Negative test: removing the app-role assignment makes the call 403 | **NOT VERIFIED** | Same reason. This is the test that makes the MI claim real rather than incidental — it must not be skipped. |
| Zero secrets in app settings (`az functionapp config appsettings list`) | **NOT VERIFIED** | No resource to query. The design has exactly two non-secret settings — `Api__BaseUrl`, `Api__Audience`. |
| Zero secrets in the repo | **PARTIALLY VERIFIED** | Reviewed by hand: no secret is introduced by any Day 17 file. `gitleaks` was not available to run. |
| Ownership checks still enforced through the BFF (user A cannot read user B's collection) | **NOT VERIFIED** | The single most important regression risk in this design, and it needs a live two-user test. |
| **The BFF compiles at all** | **NOT VERIFIED** | No .NET SDK on either machine. `Day17/api-bff/*.cs` has never been through a compiler. Package versions in `QuotesBff.csproj` were written from knowledge, not resolved against NuGet. Expect to fix build errors on the first `dotnet build`. This is the same gap Day 13 hit and recorded for its C# changes. |
| The QuotesApi-side auth change | **NOT WRITTEN** | Plan §3.3 specifies it (app role `Quotes.Proxy`, app-only token gating, `X-Forwarded-Authorization` honoured only behind that gate). No code was written for it, because writing an auth change that cannot be compiled or tested is worse than not writing it. |
| GitHub Actions workflow runs green | **NOT VERIFIED** | Not pushed. YAML was checked for syntax by eye only; the `npm ci` blocker it would have hit first is fixed (§2). |

## 8. What the next session has to do first

1. `dotnet build Day17/api-bff` — expect and fix real errors.
2. `az staticwebapp create --sku Standard` (Free tier cannot have a linked backend).
3. Function App + `az functionapp identity assign`; record `principalId`.
4. Entra: app role `Quotes.Proxy` on app `91566dbd-…`, assigned to that principal.
5. Write and deploy the QuotesApi auth change (plan §3.3).
6. `az staticwebapp backends link`, then the custom domain.
7. The four verification items in §7 that carry the exercise's actual claim:
   decoded token shape, `oid` match, the 403 negative test, and the cross-user
   ownership test.
