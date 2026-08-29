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
from 2.0 MB of unoptimised JPEG to 712 kB of AVIF + JPEG; the hero a browser
downloads went 417 kB → 48.9 kB. Lighthouse (mobile, four runs, median): 97
performance, 100 accessibility, 100 best-practices, 100 SEO. Desktop: 100 across
the board. Full JSON reports are committed.

**Deployment: not done.** No Azure resource exists. Neither the sandbox this was
built in nor the bridged machine can reach `login.microsoftonline.com`, and
neither has `az`, `func` or `dotnet` — so nothing was created, the BFF has never
been through a compiler, and the API-side auth change is specified but not
written. The verification log names every one of those gaps.

Three findings from doing the work rather than planning it, all in the log:

1. `npm ci` was broken on Linux — the Windows-generated lockfile was missing two
   linux-only packages. That would have failed the first CI run. Fixed.
2. A strict `script-src 'self'` costs 8 Best-practices points, because Angular's
   critical-CSS inliner emits an inline `onload`. Fixed with a CSP hash rather
   than by turning the optimisation off.
3. Serving the build uncompressed scored 85 where the same build brotli-compressed
   scored 97. A local Lighthouse run against an uncompressed server under-reports
   SWA by about 12 points, which is a very plausible thing to spend a day chasing
   in the wrong place.
