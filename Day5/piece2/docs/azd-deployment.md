# Deploying ASP.NET Core Container Apps via Azure Developer CLI (`azd`)

The **Azure Developer CLI (`azd`)** unifies infrastructure provisioning, container build, registry push, and deployment behind one command (`azd up`). Where Day 5's earlier exercise (`docs/azure-container-apps.md`, `scripts/deploy-aca.ps1`) drove each of those steps by hand with `az cli`, this piece drives the same end state from a declarative `azure.yaml` plus Bicep templates that `azd` reads and applies.

---

## 1. Why a separate resource group and environment

The manual exercise already created `thinkschool-rg` / `thinkschool-env` / a container app named `quotes-api` in `centralindia`. Pointing this `azd` workspace at the same names would mean `azd down` (which deletes everything tagged with its `azd-env-name`) could tear down infrastructure the other exercise still depends on, and vice versa. This workspace provisions its own resource group (`thinkschool-azd-rg`) and Container Apps Environment (`thinkschool-azd-env`), so the two exercises are fully independent and either can be torn down without touching the other.

---

## 2. `azure.yaml`

```yaml
name: quotes-api
services:
  quotes-api:
    project: ./QuotesApi/QuotesApi.csproj
    language: dotnet
    host: containerapp
```

- `project` points at the same `.csproj` that already carries the container build configuration described in `docs/containerising.md` (alpine `ContainerFamily`, port 8080, `ContainerRepository: quotes-api`). `azd` reuses that configuration rather than needing its own Dockerfile or image name.
- `host: containerapp` tells `azd` to build the project as an OCI container image and deploy it to Azure Container Apps (as opposed to `host: appservice` or `host: aks`).

An earlier draft of this file carried `metadata.template: app-service-dotnet-quickstart@0.0.1-beta` — a stray tag left over from scaffolding with the wrong azd starter template. It has been removed: this workspace was not created from that template, and `host: containerapp` already fully describes the target, so the tag added nothing but a misleading trail for anyone reading the file later.

---

## 3. Infrastructure as Code (`infra/`)

```
Day5/piece2/infra/
├── abbreviations.json    # Standard Azure resource-naming abbreviations
├── main.bicep            # Subscription-scope entry point: creates the resource group
├── main.parameters.json  # Wires azd's environment values into main.bicep's parameters
└── resources.bicep       # ACR, Log Analytics, Container Apps Environment, Container App
```

### `main.bicep`

Deploys at subscription scope so it can create the resource group itself (`thinkschool-azd-rg`), then hands off to `resources.bicep` scoped into that group. `resourceToken` is a hash derived from the subscription ID, environment name, and location — used to keep names like the ACR hostname globally unique without hardcoding anything.

### `resources.bicep`

Provisions, in order:

1. **Container Registry** (Basic SKU). `adminUserEnabled` is not needed for the app to pull its own image — see the identity note below — but Basic SKU registries do not support disabling it independently of `azd`'s own tooling expectations, so it is left as the default.
2. **Log Analytics workspace** and a **new** Container Apps Environment (`thinkschool-azd-env`) wired to it — this is a fresh environment, not a reference to the `thinkschool-env` created by the manual exercise (see §1).
3. **User-assigned managed identity** for the container app, plus an **`AcrPull` role assignment** scoping that identity to the registry. The container app's `registries` block authenticates with this identity rather than the registry's admin username/password, so no registry credential needs to exist as a Container Apps secret.
4. **The Container App itself** (`quotes-api`): external ingress on port 8080, the same `/health/live` and `/health/ready` probe paths the manual exercise used, and the same KEDA HTTP concurrency scale rule (50 concurrent requests, 1–5 replicas).

### The placeholder image, and why `main.parameters.json` needs two extra parameters

`resources.bicep` accepts `quotesApiExists` and `quotesApiImageName`, and falls back to the public `mcr.microsoft.com/azuredocs/aci-helloworld:latest` image when `quotesApiImageName` is empty. This fallback is intentional and standard for `azd`'s `host: containerapp` pattern: on the very first `azd provision`, no image has been built or pushed yet, so the container app has to reference *something* to be created at all.

What is not optional is wiring those two parameters through `main.parameters.json` to the environment variables `azd` sets automatically after building each service:

```json
"quotesApiExists": { "value": "${SERVICE_QUOTES_API_RESOURCE_EXISTS=false}" },
"quotesApiImageName": { "value": "${SERVICES_QUOTES_API_IMAGE_NAME}" }
```

An earlier draft of `main.parameters.json` only set `environmentName` and `location`, leaving both of these out. Without them, every `azd provision` (including the one implicitly run inside `azd up`) would re-apply the Bicep template with `quotesApiImageName` defaulting to empty every time — so the container app would keep resetting to the `aci-helloworld` placeholder on every run, regardless of how many times the real image was built and pushed. `azd deploy` would push the image and could still directly patch the running revision the first time, but any subsequent `azd provision`/`azd up` would silently revert it. This is the kind of bug that looks like a working deployment right up until someone re-runs `azd up` a second time.

---

## 4. `azd` command reference

| Command | Action |
| --- | --- |
| `azd auth login` | Authenticates `azd` against Azure AD (device code or browser). |
| `azd env new <name>` | Creates an isolated deployment environment (subscription, location, generated `.env` values). |
| `azd env select <name>` | Switches the active environment. |
| `azd provision` | Applies `infra/main.bicep` — creates/updates the resource group, ACR, environment, and container app shell. |
| `azd package` | Builds the container image from `QuotesApi.csproj` using .NET's SDK container support. |
| `azd deploy` | Pushes the built image to ACR and updates the Container App's revision. |
| `azd up` | `provision` + `package` + `deploy`, in that order, as one command. |
| `azd down` | Deletes every resource tagged with this environment's `azd-env-name` (i.e., everything in `thinkschool-azd-rg`). |

---

## 5. Running it

From `Day5/piece2/`:

```powershell
azd auth login
azd env new thinkschool-azd --location centralindia
azd up
```

`azd up` prints the resulting Container App FQDN at the end of the run. See `docs/day5-azd-submission.md` for the health-check commands to run against it.

---

## 6. Failure modes and mitigations

1. **Stale image on repeat runs**: as covered above, an incomplete `main.parameters.json` causes `azd provision` to silently reset the deployed image to the placeholder. Fixed here by threading `quotesApiExists`/`quotesApiImageName` through.
2. **Resource-group collision with the manual exercise**: reusing `thinkschool-rg`/`thinkschool-env` would let `azd down` delete infrastructure the manual exercise still owns (and vice versa). Fixed here by using a dedicated `thinkschool-azd-rg` / `thinkschool-azd-env`.
3. **ACR authentication drift**: because the container app authenticates to the registry via its managed identity (not the registry admin password), rotating or disabling the registry's admin account does not break pulls — only the `AcrPull` role assignment matters.
4. **First-run latency**: provisioning a new Log Analytics workspace and Container Apps Environment from scratch takes several minutes; subsequent `azd deploy`-only runs (no infrastructure change) are much faster.
5. **Ephemeral SQLite storage**: unchanged from the manual exercise — `/tmp/quotes.db` does not survive a replica restart or scale event. Azure SQL/PostgreSQL via the existing `QuotesApi.Migrations.SqlServer` project is the real answer for anything beyond this exercise.
