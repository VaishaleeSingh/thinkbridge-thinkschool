# thinkschool

Training repository for the thinkbridge .NET programme. One folder per day.
Each day starts from the previous day's code and adds one layer, so the same
application — a small quotes API — is carried forward rather than rewritten,
and each day's folder is a working snapshot of what it looked like at the end
of that day.

The current, most complete version of the application is **`Day5/piece2`**.

## The application

`QuotesApi` is an ASP.NET Core minimal API on .NET 10 with EF Core over SQLite
(SQL Server via Testcontainers in the integration suite). It exposes quotes and
user-owned collections of quotes, behind two authentication schemes — a
first-party JWT and Microsoft Entra ID — selected per request.

| Area | Where |
|---|---|
| Endpoints | `QuotesApi/Extensions/*EndpointExtensions.cs` |
| Domain model | `QuotesApi/Models` |
| Data access | `QuotesApi/Data`, `QuotesApi/Repositories` |
| Typed configuration | `QuotesApi/Configuration` |
| Cross-cutting middleware | `QuotesApi/Middleware` |
| Tracing setup | `QuotesApi/Extensions/ObservabilityExtensions.cs` |

## Days

**Day 1 — modelling.** `Day1/refactor-orders` refactors an anaemic order model
into one that owns its invariants; `Day1/piece2` applies the same idea to
quotes and collections. `Day1/piece2/RICH_MODEL_WHY.md` is the write-up.

**Day 2 — persistence.** EF Core, migrations, a repository seam, and the first
integration tests against a real SQL Server in a container.

**Day 3 — security and testing.** Password login with refresh tokens,
authorization policies including resource-based ownership checks, Entra ID as a
second scheme, and the unit / integration / SQL Server test split that the
later days keep running.

**Day 4 — operability.** Structured logging with Serilog and correlation IDs,
OpenTelemetry tracing exported to Jaeger and Azure Application Insights, typed
configuration with `IOptions` and startup validation, and a CI pipeline.
`Day4/piece2/docs/observability.md` covers the tracing setup.

**Day 5 — performance and packaging.** Diagnosing a slow endpoint from its
trace rather than by guessing: `Day5/piece2/docs/slow-endpoint-diagnosis.md`
walks an N+1 in `GET /api/collections` from the Jaeger trace that exposed it,
through the fix, to the test that stops it coming back. Then packaging the app
as a container image built from the project itself, with no Dockerfile —
`Day5/piece2/docs/containerising.md`, including the health-probe split and the
four things about it that were not obvious.

## Running it

Prerequisites: .NET 10 SDK. Docker is needed only for the SQL Server
integration tests and for running Jaeger locally.

```bash
cd Day5/piece2
dotnet run --project QuotesApi
```

The API needs a JWT signing key, which is deliberately not in
`appsettings.json`. Supply it as a user secret:

```bash
dotnet user-secrets --project QuotesApi set "Jwt:Secret" "<at least 32 characters>"
```

Startup validation will fail fast with a readable message if it is missing or
too short, rather than failing later at token-signing time.

Optional, both off unless configured:

```bash
# Traces to a local Jaeger
dotnet user-secrets --project QuotesApi set "OpenTelemetry:OtlpEndpoint" "http://localhost:4317"

# Traces and logs to Azure Application Insights
dotnet user-secrets --project QuotesApi set "ApplicationInsights:ConnectionString" "<connection string>"
```

## Tests

```bash
cd Day5/piece2
dotnet test QuotesApi.slnx
```

Four projects: `Quotes.Tests.Unit` (domain and services, no host),
`QuotesApi.Tests` and `Quotes.Tests.Integration` (in-process host via
`WebApplicationFactory`), and `Quotes.Tests.Integration.SqlServer`
(Testcontainers — **requires Docker running**; these are the tests that fail
first if Docker is not up).

As a container (no Dockerfile — the image is built from the project):

```bash
cd Day5/piece2
dotnet publish QuotesApi --os linux-musl --arch x64 /t:PublishContainer
docker run --rm -p 8080:8080 -e Jwt__Secret="<at least 32 characters>" quotes-api:0.1.0
curl http://localhost:8080/health
```

The `Jwt__Secret` variable is required — user secrets do not exist inside a
container, and startup validation fails fast without it. See
`Day5/piece2/docs/containerising.md`.

Coverage:

```bash
dotnet test QuotesApi.slnx --collect:"XPlat Code Coverage" \
  --settings coverlet.runsettings --results-directory:TestResults
reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" \
  -targetdir:CoverageReport -reporttypes:Html
```

`--results-directory` matters. Without it each project writes into its own
`TestResults` folder and ReportGenerator will happily merge stale runs from
previous days, producing a report that never changes no matter what you edit.

## Working in this repo

- `main` is protected. Every task gets its own branch off an up-to-date `main`,
  and lands through a pull request that CI has passed.
- CI (`.github/workflows/ci.yml`) restores, builds and tests the current day's
  solution on every push and on every PR into `main`.
- Line endings: this repo stores LF and is worked on from Windows. Set
  `git config core.autocrlf true` once per clone. Without it every file shows
  as fully modified and real changes disappear into thousands of lines of
  line-ending noise.
- Build output (`bin/`, `obj/`), coverage output and local SQLite files are
  ignored and should never be committed.
