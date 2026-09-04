# Day 22 — Mentor Submission (Capstone kickoff: design + scaffold)

A modular monolith for one workflow: a curator builds a collection of quotes
with collaborators, submits it for review, and publishes it as an immutable
public edition.

## Repo URL

Pull request: https://github.com/thinkbridge-thinkschool/VaishaleeSingh/pull/51

Branch: `day22-capstone-kickoff`

- Capstone: `Day22/Capstone/`
- One-page design: `Day22/Capstone/docs/capstone-design.md`
- Layout and how to run it: `Day22/Capstone/README.md`

(Day 22's other task, the Polly resilience work, is PR #50 — merged.)

---

## The slice, and why this one

**Curate → review → publish.**

It is the smallest slice of this product that has all four things a capstone
needs: a real state machine, a real immutability decision, work that crosses
context boundaries without a distributed transaction, and a human step (review)
that makes the async flow non-negotiable rather than decorative.

Not included, on purpose: a feed, search ranking, a social graph beyond
"follow", billing. A slice that includes everything is not a slice.

## Bounded contexts

| Context | Owns | Language it speaks | Does not know |
|---|---|---|---|
| **Catalog** | `Quote` — author, text, attribution | quote, author, publishable | that collections exist |
| **Curation** | `Collection` — items, ordering, membership, lifecycle. **The core.** | collection, curator, contributor, draft, revision, submit | how an edition is rendered, or who follows it |
| **Publishing** | `Edition` — an immutable snapshot with a slug and visibility | edition, slug, visibility, live | how a collection was curated, or by whom |
| **Moderation** | `Review` — queue, reviewer, decision, reason | review, decision, rejected, reason | what a collection or quote *means* |
| **Identity & Access** *(upstream, existing)* | users, tokens, ownership | user, scope, owner | anything above |

Identity is deliberately not re-modelled: Curation stores a `UserId` and
nothing else about a person. The moment a module keeps names and emails, it has
quietly acquired a second job.

**Catalog and Curation are separate contexts even though both say "quote"**,
and this is the boundary most worth defending. Catalog's `Quote` is canonical
content that can be corrected; Curation cares about *a quote as it appeared
when it was added to this collection*. Same word, two meanings, two lifecycles.
Collapsing them is how a typo fix silently rewrites a published edition from
two years ago.

## The core aggregate: `Collection`

```
Draft ──submit──▶ InReview ──approve──▶ Published
  ▲                   │                    │
  └──────reject───────┘              edit  │
                                           ▼
                        InReview ◀─submit─ Revising
```

Fields: id, name, `OwnerId`, `Members`, `Items` (each with `QuoteId`,
`Position`, and a snapshot of author/text at add time), `State`,
`EditionNumber`.

**Invariants, and why each is real:**

| # | Invariant | Why |
|---|---|---|
| 1 | Name is 3–80 characters | carried from the existing model |
| 2 | At most 50 items | carried |
| 3 | No quote appears twice | carried |
| 4 | Fewer than 3 items cannot be submitted | a collection of one quote is not a collection; publishing gets a precondition instead of the client deciding what is worth publishing |
| 5 | Only the owner may submit or publish; contributors may add/remove only while `Draft` or `Revising` | roles are a rule about the collection, not about the request — an endpoint attribute cannot express "the owner of *this* collection" |
| 6 | **Items cannot change while `InReview`** | the thing reviewed has to be the thing published. Without it, review is theatre: a contributor adds a quote after approval and unreviewed content goes live |
| 7 | **`EditionNumber` +1 per publish; a published edition is immutable** | editing moves it to `Revising` while the live edition keeps serving. There is no state in which readers see a half-edited collection |
| 8 | **Positions are contiguous 1..n** | reordering renumbers, rather than accepting a client integer that can collide or leave gaps |

Rules 6, 7 and 8 are what make this an aggregate rather than a list with a name
attached. Rules 1–3 already existed in `Day7/piece2` and are carried forward
unchanged — and tested (`CarriedForwardRuleTests`), because "carried forward"
is a claim, and a rewrite that quietly drops rules the earlier days argued for
is a regression dressed as progress.

**Not in this aggregate:** `Review` is its own aggregate in Moderation (a
decision has its own lifecycle and audit — collapsing it would make "who
rejected edition 3" unanswerable after edition 4), and `Edition` is its own in
Publishing (it outlives the collection state that produced it).

## Async flows

Three, and each exists because a synchronous call would be *wrong*, not merely
slower.

**1. Publish.** Curation commits `CollectionSubmittedForPublication` in the same
transaction as the state change, through the transactional outbox from Day 20.
Moderation opens a `Review`. **A human decides** — which is precisely why this
cannot be synchronous: there is no response to wait for. `CollectionApproved`
comes back, Curation publishes the next edition and emits
`CollectionPublished` **carrying the full snapshot**; Publishing builds the
`Edition` from that payload alone.

The payload is fat on purpose. If Publishing called back into Curation for the
items, the edition it built would reflect the collection as it is *now* rather
than as it was approved.

**2. A quote correction reaches drafts and stops at editions.** Catalog emits
`QuoteRevised`. Curation refreshes the snapshot for `Draft` and `Revising`
collections and **ignores it for published editions**, which keep the text as
published. This flow is the reason the snapshot exists. It is a product decision
defensible either way, so it is written down — the next person changing it
should know they are changing a decision, not fixing a bug.

**3. Quote moderation.** `QuoteSubmitted` → `Review` → `QuoteApproved` →
Catalog marks the quote publishable and emits `QuotePublishable`. Curation keeps
a local flag from that event, so the rule *"a collection cannot be submitted
while it holds a non-publishable quote"* is enforced **inside the aggregate**
instead of by a synchronous call into Catalog mid-transaction.

**Consistency rules for all three:** one aggregate per transaction; never a
cross-context database transaction; every cross-context message goes through the
outbox; every consumer idempotent on `MessageId` using the `ProcessedMessages`
table from Day 19. Cross-context references are ids and snapshots, never entity
objects.

## Scaffolded solution layout

```
Day22/Capstone/
  QuotesPlatform.slnx
  Directory.Build.props                  target framework, nullable, implicit usings — once
  docs/
    capstone-design.md                   the one-page design
    day22-capstone-prompt.md
    day22-capstone-submission.md         this file
  README.md
  src/
    QuotesPlatform.SharedKernel/         Entity, AggregateRoot, IDomainEvent, DomainException
    QuotesPlatform.Contracts/            integration events — the only shared project
    Modules/
      Catalog/
        QuotesPlatform.Modules.Catalog.Domain           Quote
        QuotesPlatform.Modules.Catalog.Application      IQuoteRepository
        QuotesPlatform.Modules.Catalog.Infrastructure   CatalogDbContext (schema "catalog")
      Curation/                                          ← the core
        QuotesPlatform.Modules.Curation.Domain          Collection + 8 invariants
        QuotesPlatform.Modules.Curation.Application     ICollectionRepository
        QuotesPlatform.Modules.Curation.Infrastructure  CurationDbContext (schema "curation")
      Publishing/     Domain (Edition) · Application · Infrastructure
      Moderation/     Domain (Review)  · Application · Infrastructure
    QuotesPlatform.Host/                 composition only: four Add*Module calls, /health
  tests/
    QuotesPlatform.ArchitectureTests/               the boundaries, enforced
    QuotesPlatform.Modules.Curation.Domain.Tests/   the aggregate's invariants
```

17 projects. The dependency rule in one sentence:

```
Infrastructure ──▶ Application ──▶ Domain ──▶ SharedKernel
                        └──▶ Contracts ◀── every other module
Host ──▶ module Infrastructure only
```

One database, **one schema per module**, a `DbContext` per module, and **no
foreign keys across schemas**. That last rule is what keeps a module
extractable later: a cross-schema FK is a join waiting to be written, and a
join across a boundary is the boundary gone.

### The boundaries are enforced by tests, not by discipline

`QuotesPlatform.ArchitectureTests` fails the build if a module references
another module, a `Domain` project acquires a package reference (which is how EF
Core ends up shaping an aggregate), a `Domain` project references the
integration contracts (which is how a cross-module fact gets published from
inside an entity, before the transaction that makes it true has committed), or
the Host reaches past Infrastructure.

It reads the **`.csproj` files** rather than inspecting compiled assemblies, on
purpose: the compiler prunes assembly references to what the IL actually uses,
so a project reference added today but not yet used would be invisible to a
reflection-based test — and the point is to catch it the day it is added, before
anyone writes the code that depends on it.

A modular monolith is only modular while somebody is watching. These tests are
the somebody.

## Build and test

```
dotnet build Day22/Capstone/QuotesPlatform.slnx
dotnet test  Day22/Capstone/QuotesPlatform.slnx
```

**34 / 34 passing** — 8 architecture tests plus 26 covering the aggregate's
invariants.

## State on Day 22, stated plainly

`Curation.Collection` is fully implemented and tested. `Catalog.Quote`,
`Publishing.Edition` and `Moderation.Review` are real aggregates but minimal.
The Application layers hold their repository ports and no use cases. The
Infrastructure layers hold a `DbContext` per module with its own schema and the
module's DI registration, and no EF entity configurations. There are no
endpoints beyond `/health` and no UI.

That is the scope of a kickoff, and it is worth being explicit rather than
letting a reader assume a working feature exists.

## What did you learn this session?

Choosing the slice was harder than designing it. The instinct is to pick the
domain you know best — but "collections of quotes" nearly had no invariants
worth enforcing, and an aggregate with no rules is a data class. The slice only
became worth building once publishing was added, because publishing forces a
question with a real answer: what happens to a published edition when the
underlying quote is corrected? Snapshot versus reference, and the *reason* for
choosing snapshot, is the whole design.

Also: `Catalog.Quote` and a `Collection`'s item both mean "quote" and are not
the same thing. Noticing that a shared word hides two lifecycles is what a
bounded context is actually for.

## What would break this?

- **A cross-schema foreign key**, added because a join would be convenient. It
  compiles, it works, and the module is no longer extractable.
- **Publishing calling back into Curation** for the items instead of using the
  event payload — the edition then reflects "now" rather than what was
  approved, and nobody notices until a collection is edited between approval
  and rendering.
- **Dropping invariant 6** (items frozen in review) to let a curator "just fix a
  typo" mid-review. Unreviewed content then ships under a reviewer's name.
- **Recomputing `EditionNumber` from a count** rather than incrementing it. Delete
  one edition and every later number shifts, so a reader's link to edition 3
  quietly points at different content.
- **A module reference added "temporarily".** The architecture tests catch it,
  which is exactly why the temptation will be to change the test.
