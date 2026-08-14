# Containerising QuotesApi without a Dockerfile

.NET 8 and later can build an OCI image directly from the project. There is no
Dockerfile in this repository, and nothing to keep in sync with the csproj.

```powershell
cd Day5\piece2
dotnet publish QuotesApi --os linux --arch x64 /t:PublishContainer
docker images quotes-api
```

That produces `quotes-api:0.1.0` in the local Docker daemon.

## Running it

```powershell
docker run --rm -p 8080:8080 `
  -e Jwt__Secret="local-dev-only-signing-key-please-replace-me-32+chars" `
  quotes-api:0.1.0
```

Then:

```powershell
curl http://localhost:8080/health
curl http://localhost:8080/health/live
curl http://localhost:8080/health/ready
```

The `Jwt__Secret` variable is not optional. Try it once without:

```powershell
docker run --rm -p 8080:8080 quotes-api:0.1.0
```

The container exits immediately with a validation message naming
`Jwt:Secret`. That is Day 4's `ValidateDataAnnotations().ValidateOnStart()`
doing its job — the signing key is deliberately absent from
`appsettings.json`, and it normally arrives from user secrets, which exist in
a developer profile and not in a container. Failing at boot with a named cause
is the good outcome; the alternative is an app that starts happily and then
throws on the first login. Note the double underscore: `Jwt__Secret` is how
the configuration key `Jwt:Secret` is spelled as an environment variable.

## Four things that were not obvious

### The exercise's base image and architecture disagree

The suggested properties are `--os linux --arch x64` together with
`<ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:10.0-alpine</ContainerBaseImage>`.
`--arch x64` resolves the runtime identifier `linux-x64`, which is glibc.
Alpine is musl — `linux-musl-x64`. Pinning a base image by tag also means
hand-editing that string at every framework upgrade, in a file nobody thinks
to check.

`<ContainerFamily>alpine</ContainerFamily>` asks the SDK for the Alpine
*variant* of whatever base image it has already selected to match this
project's target framework and RID, so the two cannot drift apart.

### `ContainerImageName` is the old spelling

Renamed to `ContainerRepository` in .NET 8. The old name still works. New code
should use the current one.

### SQLite cannot write where it wants to

`PublishContainer` runs the app as a non-root user, and the default
connection string — `Data Source=quotes.db` — resolves relative to the working
directory, which that user does not own. The `MigrateAsync()` call in
`Program.cs` runs before the app serves anything, so the failure is a
container that starts and dies rather than one that misbehaves later.

The csproj sets `ConnectionStrings__DefaultConnection` to `/tmp/quotes.db`,
which is writable by any user in the base image.

This is a deliberately temporary answer. A file database inside a container is
the wrong shape whatever path it is given: the data dies with the container,
and two replicas do not share it. Anything real moves to the SQL Server
provider this repository already carries, in
`QuotesApi.Migrations.SqlServer`.

### `localhost` means the container

`OpenTelemetry:OtlpEndpoint` is `http://localhost:4317` in
`launchSettings.json`, which is correct when running with `dotnet run` and
wrong the moment the app is containerised — inside the container, `localhost`
is the container. To send traces to a Jaeger running on the host:

```powershell
docker run --rm -p 8080:8080 `
  -e Jwt__Secret="local-dev-only-signing-key-please-replace-me-32+chars" `
  -e OpenTelemetry__OtlpEndpoint="http://host.docker.internal:4317" `
  quotes-api:0.1.0
```

`launchSettings.json` itself is not published and has no effect on the image.

## Health endpoints

Three, not one, because "is this container healthy" is two questions with two
different consequences.

| Endpoint | Checks | A failure means |
|---|---|---|
| `/health/live` | none | restart the container |
| `/health/ready` | database | stop routing to it, leave it running |
| `/health` | everything | what a human curls |

Keeping the database check out of the liveness probe is the part that matters.
If a slow database could fail liveness, a database blip would restart every
healthy replica simultaneously and convert a recoverable problem into an
outage. Readiness is where a database check belongs: traffic stops, the
process survives, and it returns on its own when the database does.

The response body names the service and lists each check, because the default
writer returns the single word `Healthy` — which cannot distinguish this
application from any other process, or from a proxy answering on its behalf.
It reports `"error": true|false` rather than the exception, since these
endpoints are unauthenticated and a failed database check's message is an
excellent way to hand out a connection string. The detail stays in the logs,
correlated by `TraceId`.

`HealthEndpointTests` pins all of this, including the assertion that
`/health/live` runs *no* checks — the property that would quietly disappear if
someone later consolidated the three endpoints onto shared options.

## Image size

| Base | Size |
|---|---|
| Default (Debian) | _to measure_ |
| Alpine | _to measure_ |

To compare:

```powershell
dotnet publish QuotesApi --os linux --arch x64 /t:PublishContainer -p:ContainerFamily=
dotnet publish QuotesApi --os linux --arch x64 /t:PublishContainer
docker images quotes-api
```

## Known limits

`Program.cs` applies EF migrations on startup. With one container that is
convenient. With several starting at once it is a race — two instances can try
to apply the same migration concurrently. The fix is to move migrations to a
separate step that runs once before the app rolls out, which is a deployment
change rather than a containerisation one, so it is named here rather than
made.

The image is not published to a registry. `PublishContainer` pushes with
`-p:ContainerRegistry=...`; nothing here depends on that, and pushing needs
credentials that do not belong in this repository.
