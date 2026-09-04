# Capstone — one-page design

## The slice

**A curator builds a collection of quotes with collaborators, submits it for
review, and publishes it as an immutable public edition that others browse and
follow.**

That is one workflow, end to end, and it is chosen because it is the smallest
slice of this product that has all four things a capstone needs: a real state
machine, a real immutability decision, work that must happen across context
boundaries without a distributed transaction, and a human step (review) that
makes the async flow non-negotiable rather than decorative.

What it is *not*: the whole product. There is no feed, no search ranking, no
social graph beyond "follow", no billing. Those are named under **Deferred** and
left out on purpose — a slice that includes everything is not a slice.

## Bounded contexts

| Context | Owns | Language it speaks | Does not know |
|---|---|---|---|
| **Catalog** | `Quote` — author, text, attribution, background | quote, author, attribution, publishable | that collections exist |
| **Curation** | `Collection` — items, ordering, membership, lifecycle. **The core.** | collection, curator, contributor, draft, revision, submit | how an edition is rendered or who follows it |
| **Publishing** | `Edition` — an immutable snapshot with a slug and visibility, plus the public read model | edition, slug, visibility, live | how a collection was curated, or by whom |
| **Moderation** | `Review` — queue, reviewer, decision, reason | review, decision, rejected, reason | what a collection or quote *means* |
| **Identity & Access** *(upstream, existing)* | users, tokens, ownership policies | user, scope, owner | anything in the four above |

Identity is deliberately not re-modelled. Curation stores a `UserId` value
object and nothing else about a person — the moment a module starts keeping
names and emails, it has quietly acquired a second job.

**Catalog and Curation are separate contexts even though both talk about
quotes,** and this is the boundary most worth defending: Catalog's `Quote` is
canonical content that can be corrected, while Curation cares about *a quote as
it appeared when it was added to this collection*. Same word, two meanings, two
lifecycles. Collapsing them is how a typo fix silently rewrites a published
edition from 2024.

## The core aggregate: `Collection`

```
Draft ──submit──▶ InReview ──approve──▶ Published
  ▲                   │                    │
  └──────reject───────┘              edit  │
                                           ▼
                        InReview ◀─submit─ Revising
```

`Collection` is the consistency boundary: id, name, `OwnerId`, `Members`,
`Items` (each with `QuoteId`, `Position`, and a snapshot of author/text at add
time), `State`, `EditionNumber`.

**Invariants it enforces, and why each one is real:**

1. Name is 3–80 characters. *(carried from the existing model)*
2. At most 50 items. *(carried)*
3. No quote appears twice. *(carried)*
4. **Fewer than 3 items cannot be submitted for publication.** A collection of
   one quote is not a collection; the rule gives publishing a precondition
   instead of letting the client decide what is worth publishing.
5. **Only the owner may submit or publish. Contributors may add and remove
   items, and only while the state is `Draft` or `Revising`.** Roles live in the
   aggregate because they are a rule about the collection, not a rule about the
   request.
6. **Items cannot change while the state is `InReview`.** The thing being
   reviewed has to be the thing that gets published. Without this invariant the
   review step is theatre: a contributor adds a quote after approval and
   unreviewed content goes live.
7. **`EditionNumber` increases by exactly one per publish, and a published
   edition is immutable.** Editing a published collection moves it to
   `Revising` while the live edition keeps serving. There is no state in which
   readers see a half-edited collection.
8. **Positions are contiguous `1..n`.** Reordering is a domain operation that
   renumbers, not a client-supplied integer that can collide or leave gaps.

Invariants 6, 7 and 8 are the ones that make this an aggregate rather than a
list with a name attached. 1–3 already existed in `Day7/piece2`; carrying them
forward unchanged is deliberate, so the capstone is a continuation rather than a
rewrite that quietly drops rules.

Everything else is *not* in this aggregate: a `Review` is its own aggregate in
Moderation (a reviewer's decision has its own lifecycle and its own audit), and
an `Edition` is its own aggregate in Publishing (it outlives the collection
state that produced it).

## Async flows

Three, and each exists because a synchronous call would be wrong rather than
merely slower.

**1. Publish.** Curation commits `CollectionSubmittedForPublication` in the same
transaction as the state change, via the transactional outbox from Day 20.
Moderation opens a `Review`. A human decides — which is why this cannot be a
synchronous call; there is no response to wait for. `CollectionApproved` returns,
Curation transitions to `Published` and emits `CollectionPublished` **carrying
the full snapshot**. Publishing builds the `Edition` and the public read model
from that payload alone.

The payload is fat on purpose: if Publishing had to call back into Curation to
fetch items, the edition it built would reflect whatever the collection looks
like *now*, not what was approved.

**2. Quote correction propagates to drafts only.** Catalog emits `QuoteRevised`.
Curation updates the cached snapshot for collections in `Draft` or `Revising`,
and **ignores it for published editions**, which keep the text as published.
This flow is the reason the snapshot exists, and the rule is a product decision
with a defensible answer either way — it is written down here so that the next
person changing it knows they are changing a decision, not fixing a bug.

**3. Quote moderation.** `QuoteSubmitted` (Catalog) → `Review` → `QuoteApproved`
→ Catalog marks the quote publishable and emits `QuotePublishable`. Curation
keeps a local flag from that event, so invariant 4's sibling rule — *a
collection cannot be submitted while it contains a non-publishable quote* — is
checked inside the aggregate instead of by reaching synchronously into Catalog.

**Consistency rules for all three:** one aggregate per transaction; no
cross-context database transaction ever; every cross-context message goes
through the outbox; every consumer is idempotent on `MessageId` using the
`ProcessedMessages` table from Day 19. Cross-context references are ids and
snapshots, never entity objects.

## Structure — a modular monolith, not microservices

One process, one database, one deployment. Per module: `Domain` (no
dependencies), `Application` (depends on Domain), `Infrastructure` (depends on
Application; EF Core, repositories). `SharedKernel` holds base types only.
`Contracts` holds integration-event records and is **the only project two
modules may both reference**.

```
Host ──▶ each module's Infrastructure + Application   (composition only)
Module.Infrastructure ──▶ Module.Application ──▶ Module.Domain
                                    └──▶ Contracts ◀── every other module
```

One database, **one schema per module** (`catalog`, `curation`, `publishing`,
`moderation`), a `DbContext` per module, and **no foreign keys across schemas**.
That last rule is what keeps a module extractable later: a cross-schema FK is a
join waiting to be written, and a join across a boundary is the boundary gone.

The boundaries are enforced by tests rather than by discipline —
`QuotesPlatform.ArchitectureTests` fails the build if a module references
another module's assemblies, or if a `Domain` project acquires an EF Core
reference.

## Deferred, deliberately

Notifications and follower fan-out (the flow is designed; nothing is scaffolded);
search ranking; a feed; soft delete and GDPR erasure across published editions —
that last one is genuinely hard, because "immutable edition" and "erase this
user's contributions" are in direct conflict, and it deserves its own day rather
than a paragraph.
