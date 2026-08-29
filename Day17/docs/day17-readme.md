# Day 17 — Deploy to Azure Static Web Apps

## The task

> Angular 21 front end live on Azure Static Web Apps with a custom domain,
> calling the real Week-1 API via **Managed Identity (no stored client secret)**,
> Lighthouse **≥ 95**. Direct the agent to do the deploy — SWA config, custom
> domain, the Managed-Identity wiring — then **verify and defend** it.

## Where things stand

| Deliverable | Status |
|---|---|
| Live SWA URL | ✅ `https://yellow-river-074adb50f.7.azurestaticapps.net` |
| Lighthouse ≥ 95 | ✅ **99 / 96 / 100 / 100** (authenticated `/quotes`, mobile, incognito) |
| Managed Identity, no stored secret | ⚠️ in the path for the **ACR image pull**; the BFF that puts it in the **API** path is written but not yet the linked backend |
| Custom domain | ❌ not configured |

![Lighthouse: Performance 99, Accessibility 96, Best Practices 100, SEO 100 on the authenticated /quotes page](./day17-lighthouse-quotes-incognito.png)

## The two documents

| file | what it is |
|---|---|
| `day17-submission.md` | **The answer.** The brief, the agent's output, and the verification log — the three things the exercise asks for, in that order. |
| `day17-readme.md` | This file: the task, the current state, and where the code lives. |

Everything else that used to live here (the implementation plan, the SWA config
notes, the agent brief, the redeploy runbook, the verification log) has been
folded into `day17-submission.md`. The originals are in git history.

## Where the code is

| | path |
|---|---|
| Front end | `Day13/quotes-web` (Angular 21, zoneless, signals) |
| SWA config | `Day13/quotes-web/public/staticwebapp.config.json` |
| Deploy workflow | `.github/workflows/day17-swa-deploy.yml` |
| .NET gate | `.github/workflows/ci.yml` |
| Managed-Identity proxy | `Day17/api-bff/` (.NET 8 isolated Function App) |
| Week-1 API | `Day7/piece2/QuotesApi` |

## Azure resources

| resource | group | note |
|---|---|---|
| `quotes-web-day17` | `rg-quotes-api` | Static Web App, **Standard** — Free has no linked backend |
| `quotes-api-azd` | `thinkschool-rg` | Container App, **current** image, ACR pull via managed identity |
| `quotes-api-cowork` | `thinkschool-azd-rg` | Container App, **14 Aug image** — predates `/api/auth/register` |
| `crqn4pdkxclsa6s` | `thinkschool-rg` | ACR holding the current image |
| `id-quotes-api-qn4pdkxclsa6s` | `thinkschool-rg` | user-assigned identity, holds **AcrPull** |
| `thinkschool-quotes-sql` | `thinkschool-rg` | Azure SQL — provisioned, not yet wired to the API |

## Three fixes that got the API running

All three are written up with their logs in `day17-submission.md` §3f.

1. **`ImagePullUnauthorized`** — the container app pulled from ACR with admin
   username + password (a stored secret) while a managed identity with AcrPull
   sat unused. Switched to the identity.
2. **Ingress `targetPort: 80`** — the app listens on 8080. Nothing reached it.
3. **`SQLite Error 14: unable to open database file`** — the connection string
   was a relative path resolving under `/app`, which the container runs as a
   non-root user that does not own. Pointed at `/tmp/quotes.db`.

## Still to do

1. Unlink the Container App and link the BFF, so the managed identity is in the
   **API** path and not just the image pull.
2. Move off `/tmp/quotes.db` — it dies with the container and is not shared
   between replicas, so scale is pinned to 1 replica as a stopgap.
   `thinkschool-quotes-sql` needs the managed identity added as a database user
   (`CREATE USER ... FROM EXTERNAL PROVIDER`).
3. Configure the custom domain.
