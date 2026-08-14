# SQL Server migrations go here

This folder is intentionally empty until you run the scaffold command
described in the doc comment on `SqlServerDesignTimeDbContextFactory.cs`
(one directory up). `dotnet ef migrations add ... --output-dir Migrations`
will populate it with real migration files, in the same shape as
`QuotesApi/Migrations/`, but generated against the SQL Server provider
instead of SQLite -- the two can't be shared as-is (see that factory's
comment for why).

Until this folder has real migration files in it, every test in
`Quotes.Tests.Integration.SqlServer` will fail at factory-startup time
with an EF Core error about no migrations being found for this assembly.
That is expected, not a bug in the test project -- it is exactly what the
"applies migrations on startup" requirement forces to be true before
those tests can pass at all.

ONE THING TO KEEP IN MIND GOING FORWARD: these migrations and the
existing SQLite ones under `QuotesApi/Migrations/` are two independent
histories generated from the same `QuotesDbContext` model, not one
history shared across providers. If the model changes later (a new
column, a new entity, a changed index), someone has to remember to run
`dotnet ef migrations add` against BOTH providers -- there is no
tooling here that enforces that automatically, and this suite will not
fail loudly if the SQL Server side is left stale: it will just keep
testing against an outdated schema. `SqlServerMigrationTests` catches
"migrations don't apply" but cannot catch "migrations are applied but
out of date relative to the current model" -- that kind of drift.
