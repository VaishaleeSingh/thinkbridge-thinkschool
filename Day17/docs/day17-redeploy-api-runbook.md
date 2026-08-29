# Redeploying the Week-1 API so `/api/auth/register` exists

## Why this is needed

`quotes-api-cowork` runs an image built on **Day 5**, from `Day5/piece2`.
`POST /api/auth/register` was added later, in `Day7/piece2`, so the Angular
client could create accounts — and it has never been deployed. Account creation
on the live site therefore returns **404**, which the UI shows as "That item no
longer exists". Full diagnosis in
`Day17/verification/day17-verification-log.md` §6c.

Redeploying also ships the CORS policy added in the same commit. That happens
not to matter right now — the SWA link makes everything same-origin — but it
stops being a latent difference between the source and what is running.

## The trap this avoids

`Day7/piece2/azure.yaml` exists, but Day 7 had **no azd environment**. Running
`azd deploy` there would have prompted to create a new environment and stood up
a *second* set of Azure resources instead of updating the existing Container
App.

Fixed by copying Day 5's azd environment into Day 7:

```
Day5/piece2/.azure  ->  Day7/piece2/.azure     (already done; .azure is gitignored)
```

Both `azure.yaml` files declare the same service (`quotes-api`) and the same
project path, so azd now builds Day 7's code and pushes it to the same registry
and the same Container App:

```
AZURE_ENV_NAME                    thinkschool-azd
AZURE_CONTAINER_REGISTRY_ENDPOINT crqn4pdkxclsa6s.azurecr.io
AZURE_LOCATION                    centralindia
```

## Steps

Run these on the Windows host — they need the .NET SDK and `azd`, neither of
which is reachable from the environment this was written in (`dotnet` is absent
from the bridge VM, and `dot.net`, `builds.dotnet.microsoft.com`,
`api.nuget.org` and `packages.microsoft.com` are all refused by the egress
proxy; tested, not assumed).

**1. Compile first, and expect this step to fail.**

```
dotnet build Day7/piece2/QuotesApi.slnx
```

`Day13/docs/day13-angular-signals-zoneless-submission.md` recorded that the
Day 7 C# changes "have not been compiled, because no .NET SDK was available
where the front end was built". `/api/auth/register` and
`CorsExtensions.cs` may never have been through a compiler. Fix whatever it
reports before going near Azure — a failed `azd deploy` halfway through is much
harder to reason about than a failed build.

**2. Run the tests, since the solution is right there.**

```
dotnet test Day7/piece2/QuotesApi.slnx
```

**3. Deploy just the service — not `azd up`.**

```
cd Day7/piece2
azd deploy quotes-api
```

`azd deploy` builds, pushes and updates the running app. `azd up` would also
re-provision infrastructure, which is not wanted: the Container App already
exists, already has its managed identity for the registry pull, and already has
`Jwt__Secret` and `AzureAd__Audience` configured.

**4. Verify against the live site, not the portal.**

```
GET  https://quotes-api-cowork...azurecontainerapps.io/health      -> Healthy
POST https://yellow-river-074adb50f.7.azurestaticapps.net/api/auth/register
     {"email":"...","password":"..."}                              -> 200/201, not 404
```

The second one is the point of the exercise: it goes through the Static Web App,
so a success there proves the new image, the SWA `/api` link and the same-origin
front end all line up.

## What the redeploy must not lose

The Container App's existing configuration is *not* in this repository, and
`azd deploy` preserves it — but check after deploying, because losing either
one fails the app in a way that looks unrelated:

| setting | source | why it matters |
|---|---|---|
| `Jwt__Secret` | reference to the `jwt-secret` Container App secret | the API refuses to start without it (Day 4 startup validation) |
| `AzureAd__Audience` | `api://91566dbd-d857-488a-858d-475e60b309b7` | added in Day 17; without it the Entra scheme validates an audience Entra cannot issue (§6d) |
| `ASPNETCORE_ENVIRONMENT` | `Production` | keeps `appsettings.Development.json` out of the deployment |

## After this lands

The Entra work is unblocked. `AuthSchemeSelector` routes any token whose `aud`
contains `api://` to the EntraId scheme, and the audience now matches the app
registration — so adding MSAL to the Angular client needs **no C# change at
all**. The registration already has the SPA platform and both redirect URIs, and
no client secret.

---

# POSTSCRIPT: the deploy went to the wrong Container App

## What happened

`dotnet build Day7/piece2/QuotesApi.slnx` **succeeded** — 14 warnings, all
pre-existing `NU1903` vulnerability notices on transitive packages, no errors.
The prediction above that it would fail was wrong: the Day 7 C# changes compile
fine.

`azd deploy quotes-api` then succeeded in 41 seconds and reported:

```
Endpoint: https://quotes-api-azd.whitestone-71ebd55e.centralindia.azurecontainerapps.io/
resource group: thinkschool-rg
```

That is **not** the app the Static Web App is linked to. There are (at least)
two Container Apps running this API:

| app | resource group | deployed by | code |
|---|---|---|---|
| `quotes-api-cowork` | `thinkschool-azd-rg` | by hand, Day 5 | **stale** (no `/register`) |
| `quotes-api-azd` | `thinkschool-rg` | azd, and now again | **current** (has `/register`) |

The SWA `/api` backend is linked to `quotes-api-cowork`. So the new code is live,
and the deployed front end still cannot reach it.

## Why the mitigation above did not prevent it

Copying Day 5's azd environment into `Day7/piece2` did stop azd from creating a
*new* environment — but that environment was never the one behind
`quotes-api-cowork`. Day 5 deployed the API **twice**, by two different routes
(`Day5/piece2/docs/azure-container-apps.md` by hand with `az cli`, and
`docs/azd-deployment.md` with azd), and they produced different apps.

The tell was visible and was waved through: the copied env said

```
AZURE_RESOURCE_GROUP="thinkschool-rg"
```

while the portal showed `quotes-api-cowork` in `thinkschool-azd-rg`. That
mismatch was noticed and rationalised as "possibly stale, or azd appends -rg"
instead of being checked. It was neither — it was two different resource groups
holding two different apps.

## The fix, and which app should win

**Re-link the Static Web App's `/api` backend to `quotes-api-azd`**, rather than
chasing the new image onto `quotes-api-cowork`. Reasons:

- `quotes-api-azd` is the azd-managed app. Every future `azd deploy` updates it,
  so it stays current by default. `quotes-api-cowork` is hand-deployed and would
  drift out of date again the moment anyone forgets.
- It is non-destructive: `quotes-api-cowork` keeps running, so the URL cited in
  `Day5/piece2/docs/day5-aca-submission.md` continues to answer.

Steps:

1. SWA `quotes-web-day17` → Settings → APIs → Production → unlink, then link
   Container App **`quotes-api-azd`**.
2. On `quotes-api-azd`, confirm/add the settings that were added to the *other*
   app and do not exist here:
   - `AzureAd__Audience = api://91566dbd-d857-488a-858d-475e60b309b7`
   - `Jwt__Secret` — must resolve to a real key or the app will not start
     (Day 4 startup validation). azd provisioned this app, so it likely already
     has one; confirm rather than assume.
3. Verify through the SWA, which is the only test that matters:

```
POST https://yellow-river-074adb50f.7.azurestaticapps.net/api/auth/register
     {"email":"","password":""}   -> 400 application/problem+json   (route exists)
                                  -> NOT 404
```

A 400 with the API's own validation body proves the route exists and the link
reaches the current image. 404 means the SWA is still pointed at the stale app.
