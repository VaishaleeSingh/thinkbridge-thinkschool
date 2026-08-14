# Day 5, Task 5 — Mentor Submission (Verify in App Insights with KQL)

OpenTelemetry has been wired into this app since `docs/observability.md`. This task is the first time that telemetry lands in Azure and can be queried.

## GitHub Link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/tree/day5-azd-deployment/Day5/piece2

---

## What had to change first: the deployed app was emitting nothing

The exercise assumes `azd` created an Application Insights resource, or that one can be linked through `APPLICATIONINSIGHTS_CONNECTION_STRING`. Neither was true here, for two separate reasons — both found by reading what was actually deployed rather than by opening the portal and expecting a resource:

1. **`infra/resources.bicep` provisioned no Application Insights and no Log Analytics workspace at all.** This workspace's Bicep was written by hand for Task 4 (a registry, an identity, an AcrPull role assignment, and the container app), not scaffolded from an `azd` template that would have included monitoring by default. There was no resource to link to.

2. **This app does not read `APPLICATIONINSIGHTS_CONNECTION_STRING`.** `QuotesApi/Extensions/ObservabilityExtensions.cs` reads the configuration key `ApplicationInsights:ConnectionString` and passes it to `UseAzureMonitor()` explicitly. `APPLICATIONINSIGHTS_CONNECTION_STRING` is the Azure Monitor distro's own auto-discovery variable, which this app never consults. Setting only the documented variable would have produced a resource with no data in it and no error anywhere to explain why.

Both are now fixed in `infra/resources.bicep`:

- a Log Analytics workspace (`log<token>`) and a workspace-based Application Insights component (`appi-quotes-api-<token>`), in `thinkschool-azd-rg` alongside everything else this exercise owns, so `azd down` removes them with it;
- `ApplicationInsights__ConnectionString` on the container app — the double underscore is the environment-variable spelling of `:`, so it arrives as the key the app actually reads;
- `APPLICATIONINSIGHTS_CONNECTION_STRING` set to the same value, so the app matches what Azure tooling and the standard docs expect to find.

The connection string is deliberately **not** a Bicep output. `azd` writes outputs into `.azure/<env>/.env`, and `.azure` is not in this repository's `.gitignore` — an output here is one `git add .` away from committing an ingestion key. The component and workspace *names* are output instead; the connection string is fetched on demand:

```powershell
az monitor app-insights component show `
  -g thinkschool-azd-rg -a appi-quotes-api-<token> `
  --query connectionString -o tsv
```

### One thing this exercise nearly broke, already guarded in code

`ObservabilityExtensions` registers `AddAspNetCoreInstrumentation()` and `AddHttpClientInstrumentation()` **only when Azure Monitor is off**, because the Azure Monitor distro registers both itself. With a connection string now present in the deployed environment for the first time, an unconditional registration would have recorded every request as two spans — doubling `count()` and corrupting the exact p50/p99 numbers this task asks for, with nothing in the portal indicating anything was wrong. The conditional was written for a different reason (keeping CI and the 135-test suite off the exporters); it happens to be what keeps these percentiles honest.

---

## Deploy, then generate traffic

```powershell
cd Day5\piece2
azd up
```

Then the correction from `docs/azd-deployment.md` section 4 — still required on every `azd up`/`azd deploy`, this task changes nothing about that:

```powershell
az containerapp update -n quotes-api-cowork -g thinkschool-azd-rg `
  --image <registry>.azurecr.io/quotes-api:<tag>
```

Confirm the app picked up the new configuration before generating traffic — a revision without the variable emits nothing:

```powershell
az containerapp show -n quotes-api-cowork -g thinkschool-azd-rg `
  --query "properties.template.containers[0].env[?starts_with(name,'ApplicationInsights')]" -o table
```

Traffic:

```powershell
$base = "https://quotes-api-cowork.whitestone-71ebd55e.centralindia.azurecontainerapps.io"

1..20 | ForEach-Object { curl.exe -s -o NUL -w "%{http_code} " "$base/health" }
1..20 | ForEach-Object { curl.exe -s -o NUL -w "%{http_code} " "$base/api/quotes" }
1..10 | ForEach-Object { curl.exe -s -o NUL -w "%{http_code} " "$base/api/collections/1" }
1..5  | ForEach-Object {
  curl.exe -s -o NUL -w "%{http_code} " -X POST "$base/api/auth/login" `
    -H "Content-Type: application/json" `
    -d '{\"email\":\"nobody@example.com\",\"password\":\"WrongPassword123!\"}'
}
```

**The authenticated routes will return 401, and that is expected here.** The deployed database has no users: the startup seeding was removed (commit `b5f222a`) because it broke the integration suite with UNIQUE constraint failures, and this API exposes no registration endpoint — only `/api/auth/login`, `/refresh`, `/logout`. A 401 is still a fully recorded request with a real duration, which is all this task needs; it is not a deployment failure. Producing authenticated traffic would mean inserting a user into the container's SQLite database directly, which is out of scope for a latency query.

Telemetry takes roughly **1–3 minutes** to become queryable. An empty result immediately after the curls is ingestion latency, not a broken pipeline.

### Alternative: create the resources by hand, without re-running `azd up`

Equivalent to what the Bicep does, for when a full reprovision is not wanted. These resources sit **outside** the Bicep, so the next `azd up` creates its own pair and repoints the app at those.

```powershell
az monitor log-analytics workspace create -g thinkschool-azd-rg -n log-quotes-api -l centralindia
$wsId = az monitor log-analytics workspace show -g thinkschool-azd-rg -n log-quotes-api --query id -o tsv
az monitor app-insights component create -g thinkschool-azd-rg -a appi-quotes-api -l centralindia --workspace $wsId

$cs = az monitor app-insights component show -g thinkschool-azd-rg -a appi-quotes-api --query connectionString -o tsv
az containerapp update -n quotes-api-cowork -g thinkschool-azd-rg `
  --set-env-vars "ApplicationInsights__ConnectionString=$cs" "APPLICATIONINSIGHTS_CONNECTION_STRING=$cs"
```

`--set-env-vars` merges rather than replaces, so `Jwt__*` and `ASPNETCORE_ENVIRONMENT` survive. The update creates a new revision; that restart is what makes the app read the variable and switch telemetry on.

---

## Where to run the query

### In the portal

1. **portal.azure.com** → search the resource name (`appi-quotes-api…`) → open the Application Insights resource. It is in resource group `thinkschool-azd-rg`.
2. Left menu → **Monitoring** → **Logs**. Dismiss the "Queries" sample dialog that opens on top.
3. Paste the query into the editor and press **Run**.
4. The time-range control above the editor must read **"Set in query"**. If it is set to a range (e.g. "Last 24 hours"), that range ANDs with the `ago(30m)` filter, and a narrower portal range silently overrides the query's own window.

### From the CLI, no portal

```powershell
az extension add -n application-insights
$appId = az monitor app-insights component show -g thinkschool-azd-rg -a appi-quotes-api --query appId -o tsv

az monitor app-insights query --app $appId --analytics-query `
  "requests | where timestamp > ago(30m) | summarize count(), p50=percentile(duration,50), p99=percentile(duration,99) by name | order by p99 desc" `
  -o table
```

### If the result is empty

In the order worth checking, because each has a different fix:

| Cause | How to tell | Fix |
| --- | --- | --- |
| Ingestion latency | Fewer than ~3 minutes since the traffic | Wait, re-run |
| App never got the variable | `az containerapp show -n quotes-api-cowork -g thinkschool-azd-rg --query "properties.template.containers[0].env[].name"` does not list `ApplicationInsights__ConnectionString` | Re-apply the env var; check the active revision is the new one |
| Variable set on an old revision | `az containerapp revision list -n quotes-api-cowork -g thinkschool-azd-rg -o table` shows traffic on an earlier revision | Route traffic to the latest revision |
| Portal time range narrower than the query | Time picker is not "Set in query" | Set it to "Set in query" |
| Querying the wrong resource | Two components exist (a hand-made one and the Bicep's `appi-quotes-api-<token>`) | Query the one whose connection string is on the running revision |

---

## The query

```kusto
requests
| where timestamp > ago(30m)
| summarize count(), p50=percentile(duration, 50), p99=percentile(duration, 99) by name
| order by p99 desc
```

`duration` is in milliseconds. `name` is the ASP.NET Core operation name, so endpoints appear as `GET /api/quotes`, `POST /api/auth/login`, and so on.

### Results

> **TODO — paste the actual result table from the Logs blade here, and a screenshot as `images/appinsights-kql.png`.**

| name | count_ | p50 (ms) | p99 (ms) |
| --- | --- | --- | --- |
| | | | |

Expect the health probes to dominate the row count: the liveness and readiness probes in `infra/resources.bicep` run every 10 seconds each, so over a 30-minute window they contribute roughly 180 requests apiece before any manual traffic is counted. `/health/live` should show the lowest p50 of anything in the table — it runs no checks at all — and `/health/ready` a slightly higher one, since it runs the database check. That split is the same one `docs/containerising.md` documents locally, now visible as a latency number rather than a JSON body.

---

## Saving it as a function

In the Application Insights resource → **Logs** → run the query → **Save** → **Save as function**.

- **Function name / alias:** `EndpointLatency`
- **Legacy category:** `QuotesApi`
- **Function parameters:** `window` of type `timespan`, default `30m`

Then replace `ago(30m)` with `ago(window)` before saving, so the saved function answers more than one question:

```kusto
requests
| where timestamp > ago(window)
| summarize count(), p50=percentile(duration, 50), p99=percentile(duration, 99) by name
| order by p99 desc
```

Re-use it by name, as its own table-like operator:

```kusto
EndpointLatency(1h)
EndpointLatency(24h) | where name !startswith "GET /health"
```

The query text is committed at `docs/kql/endpoint-latency.kql`, including the probe-excluding variant. The function itself is saved in the **Log Analytics workspace**, not in the App Insights component — a workspace-based component stores its tables in the workspace, and saved functions live beside them. That matters for `azd down`: the function disappears with the workspace, and this file is the only copy that survives.

> **TODO — screenshot of the saved function in the Logs blade as `images/appinsights-function.png`.**

---

## Verification checklist

| Check | Expected | Actual |
| --- | --- | --- |
| `ApplicationInsights__ConnectionString` present on the running revision | set | **TODO** |
| `requests` table returns rows within ~3 min of traffic | non-empty | **TODO** |
| Health probe rows present | `GET /health/live`, `GET /health/ready` | **TODO** |
| `GET /health/live` has the lowest p50 in the table | yes | **TODO** |
| Query saved as a function and callable as `EndpointLatency(30m)` | yes | **TODO** |

---

## What did you learn this session?

- A connection string is not configuration here, it is a **switch between two different telemetry pipelines**. With it, the Azure Monitor distro owns ASP.NET Core and HttpClient instrumentation; without it, the app registers those itself and exports over OTLP. Wiring the variable without knowing that would have double-instrumented every request and quietly halved every percentile's meaning — a failure with no error message attached to it.
- The environment variable an exercise names is not necessarily the one an app reads. `APPLICATIONINSIGHTS_CONNECTION_STRING` is a convention of the distro's auto-discovery, not a universal contract; this app reads `ApplicationInsights:ConnectionString` because it configures the exporter explicitly. Setting the wrong one produces an empty resource and no diagnostic anywhere.
- Percentiles are only as trustworthy as the sampling configuration underneath them. `count()` counts *stored* records; the moment sampling is on, that is not the number of requests that happened, and `sum(itemCount)` is the honest aggregate.
- On a low-traffic app, health probes are not noise in the data — they *are* the data. Any latency dashboard here needs an explicit probe filter, or every number describes the probe rather than the endpoint.

## What would break this?

- Removing or renaming `ApplicationInsights__ConnectionString` (or provisioning into a different resource group without it) silently reverts the app to emitting nothing. `UseAzureMonitor()` is never called, the app starts cleanly, and the only symptom is an empty `requests` table.
- Registering `AddAspNetCoreInstrumentation()`/`AddHttpClientInstrumentation()` unconditionally in `ObservabilityExtensions` double-counts every request and corrupts every percentile in this query, while looking entirely healthy in the portal.
- Enabling sampling (`AzureMonitorOptions.SamplingRatio` below 1.0) makes `count()` under-report by the sampling factor; the query needs `sum(itemCount)` to stay correct.
- `azd down` deletes the workspace, and the saved function with it — the function is not infrastructure-as-code. `docs/kql/endpoint-latency.kql` is the only committed copy.
- Querying within the first minute or two after traffic returns an empty table that looks identical to a broken pipeline. Ingestion latency, not a failure.
- A 30-day retention window means this query answers nothing about anything older; `ago(60d)` returns empty regardless of what traffic actually happened.
