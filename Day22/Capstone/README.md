# Capstone — QuotesPlatform

A modular monolith for the curate → review → publish slice. This file is the
layout and how to run it; the thinking is in the docs:

| Document | What it is |
|---|---|
| [`docs/capstone-design.md`](docs/capstone-design.md) | The one-page design: contexts, the core aggregate, the async flows |
| [`docs/day22-capstone-prompt.md`](docs/day22-capstone-prompt.md) | The task as given, and what it is asking for |
| [`docs/day22-capstone-submission.md`](docs/day22-capstone-submission.md) | The mentor submission: repo URL, design, and this layout |

## Layout

```
Day22/Capstone/
  QuotesPlatform.slnx
  Directory.Build.props              target framework, nullable, implicit usings — once
  src/
    QuotesPlatform.SharedKernel/     Entity, AggregateRoot, IDomainEvent, DomainException
    QuotesPlatform.Contracts/        integration events — the only shared project
    Modules/
      Catalog/       Domain · Application · Infrastructure
      Curation/      Domain · Application · Infrastructure   ← the core
      Publishing/    Domain · Application · Infrastructure
      Moderation/    Domain · Application · Infrastructure
    QuotesPlatform.Host/             composition only: four Add*Module calls
  tests/
    QuotesPlatform.ArchitectureTests/            the boundaries, enforced
    QuotesPlatform.Modules.Curation.Domain.Tests/ the aggregate's invariants
```

17 projects. The dependency rule is one sentence: **Infrastructure → Application
→ Domain → SharedKernel, and the only project two modules may both reference is
Contracts.** The Host references module Infrastructure and nothing else.

## Run it

```bash
dotnet build Day22/Capstone/QuotesPlatform.slnx
dotnet test  Day22/Capstone/QuotesPlatform.slnx
dotnet run --project Day22/Capstone/src/QuotesPlatform.Host
```

## Why the tests matter more than usual here

`QuotesPlatform.ArchitectureTests` reads the `.csproj` files and fails the build
if a module references another module, a Domain project acquires a package
reference, or the Host reaches past Infrastructure.

It reads the project *files* rather than inspecting compiled assemblies on
purpose: the compiler prunes assembly references to what the IL actually uses,
so a project reference added today but not yet used would be invisible to a
reflection-based test — and the whole point is to catch it the day it is added,
before anyone writes the code that depends on it.

A modular monolith is only modular while somebody is watching. These tests are
the somebody.

## State on Day 22

Scaffold and design. `Curation.Collection` is fully implemented with its eight
invariants and covered by tests; `Catalog.Quote`, `Publishing.Edition` and
`Moderation.Review` are real aggregates but minimal; the Application layers hold
their repository ports and no use cases yet; the Infrastructure layers hold a
`DbContext` per module with its own schema and the module's DI registration, and
no EF entity configurations yet. No endpoints beyond `/health`.

Not built, and named in the design as deferred: notifications and follower
fan-out, search ranking, and erasure across published editions — that last one
because "an edition is immutable" and "erase this user's contributions" are in
direct conflict and it deserves more than a paragraph.
