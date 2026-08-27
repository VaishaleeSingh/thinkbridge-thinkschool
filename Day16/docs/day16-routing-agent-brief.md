# Day 16 — Routing, lazy loading, guards: brief

## Role and working agreement

Work only in `Day13/quotes-web` and `Day16/docs`. No commit, push, branch, or generated/cache-file changes as part of implementing. Read the real source before changing anything.

## What I found before writing any code

Three of the four asks in this exercise already exist in this codebase, from earlier days, verified by reading the source directly rather than assumed:

- **Lazy-loaded routes**: every route in `app.routes.ts` uses `loadComponent: () => import(...)`. Nothing is eagerly imported.
- **Functional auth guard**: `core/guards/auth-guard.ts` already has `authGuard` (redirects to `/sign-in?returnUrl=...` when `AuthStore.isAuthenticated()` is false) and `guestGuard` (the inverse, for `/sign-in` itself). Applied at the parent route so every child route is protected by default.
- **Route params**: `quotes/:id` already exists, backed by the real `GET /api/quotes/{id}` endpoint, parsed defensively (`parseQuoteId`, digits-only, rejects `0x10`/`007`/leading-zero forms) rather than a bare `Number(raw)`.

So the only genuinely new piece is the **View Transition between the quotes list and a quote detail** — that did not exist. This brief is scoped to just that, plus independently re-verifying the three pre-existing pieces rather than taking them on faith.

## Source of truth

- `src/app/app.routes.ts` — existing route table.
- `src/app/core/guards/auth-guard.ts` — existing guard.
- `src/app/features/quotes/pages/quote-detail-page/quote-detail-page.ts` / `.html` — real `GET /api/quotes/{id}` consumer, id param `id` (a number, e.g. `100001`).
- `src/app/features/quotes/components/quote-card/quote-card.html` — the list's card. Its only click target opens an in-page modal preview (`quote-preview-dialog`), **not** a router navigation — so there was no existing navigation path from the list into `/quotes/:id` for a View Transition to apply to.

## What to build

1. `provideRouter(...)` in `app.config.ts`: add `withViewTransitions({ skipInitialTransition: true })`. `skipInitialTransition` matters — without it the very first paint of the app (nothing navigated FROM) also animates, which is meaningless.
2. `quote-card.html`/`.ts`: add a real `routerLink` to `/quotes/:id` (a small circular icon-only expand button in the card footer, inline SVG glyph in the same style as the existing theme-toggle icon, with `aria-label` carrying the accessible name) **without removing** the existing modal-preview button — this is additive, not a redesign of the list's primary interaction.
3. `[style.view-transition-name]="'quote-image-' + quote.id"` on the matching visual element in both `quote-card.html` (the card's `.quote` article) and `quote-detail-page.html` (the `.quote-detail` article), so the browser morphs the same visual element between the two views instead of a generic cross-fade.
4. `prefers-reduced-motion`: the app already collapses `animation-duration`/`transition-duration` to `1ms` globally, but the View Transition API's pseudo-elements (`::view-transition-old/new/group`) live in a separate pseudo-element tree that existing `*`/`*::before`/`*::after` selectors don't reach — extend the existing reduced-motion block in `_animations.scss` to name them explicitly.

## Required verification (not to be taken on the diff's word)

- Network tab: confirm the `QuoteDetailPage` chunk is not requested until the moment of navigating into `/quotes/:id`, not on `/quotes` alone.
- Guard: open a protected route in a signed-out session and confirm the real redirect to `/sign-in` with `returnUrl` set.
- View Transition: confirm `document.startViewTransition` is actually invoked by a real SPA navigation (not just that the API exists in the browser), since Angular could silently no-op it if misconfigured.

## Done means

- The three pre-existing pieces are independently re-confirmed, not just cited from memory of earlier days.
- The View Transition is real (proven to fire), reachable from the actual UI, and degrades safely under reduced motion.
- No UI regressions to the existing modal preview.
