# Day 16 — State management, signals first: brief

## Role and working agreement

Work only in `Day13/quotes-web` and `Day16-signals/docs`. No commit, push, branch, or generated/cache-file changes as part of implementing.

## What already exists — read first, don't rebuild it

This app is already signals-first everywhere: `QuotesStore`, `QuoteDetailStore`, `CollectionDetailStore`, `AuthStore` are all `@Injectable` classes built entirely on signals, no NgRx, no RxJS state. So "model state with signals" as a general pattern is not new here — the exercise is really about picking ONE small, currently-missing feature and giving it the right-sized amount of state, not the full store-class ceremony every existing feature already has.

## The real gap

Adding an existing quote to a collection only exists today from inside `CollectionDetailStore` (`addQuote()`), reachable only when you're already viewing that one collection. There is no way to add a quote to a collection from the quotes list itself, even though the real endpoint for it already exists and is already used elsewhere:

- `GET /api/collections` — the caller's own collections (`CollectionsApi.list()`).
- `POST /api/collections/{id}/items` — add a quote to a collection (`CollectionsApi.addItem()`), body `{ quoteId }`. Fails 400 if the collection already has 50 quotes (`COLLECTION_LIMITS.maxItems`) or the quote is already in it.

## What to build

A small "add this quote to a collection" control on each quote card in the list, backed by a plain signals-based service — deliberately NOT shaped like `CollectionDetailStore`:

- No re-fetch-the-whole-detail-after-every-mutation pattern (there's no "detail" here to re-fetch — just a list of collections and their counts). Patch the affected collection's `quoteCount` locally instead after a successful add; that's a real, defensible simplification for this scope, not a shortcut to hide.
- One shared instance for the whole quotes page (provided on `QuotesPage`, same lifecycle as `QuotesStore`), not one per card — the collections list only needs fetching once, and only one card's picker menu should be open at a time.
- Signals needed: the fetched collections list, whether it's loading, a load failure, which quote's picker menu is currently open (or none), which specific add is in flight (so only the button someone clicked shows a spinner, not every button), and a failure from the last add attempt (kept separate from the load failure, same reasoning `CollectionDetailStore.actionError` already uses).

UI: a small icon button on the card (additive, doesn't touch the existing modal-preview button, expand-icon link, or delete button) that opens a small inline list of the user's collections; picking one calls the add; show per-item pending/success/error, using the real API's error message on a 400 (full collection, or already a member) rather than a generic failure string.

## Required verification (not to be taken on the diff's word)

- Add a real quote to a real collection through the new UI, then open that collection's real detail page and confirm the quote is actually there — not just that the button showed success.
- Trigger the real 400 case (add the same quote to the same collection twice) and confirm the actual API message is shown, not a generic one.
- Confirm only one card's menu opens at a time, and that opening a second card's menu doesn't refetch the collections list needlessly.

## The judgment call I own, not the agent

A short written note, in my own words: at what point would this feature's state actually need to graduate from "signals in a small service" to a real store (the `CollectionDetailStore`-style class, or further, something like NgRx)? The agent can draft a first pass at this; the final call and the reasoning behind it has to be mine.

## Done means

- Real endpoints only, no invented fields.
- Additive to the existing card UI, nothing removed or restructured.
- State is scoped to exactly what this feature needs — no signal that isn't read by something, no computed that isn't read by something.
- Verified live against the running app and real backend, not only unit tests.
