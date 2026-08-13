# Observability runbook

How this app's telemetry is configured, how to query it, and how the alert
is defined. Written down rather than left in the portal so it can be
reviewed, corrected, and recreated if the resource is ever rebuilt.

## Where telemetry goes

Three destinations, all independent and all optional:

| Destination | Enabled by | Used for |
|---|---|---|
| Console | always | local development |
| OTLP collector (Jaeger / Aspire) | `OpenTelemetry:OtlpEndpoint` | local trace inspection |
| Azure Application Insights | `ApplicationInsights:ConnectionString` | deployed environments |

With none configured the app still runs, still creates spans, and still
stamps every log line with the trace ID -- it simply keeps the telemetry to
itself. That is what makes CI and the test suite work without any Azure
resource existing.

## Secrets

The App Insights connection string is never committed. It comes from:

- **Deployed**: Key Vault, via `KeyVault:Uri` and the app's managed identity.
  Key Vault secret names cannot contain `:`, so the hierarchy uses a double
  dash -- store it as `ApplicationInsights--ConnectionString`.
- **Local**: .NET user-secrets, which live in your user profile, not the repo.

```bash
cd QuotesApi
dotnet user-secrets init
dotnet user-secrets set "ApplicationInsights:ConnectionString" "InstrumentationKey=...;IngestionEndpoint=..."
```

To point at a Key Vault locally instead, `az login` first -- 
`DefaultAzureCredential` picks up that session.

## KQL

Find every log line for one request, given a trace ID from a log or a trace:

```kusto
traces
| where timestamp > ago(15m)
| where operation_Id == "ff929c1618d7f99c0cd23b1b58c6672d"
| order by timestamp asc
| project timestamp, severityLevel, message, customDimensions
```

Slowest requests in the last hour, which is where a latency investigation
usually starts:

```kusto
requests
| where timestamp > ago(1h)
| summarize count(), avg(duration), percentile(duration, 95), percentile(duration, 99) by name
| order by percentile_duration_99 desc
```

The custom span this app creates around password hashing -- useful for
telling "the database is slow" apart from "hashing is slow", which look
identical from the outside:

```kusto
dependencies
| where timestamp > ago(1h)
| where name == "verify-password"
| summarize count(), avg(duration), percentile(duration, 95) by bin(timestamp, 5m)
| order by timestamp asc
```

Failed requests joined to their exceptions, by trace:

```kusto
requests
| where timestamp > ago(1h) and success == false
| join kind=leftouter (exceptions | project operation_Id, type, outerMessage) on operation_Id
| project timestamp, name, resultCode, type, outerMessage, operation_Id
| order by timestamp desc
```

## The alert

Average response time of `POST /api/quotes` over 500 ms across 5 minutes,
emailing an action group.

Portal: Application Insights resource -> Alerts -> Create -> Alert rule.
Signal `Server response time`. Split by dimension `request/name`, value
`POST /api/quotes`. Aggregation Average, threshold Greater than 500,
aggregation granularity 5 minutes.

Reproducibly, with the Azure CLI:

```bash
az monitor action-group create \
  --name quotes-api-oncall \
  --resource-group <rg> \
  --short-name quotesapi \
  --action email primary <your-email>

az monitor metrics alert create \
  --name "quotes-api-post-quotes-slow" \
  --resource-group <rg> \
  --scopes "/subscriptions/<sub>/resourceGroups/<rg>/providers/microsoft.insights/components/<app-insights-name>" \
  --condition "avg requests/duration > 500 where request/name includes 'POST /api/quotes'" \
  --window-size 5m \
  --evaluation-frequency 1m \
  --action quotes-api-oncall \
  --description "Average response time for POST /api/quotes exceeded 500ms over 5 minutes."
```

### Why only this one alert

An alert should page a person only when a person needs to do something.
Everything else belongs on a dashboard that someone looks at deliberately.
Latency on the main write endpoint qualifies: it is user-visible, it is
actionable, and it is not something a dashboard would catch at 3am.
Request counts, dependency call volumes and CPU graphs do not qualify --
they are context for an investigation, not reasons to wake up.

### Caveat: sampling

The Azure Monitor distro enables adaptive sampling by default. It reweights
what it keeps, so aggregate counts stay roughly honest, but on a
low-traffic endpoint a 5-minute average can be computed from very few
retained samples and will look noisier than reality. Worth knowing before
trusting a single breach of the threshold.
