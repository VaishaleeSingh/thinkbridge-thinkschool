# Day 5, task 2 — mentor submission

Container image from `dotnet publish`, no Dockerfile.

## GitHub link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/tree/day5-container-image/Day5/piece2

(Replace with the pull request URL once opened.)

## Notes for mentor

Commits: `5e17228` (image + health probes), `d0c9580` (CI), `bb5bd1a` (musl fix
+ tag), `c2940fb` (verified probe responses).
Write-up: `Day5/piece2/docs/containerising.md`.

### The exercise's instructions produce an image that does not run

`--os linux --arch x64` together with an Alpine base image are individually
reasonable and jointly broken. `--arch x64` resolves RID `linux-x64`, which is
glibc; Alpine is musl. The image builds, `docker images` lists it, and
`docker run --entrypoint sh` shells in and prints `NAME="Alpine Linux"`. Every
signal short of actually starting the app says fine. Then:

```
System.DllNotFoundException: Unable to load shared library 'e_sqlite3'
Error relocating /app/libe_sqlite3.so: fcntl64: symbol not found
   at ...MigrateAsync(...)
   at Program.<Main>$(String[] args) in Program.cs:line 115
```

`fcntl64` is a glibc symbol. Managed code is portable; native code is not, and
this app carries one native dependency — `SQLitePCLRaw.lib.e_sqlite3`, via
`Microsoft.EntityFrameworkCore.Sqlite`. Fix is `--os linux-musl --arch x64`.

`ContainerFamily` could not have prevented this: it selects the base image, not
the RID. Alpine is not a drop-in size optimisation, it is a different C library.

### What the app needed before it could be containerised at all

`/health` did not exist. The exercise treats "hit `/health`" as a verification
step; here it was an unstated feature.

It is now three endpoints, because "is this container healthy" is two questions
with different consequences. Measured against the running container:

| Endpoint | Time | Checks | A failure means |
|---|---|---|---|
| `/health/live` | 0.19 ms | none | restart the container |
| `/health/ready` | 89 ms | database | stop routing, leave it running |
| `/health` | 27.2 ms | database | what a human curls |

0.19 ms against 89 ms is the argument. A probe whose failure restarts
containers must not be able to block on a database — otherwise one database
blip restarts every healthy replica at once and a recoverable problem becomes
an outage.

The response names the service, because the default writer returns the bare
word `Healthy`, which cannot distinguish this app from a proxy answering on its
behalf. It reports `"error": true|false` rather than the exception message,
since these endpoints are unauthenticated and a failed database check is an
excellent way to hand out a connection string.

### Two more things the container forced into the open

`Jwt:Secret` lives only in user secrets, which do not exist in a container, so
`docker run` exits at boot with a named validation error until
`-e Jwt__Secret=...` is supplied. That is Day 4's `ValidateOnStart()` working,
and it is the good outcome — the alternative is an app that starts happily and
throws on the first login.

SQLite writes relative to the working directory, and the container runs as
non-root `app` (uid 1654) while `/app` is root-owned 755. Verified by shelling
in rather than assumed. The csproj redirects the connection string to `/tmp`;
the startup log then shows all four migrations applying cleanly.

### CI

A separate `container` job builds the image, starts it, polls `/health/ready`
until it answers, and greps `/health` for `"service":"QuotesApi"`. It does not
stop at a successful build, precisely because a successful build is what hid
the musl fault for an hour.

## What did you learn this session?

That a green build is not evidence the artifact works. Publish succeeded,
`docker images` listed the image, and `docker run --entrypoint sh` printed the
right OS — while the application could not start. The only check that would
have caught it is the one that runs the thing. That is now encoded in CI rather
than remembered: build, start, wait for readiness, confirm the service names
itself.

Also that eager startup work has a diagnostic value nobody advertises. The
crash landed on `MigrateAsync` at `Program.cs:115`. Had migrations not run at
boot, the container would have started clean and failed later on the first
request that touched the database — in production, at a distance from the
cause.

## What would break this?

- SQLite in a container is ephemeral by construction. Data dies with the
  container and two replicas do not share it. `/tmp` makes it *work*, not
  *right*.
- `MigrateAsync()` on startup is a race across replicas: two instances can try
  to apply the same migration concurrently. Fine for one container, wrong for a
  rollout. Named in the doc, deliberately not fixed under cover of a
  containerisation task.
- `NU1903` — `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 has a published high-severity
  advisory. It is transitive through the SQLite provider, so there is nothing
  here to bump. A container image inherits every transitive native dependency,
  advisories included.
- Any future native dependency reintroduces the musl problem, and will again
  build cleanly before failing at runtime. Nothing in the toolchain warns.
- The image is not pushed to a registry, so nothing verifies it is
  distributable — only that it builds and starts.
- `HealthEndpointTests` has not been run. The container proves the code works
  at runtime; it does not prove the test file compiles.
