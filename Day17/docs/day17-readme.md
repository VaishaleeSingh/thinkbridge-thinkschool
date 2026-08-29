# Day 17 — Deploy to Azure Static Web Apps

The exercise: get the Angular 21 front end live on Azure Static Web Apps behind a
custom domain, calling the real Week-1 API with a **managed identity and no
stored client secret**, at Lighthouse ≥ 95 — and direct an agent to do the deploy
rather than hand-rolling it.

## Read in this order

| File | What it is |
|---|---|
| `day17-swa-agent-brief.md` | **The brief.** What the agent was told, including the constraints that make the two fakeable claims (the MI call, the Lighthouse number) unfakeable. |
| `day17-implementation-plan.md` | The plan, with the sections that were wrong revised in place rather than quietly corrected. §0 is the load-bearing one. |
| `day17-staticwebapp-config-notes.md` | Why every line of `staticwebapp.config.json` is there. |
| `../verification/day17-verification-log.md` | What was actually run and what it returned — **including a table of everything that is NOT verified and why.** |
| `../api-bff/` | The BFF that holds the managed identity. Never compiled; see the log. |
| `../.env.example` | Every configuration value in one place, and — for the two real secrets — the mechanism that owns each instead. |

## The one-paragraph version

A browser cannot hold a managed identity — it is a credential issued by Azure's
identity endpoint to a compute resource, and anything shipped to a browser to
obtain one would be the secret the requirement forbids. So the token has to be
minted server-side, which on SWA means the `/api` backend. It must be a **linked**
backend (a real Function App with its own system-assigned identity), not SWA's
built-in managed functions — those run in a Microsoft-managed subscription with no
identity to assign, and linked backends need the **Standard** plan. The BFF
attaches the managed-identity token as `Authorization` (authenticating the calling
*application*) and forwards the user's existing first-party JWT as
`X-Forwarded-Authorization` (carrying the *user*, which Day 3's resource-based
ownership checks on collections still need). Collapsing those two into one token
would silently make every collection readable by everyone.

`environment.production.ts` already had `apiBaseUrl: ''` — same-origin, by a
deliberate decision on Day 13 — so the SPA needs no code change to talk to the
BFF, and CORS disappears from production entirely.

## Status

**Front end: done and measured.** Builds, lints, 83/83 tests pass. `public/` went
from 2.0 MB of unoptimised JPEG to **428 kB of WebP**; the hero went 417 kB →
96.8 kB. Lighthouse (mobile, three runs): **96 performance, 100 accessibility,
100 best-practices, 100 SEO.**

**Deployment: live, with the managed identity still outstanding.**

```
Live URL : https://yellow-river-074adb50f.7.azurestaticapps.net
SWA      : quotes-web-day17  (SKU Standard, rg-quotes-api)
Backend  : Container App -> quotes-api-cowork
```

The front end is deployed and the Week-1 API is linked as the SWA's `/api`
backend, so the browser talks to the API **same-origin** and there is no CORS
configuration in this deployment at all. Proven by the API's own
`application/problem+json` body arriving through `/api/auth/login` — see
`../verification/day17-verification-log.md` §6b.

Still outstanding, and stated plainly because a working deployment is not the
same as a finished exercise:

- **The managed identity is not in the path.** A linked Container App
  authenticates the platform hop; it does not mint an MI token for the API. That
  needs the BFF in `../api-bff/`, which has never been compiled — no .NET SDK was
  available where it was written. An SWA environment allows one backend, so
  adopting it means unlinking the Container App first.
- **The custom domain** is not set up; the default hostname is what is live.
- **Lighthouse has not been re-run against the live URL** — the ≥ 95 numbers are
  from the production build through a local emulation of the SWA edge.
- **A signed-in session was not tested end to end** (that needed real
  credentials).

Three findings from doing the work rather than planning it, all in the log:

1. `npm ci` was broken on Linux — the Windows-generated lockfile was missing two
   linux-only packages. That would have failed the first CI run. Fixed.
2. A strict `script-src 'self'` costs 8 Best-practices points, because Angular's
   critical-CSS inliner emits an inline `onload`. Fixed with a CSP hash rather
   than by turning the optimisation off.
3. Serving the build uncompressed scored 85 where the same build brotli-compressed
   scored 96. A local Lighthouse run against an uncompressed server under-reports
   SWA by about 11 points, which is a very plausible thing to spend a day chasing
   in the wrong place.

An AVIF tier and a three-format `image-set()` were also built and measured, then
removed in favour of WebP alone. `docs/day17-implementation-plan.md`, "Formats
considered", has the numbers and the argument.
