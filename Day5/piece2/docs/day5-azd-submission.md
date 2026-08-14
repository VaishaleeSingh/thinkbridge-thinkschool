# Day 5, Task 4 — Mentor Submission (Deploy via `azd` CLI)

Deployment automation using the Azure Developer CLI (`azd`), a declarative `azure.yaml`, and Bicep Infrastructure as Code.

## GitHub Link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/tree/day5-azd-deployment/Day5/piece2

---

## Execution status: prepared, not yet run

**This is different from the other Day 5 submissions in this folder, and deliberately so.** The `azure.yaml` and `infra/*.bicep` files below are complete and were reviewed line by line against the project's actual container settings (`QuotesApi.csproj`), but `azd up` has **not** been executed against them yet, and this document does not contain a live FQDN, resource IDs, or health-check output.

The reason: this submission was prepared from an automated cloud sandbox that has no network route to any Azure endpoint (`login.microsoftonline.com`, `management.azure.com`, and ACR are all unreachable from it — confirmed, not assumed). `azd`/`az` were installed and validated locally, but authenticating and provisioning has to happen from a machine that can actually reach Azure — this one, run from a normal terminal.

**To complete this submission**, run the three commands in §5 of `docs/azd-deployment.md` from `Day5/piece2/` on a machine with Azure access, then replace this section with the real output: the resource group, the ACR login server, the Container App FQDN, and the result of each `curl` in the verification checklist below.

---

## What running `azd up` will do

```
azd auth login
azd env new thinkschool-azd --location centralindia
azd up
```

- Creates resource group `thinkschool-azd-rg` in `centralindia`.
- Provisions a Basic-SKU Azure Container Registry, a Log Analytics workspace, and a Container Apps Environment `thinkschool-azd-env`.
- Builds `QuotesApi.csproj` into an OCI image (alpine base, linux-musl-x64, per the container settings already in the `.csproj` — see `docs/containerising.md`) and pushes it to the new registry.
- Deploys it as Container App `quotes-api`: external ingress, target port 8080, liveness probe `/health/live`, readiness probe `/health/ready`, KEDA HTTP concurrency scale rule (min 1 / max 5 replicas, 50 concurrent requests).

## Verification checklist (fill in after running `azd up`)

```powershell
azd show
curl -i https://<fqdn>/health
curl -i https://<fqdn>/health/live
curl -i https://<fqdn>/health/ready
curl -i https://<fqdn>/api/quotes
```

| Check | Expected | Actual |
| --- | --- | --- |
| `azd show` — Container App status | Succeeded / Healthy | _pending_ |
| `GET /health` | `200 OK` | _pending_ |
| `GET /health/live` | `200 OK` | _pending_ |
| `GET /health/ready` | `200 OK` | _pending_ |
| `GET /api/quotes` | `200 OK` with quote payload | _pending_ |

---

## Notes for Mentor

### Bugs found and fixed while preparing this workspace

An earlier draft of these files (present before this session) had two problems that would have caused a deployment to look successful once and then quietly regress:

1. `main.bicep` hardcoded the same resource group name (`thinkschool-rg`) already used by the manual `az cli` exercise earlier in Day 5, and `resources.bicep` referenced that exercise's Container Apps Environment (`thinkschool-env`) as `existing` rather than provisioning its own. Running `azd down` on this workspace would have deleted infrastructure the other exercise still owns. Fixed by provisioning a dedicated `thinkschool-azd-rg` / `thinkschool-azd-env`.
2. `main.parameters.json` only mapped `environmentName` and `location`, omitting `quotesApiExists` and `quotesApiImageName`. Without those two parameters wired to azd's `${SERVICE_QUOTES_API_RESOURCE_EXISTS}` / `${SERVICES_QUOTES_API_IMAGE_NAME}` environment variables, every `azd provision` would silently reset the container app's image back to the `aci-helloworld` placeholder, even after a real image had been built and pushed. Fixed by adding both parameters.

Full detail on both, and on the rest of the Bicep, is in `docs/azd-deployment.md`.

### `azure.yaml`

```yaml
name: quotes-api
services:
  quotes-api:
    project: ./QuotesApi/QuotesApi.csproj
    language: dotnet
    host: containerapp
```

---

## What did you learn this session?

- `azd` collapses infrastructure provisioning, container build, registry push, and deployment into one command, but that only works correctly if `main.parameters.json` actually forwards the image name/exists flags `azd` computes after building each service — leaving them out doesn't fail loudly, it just makes `azd provision` keep resetting the deployed image.
- Reusing a resource group or Container Apps Environment across two independent `azd`/`az cli` exercises creates a hidden coupling: `azd down` deletes by tag, not by what it created itself, so anything else living in that resource group is at risk.
- Confirming what a sandboxed execution environment can actually reach on the network (rather than assuming a CLI install implies connectivity) is worth doing before treating any "it works" claim as verified.

## What would break this?

- Running `azd up` a second time without the `main.parameters.json` fix would revert the running container back to the `aci-helloworld` placeholder image, even though the first deploy looked correct.
- Pointing this workspace's `main.bicep` at the existing `thinkschool-rg`/`thinkschool-env` names would make `azd down` here capable of deleting the separate manual-exercise deployment.
- Mismatched target port or probe paths between `resources.bicep` and the app's actual container settings (port 8080, `/health/live`, `/health/ready`) would fail ingress routing or health probes exactly as in the manual exercise.
