# Deploying ASP.NET Core Container Apps via Azure Developer CLI (`azd`)

The **Azure Developer CLI (`azd`)** unifies infrastructure provisioning, container build, registry push, and deployment behind one command (`azd up`). Where Day 5's earlier exercise (`docs/azure-container-apps.md`, `scripts/deploy-aca.ps1`) drove each of those steps by hand with `az cli`, this piece drives the same end state from a declarative `azure.yaml` plus Bicep templates that `azd` reads and applies.

This was run for real: `azd auth login`, `azd env new`, and `azd up` were executed against a live subscription, not just authored and left unrun. Three real, load-bearing bugs turned up doing that, none of them visible from reading the Bicep alone. All three are below, in the order they were hit.

---

## 1. Why a separate resource group, and why *not* a separate environment

The manual exercise already created `thinkschool-rg` / `thinkschool-env` / a container app named `quotes-api` in `centralindia`. The first draft of this workspace assumed both a dedicated resource group and a dedicated Container Apps Environment would keep the two exercises fully independent, so `azd down` here could never touch what the other exercise owns.

Half of that held up; half didn't survive contact with the subscription. The dedicated resource group (`thinkschool-azd-rg`) is real and correct -- `azd down` on this workspace only deletes what it provisions inside it. The dedicated *environment* is not: the first `azd up` failed provisioning with

```
MaxNumberOfRegionalEnvironmentsInSubExceeded
```

This subscription allows exactly one Container Apps Environment per region. `thinkschool-env` in `thinkschool-rg` already occupies `centralindia`, so a second, dedicated environment for this exercise was never available at any resource-group boundary -- not a missed optimization, a hard quota. `infra/resources.bicep` now references `thinkschool-env` as an `existing` resource, scoped into `thinkschool-rg`, instead of provisioning a new one. `azd down` here is unaffected: it still only deletes resources tagged with this workspace's `azd-env-name`, and the environment itself is referenced rather than owned, so it is never a deletion candidate.

One consequence of a shared environment: container app names must be unique *within the environment*, not just within a resource group. The first `azd up` also failed with `ContainerAppNameConflictInCluster` -- a container app literally named `quotes-api-azd` already existed in `thinkschool-env`, left over from a separate, concurrent attempt at this same exercise. It was confirmed (via `az containerapp replica list`) to be a different, broken deployment stuck in `ImagePullBackOff`, not this workspace's own resource, before renaming anything. This workspace's container app is therefore named `quotes-api-cowork`, not `quotes-api`.

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

- `project` points at the same `.csproj` that already carries the container build configuration described in `docs/containerising.md` (port 8080, `ContainerRepository: quotes-api`). `azd` reuses that configuration rather than needing its own Dockerfile or image name.
- `host: containerapp` tells `azd` to build the project as an OCI container image and deploy it to Azure Container Apps (as opposed to `host: appservice` or `host: aks`).

An earlier draft of this file carried `metadata.template: app-service-dotnet-quickstart@0.0.1-beta` -- a stray tag left over from scaffolding with the wrong azd starter template. It has been removed: this workspace was not created from that template, and `host: containerapp` already fully describes the target.

An earlier draft also carried a `hooks.postdeploy` block, an attempt to auto-correct the image-path bug in section 4 below with a shell script run after every deploy. It failed at run time (`'postdeploy' hook failed with exit code '0' ... fork/exec /bin/sh: not a directory`) -- azd's hook `run:` field did not accept a multi-line inline script the way it was written, and diagnosing azd's exact hook schema was not worth the extra live-deployment cycles it would have cost. The corrective command in section 6 is a documented manual step instead of an automated one; it is one line, it is idempotent, and it is verified to work.

---

## 3. Infrastructure as Code (`infra/`)

```
Day5/piece2/infra/
├── abbreviations.json    # Standard Azure resource-naming abbreviations
├── main.bicep            # Subscription-scope entry point: creates the resource group
├── main.parameters.json  # Wires azd's environment values into main.bicep's parameters
└── resources.bicep       # ACR, managed identity, existing environment reference, Container App
```

### `main.bicep`

Deploys at subscription scope so it can create the resource group itself (`thinkschool-azd-rg`), then hands off to `resources.bicep` scoped into that group. `resourceToken` is a hash derived from the subscription ID, environment name, and location -- used to keep names like the ACR hostname globally unique without hardcoding anything.

### `resources.bicep`

1. **Container Registry** (Basic SKU, `adminUserEnabled: true` left at its default -- the app itself authenticates via managed identity, not the admin account).
2. **Container Apps Environment**: an `existing` reference to `thinkschool-env` in `thinkschool-rg`, per section 1 -- not a new resource.
3. **User-assigned managed identity** for the container app, plus an **`AcrPull` role assignment** scoping that identity to the registry. The container app's `registries` block authenticates with this identity rather than the registry's admin username/password, so no registry credential needs to exist as a Container Apps secret. Verified live via `az role assignment list --scope <registry-id>`: the assignment's `principalId` matches the managed identity's own `principalId` (not its `clientId` -- `az role assignment list -o table`'s default "Principal" column resolves to the identity's `clientId` for display, which looks like a mismatch until you query `principalId` directly).
4. **The Container App** (`quotes-api-cowork`, section 1): external ingress on port 8080, `/health/live` and `/health/ready` probe paths matching the manual exercise, the same KEDA HTTP concurrency scale rule (50 concurrent requests, 1-5 replicas). The JWT signing key is a Container Apps secret whose value is `guid(subscription().id, resourceToken, 'jwt-secret')` -- deterministic per subscription/environment, never typed or pasted as a literal string anywhere in this repository or in a terminal. An earlier draft had this as a `@secure() param jwtSecret string` with a literal placeholder default; typing that placeholder into a live terminal during deployment was blocked by an automated safety check for looking like a credential, which is what prompted deriving it instead of supplying it.

### The placeholder image, and why `main.parameters.json` needs two extra parameters

`resources.bicep` accepts `quotesApiExists` and `quotesApiImageName`, and falls back to the public `mcr.microsoft.com/azuredocs/aci-helloworld:latest` image when `quotesApiImageName` is empty. This fallback is intentional and standard for `azd`'s `host: containerapp` pattern: on the very first `azd provision`, no image has been built or pushed yet, so the container app has to reference *something* to be created at all.

What is not optional is wiring those two parameters through `main.parameters.json` to the environment variables `azd` sets automatically after building each service:

```json
"quotesApiExists": { "value": "${SERVICE_QUOTES_API_RESOURCE_EXISTS=false}" },
"quotesApiImageName": { "value": "${SERVICES_QUOTES_API_IMAGE_NAME}" }
```

An earlier draft of `main.parameters.json` only set `environmentName` and `location`, leaving both of these out. Without them, every `azd provision` (including the one implicitly run inside `azd up`) would re-apply the Bicep template with `quotesApiImageName` defaulting to empty every time -- so the container app would keep resetting to the `aci-helloworld` placeholder on every run, regardless of how many times the real image was built and pushed. Fixed by adding both parameters. This bug and the one in section 4 are easy to conflate -- both produce a container app pointed at the wrong image -- but they are independent problems with independent fixes.

---

## 4. The image-path bug, and the corrective step it requires

This is the bug that actually blocked the first working deployment, and it is not about RBAC or identity at all, despite producing the same `ImagePullBackOff` symptom Container Apps shows for both.

`azd deploy` (and the deploy phase of `azd up`) builds the image via `dotnet publish` with a repository path it computes itself, of the shape `<image-repo>/<env-name-derived-service-name>` (concretely, in this workspace: `quotes-api/quotes-api-thinkschool-azd-cowork`). But `QuotesApi.csproj` pins `<ContainerRepository>quotes-api</ContainerRepository>` (see `docs/containerising.md` section 2 for why that property is set at all). An unconditioned property assignment inside the project file wins over the `-p:ContainerRepository=...` azd passes on the command line during evaluation, so the image is actually built and pushed to the plain `quotes-api` repository -- confirmed directly with `az acr repository show-tags -n <registry> --repository quotes-api`, which lists the tag. Meanwhile, azd's own tracked `SERVICES_QUOTES_API_IMAGE_NAME` -- the value threaded through `main.parameters.json` into `resources.bicep`'s `quotesApiImageName` -- still records the nested path azd *intended* to use, because that value is computed from azd's own naming convention rather than read back from what actually got pushed. The two disagree, and the container app ends up pointed at an image that was never built:

```
Container 'quotes-api' was terminated with exit code '' and reason 'ImagePullFailure'.
Image <registry>.azurecr.io/quotes-api/quotes-api-thinkschool-azd-cowork:azd-deploy-<tag> not found in registry.
ErrorMessage=Image not found or access denied.
Error: {"errors":[{"code":"MANIFEST_UNKNOWN","message":"manifest tagged by \"azd-deploy-<tag>\" is not found", ...}]}
```

Reproduced twice, on two separate `azd deploy` runs, each time with a different tag -- not a one-off. Removing `ContainerRepository`/`ContainerImageName` from the csproj entirely would fix it structurally (azd's own path would then apply cleanly, with nothing to conflict with it), but that property also controls the image name the manual exercise in `docs/containerising.md` produces locally (`docker images quotes-api`); removing it risks changing that exercise's documented output for a bug that only affects the azd path. It was left in place, and the correction is instead a documented manual step, run once after every `azd up`/`azd deploy`:

```powershell
az containerapp update `
  -g thinkschool-azd-rg `
  -n quotes-api-cowork `
  --image <AZURE_CONTAINER_REGISTRY_ENDPOINT>/quotes-api:<tag from SERVICES_QUOTES_API_IMAGE_NAME>
```

The tag itself is correct in both places -- only the repository path segment differs -- so the corrective image reference is always `<registry endpoint>/quotes-api:<same tag azd just used>`.

An automated fix was attempted first, as an `azure.yaml` `postdeploy` hook running this same `az containerapp update` automatically. It failed at run time with a hook-schema error (see section 2) rather than a logic error, and was reverted rather than spending further live deployment cycles reverse-engineering azd's exact hook syntax.

---

## 5. The Alpine / RID bug, and why it doesn't survive azd

`QuotesApi.csproj` originally pinned `<ContainerFamily>alpine</ContainerFamily>` and `<ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:10.0-alpine</ContainerBaseImage>`, exactly as `docs/containerising.md` documents for the manual, local build. That document is also explicit that Alpine only works when paired with the matching RID: `dotnet publish --os linux-musl --arch x64`, passed on the command line every time.

`azd`'s built-in .NET SDK container integration for `host: containerapp` / `language: dotnet` does not expose a way to pass that flag. Its own invocation, captured directly with `azd deploy --debug`:

```
dotnet publish <project> -r linux-x64 -c Release /t:PublishContainer \
  -p:ContainerRepository=... -p:ContainerImageTag=... -p:ContainerRegistry=... \
  --getProperty:GeneratedContainerConfiguration
```

`-r linux-x64` is hardcoded, and a command-line `-r` is a global MSBuild property -- nothing in the project file can override it, including a `<RuntimeIdentifier>linux-musl-x64</RuntimeIdentifier>` added specifically to try (it had no effect; confirmed by re-deploying and seeing the same crash). With `ContainerFamily: alpine` still set, the result is an Alpine (musl) base image running a glibc-built `libe_sqlite3.so`, and the container crashes on the first database call:

```
Unhandled exception. System.TypeInitializationException: The type initializer for
'Microsoft.Data.Sqlite.SqliteConnection' threw an exception.
System.DllNotFoundException: Unable to load shared library 'e_sqlite3'
Error relocating /app/libe_sqlite3.so: fcntl64: symbol not found
```

This is precisely the failure `docs/containerising.md` section 1 already documents and warns about -- azd simply has no way to apply the one-flag fix that section describes.

`ContainerFamily` and `ContainerBaseImage` have been removed from `QuotesApi.csproj`. This does not change the manual exercise's outcome: its documented command still requests the musl RID explicitly on the command line, and the .NET SDK resolves a `*-musl-x64` RID to the Alpine base image on its own, with or without `ContainerFamily` pinned in the project file -- that flag was redundant with the command-line RID, not load-bearing for it. What changes is only the azd path: with no family pinned, azd's own `-r linux-x64` now pairs with the SDK's default (Debian-based, glibc) base image, which actually matches the RID it publishes with, instead of a musl base paired with a glibc binary. Verified live: after removing both properties and re-running `azd deploy`, the container reached `Running` and all three health endpoints returned `200`.

---

## 6. `azd` command reference

| Command | Action |
| --- | --- |
| `azd auth login` | Authenticates `azd` against Azure AD (device code or browser). |
| `azd env new <name>` | Creates an isolated deployment environment (subscription, location, generated `.env` values). |
| `azd env select <name>` | Switches the active environment. |
| `azd provision` | Applies `infra/main.bicep` -- creates/updates the resource group, ACR, identity, and container app shell. |
| `azd package` | Builds the container image from `QuotesApi.csproj` using .NET's SDK container support. |
| `azd deploy` | Pushes the built image to ACR and updates the Container App's revision. |
| `azd up` | `provision` + `package` + `deploy`, in that order, as one command. |
| `azd down` | Deletes every resource tagged with this environment's `azd-env-name` (i.e., everything in `thinkschool-azd-rg`; the referenced `thinkschool-env` is untouched, per section 1). |

---

## 7. Running it

From `Day5/piece2/`:

```powershell
azd auth login
azd env new thinkschool-azd-cowork --location centralindia
azd up
```

`azd up` (or a later `azd deploy`) will print a `SUCCESS` message even though the deployment is not yet correct -- azd's own success signal does not catch the image-path bug in section 4. After every `azd up`/`azd deploy`, run the corrective command from section 4 before trusting the endpoint:

```powershell
$tag = (azd env get-values | Select-String 'SERVICES_QUOTES_API_IMAGE_NAME').ToString().Split(':')[-1].Trim('"')
$registry = (azd env get-values | Select-String 'AZURE_CONTAINER_REGISTRY_ENDPOINT').ToString().Split('=')[-1].Trim('"')
az containerapp update -g thinkschool-azd-rg -n quotes-api-cowork --image "$registry/quotes-api:$tag"
```

Then see `docs/day5-azd-submission.md` for the verification checklist and its real results.

---

## 8. Failure modes and mitigations

1. **Stale image on repeat provisions**: an incomplete `main.parameters.json` causes `azd provision` to silently reset the deployed image to the placeholder. Fixed by threading `quotesApiExists`/`quotesApiImageName` through (section 3).
2. **Container Apps Environment quota**: this subscription allows one environment per region. Fixed by referencing the existing `thinkschool-env` instead of provisioning a new one (section 1).
3. **Container app name collision in a shared environment**: names must be unique per environment, not per resource group. Fixed by naming this workspace's app `quotes-api-cowork` (section 1).
4. **Image-path mismatch**: `ContainerRepository: quotes-api` in the csproj wins over azd's own computed path every build, so the container app is always pointed at a path the image was never pushed to. Requires a manual `az containerapp update --image` after every `azd up`/`azd deploy` (section 4) -- not yet automated.
5. **Alpine + azd is not viable as configured**: azd's SDK container integration hardcodes `-r linux-x64` with no override, so `ContainerFamily: alpine` produces a musl/glibc mismatch that crashes on first SQLite access. Fixed by removing `ContainerFamily`/`ContainerBaseImage`, which lets the base image follow whatever RID actually gets used (section 5).
6. **ACR authentication drift**: because the container app authenticates to the registry via its managed identity (not the registry admin password), rotating or disabling the registry's admin account does not break pulls -- only the `AcrPull` role assignment matters.
7. **Ephemeral SQLite storage**: unchanged from the manual exercise -- `/tmp/quotes.db` does not survive a replica restart or scale event. Azure SQL/PostgreSQL via the existing `QuotesApi.Migrations.SqlServer` project is the real answer for anything beyond this exercise.
