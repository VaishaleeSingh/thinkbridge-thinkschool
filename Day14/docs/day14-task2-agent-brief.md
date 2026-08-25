# Brief for the agent — Day 14, Task 2 (Signal Forms preview)

## Context

`quotes-web` (Angular 21.2, standalone, zoneless) already has a working,
hand-verified reactive-forms create-quote dialog at
`src/app/features/quotes/components/quote-form-dialog/`. Do not touch that
component, its template, or its three shared field components
(`text-field`, `textarea-field`, `select-field`). This task is a **separate,
side-by-side rebuild** of the same form using Angular's experimental Signal
Forms preview API (`@angular/forms/signals`, confirmed present in
`node_modules/@angular/forms/types/signals.d.ts` in this exact install —
`form`, `schema`, `required`, `minLength`, `maxLength`, `submit`, the
`Field` directive, `FormValueControl`/`FormUiControl` contracts, etc.), so
the two can be compared honestly.

## Real API contract — read directly, do not assume

`POST /api/quotes` (see `src/app/core/models/quote.ts`, mirrored from
`QuotesApi/Models/Quote.cs` / `QuoteEndpointExtensions.cs`):

```ts
interface CreateQuoteRequest {
  readonly author: string;
  readonly text: string;
  readonly backgroundImageUrl: string;
}
```

- `author`: required, whitespace-only counts as empty (server normalizes/
  rejects it), max 200 chars (`QUOTE_LIMITS.authorMaxLength`).
- `text`: same rule, max 1000 chars (`QUOTE_LIMITS.textMaxLength`).
- `backgroundImageUrl`: optional server-side (a default is derived if
  omitted), but in *this UI* it is always populated from a `<select>` of six
  bundled backgrounds (`QUOTE_BACKGROUND_OPTIONS`), defaulting to
  `DEFAULT_QUOTE_BACKGROUND_URL` — so from the form's point of view it is
  always present and always one of those six values. No other fields exist
  on this endpoint. Do not invent a field (no `tags`, no `id`, no
  `createdAt` — none of those are accepted by `POST /api/quotes`).

## What to build

A new, standalone demo component — does not replace or get wired into the
real create-quote flow (`quotes-page` keeps using the existing dialog) — at
`src/app/features/quotes/components/quote-form-signal-demo/`, reachable from
a small link/route (e.g. `/quotes/signal-forms-demo`) so it can be opened and
driven by hand and by a Playwright script, same as the reactive version.

Rebuild the same three fields (author text input, text textarea, background
select) with the same validation rules as the reactive form above, using
`@angular/forms/signals`:

- `form()` + `schema()` (or inline logic) with `required()`, `maxLength()`
  for author/text, and a custom `validate()` for the same "whitespace-only
  counts as empty" rule the reactive version has (`noWhitespace()` validator
  — read `src/app/shared/forms/no-whitespace.validator.ts` for the exact
  rule so the signal-forms version enforces the identical thing, not a
  looser or stricter approximation).
- Real `<label for>` associations, `aria-invalid`, and `aria-describedby`
  pointing at the actual visible error text — same bar as the reactive
  version, not "the Field directive probably handles it." Signal Forms'
  `FormUiControl`/`Field` directive contract only *wires* `invalid`/`errors`
  *inputs* if a control implements them — check whether that alone produces
  correct `aria-invalid`/`aria-describedby` on a plain native `<input>`
  bound via `[field]`, or whether the aria attributes still need to be set
  by hand in this template the way the reactive field components do it.
  Don't claim the wiring is equivalent without checking it renders in the
  DOM.
- On submit: use the preview API's own `submit()` helper against a fake
  0.9s-delayed submit function (mirror the reactive version's submitting
  state — Save button becomes "Saving…" and disabled, Cancel disabled too),
  then simulate a server-side field rejection (same shape as the reactive
  dialog's `fieldErrors` input: `{ backgroundImageUrl: ['message'] }`) and
  show how a server-side error is surfaced back into the signal-forms field
  state — Signal Forms has no built-in concept of "errors that came from an
  HTTP response," so this has to be done by hand; don't assume there's a
  one-line API for it without checking.
- Focus management on invalid submit: same requirement as the reactive
  version — move focus to the first invalid field, not just mark it invalid
  visually.

## Comparison write-up (required deliverable, not optional)

A short markdown doc, `docs/day14-task2-comparison.md`, with a plain
"where Signal Forms is simpler" / "where it's still rough" section, backed
by what was actually hit while building this, not a generic API comparison
copied from Angular's docs. Cover at minimum: how much boilerplate each
needs for the same three fields, how each handles the whitespace-only rule,
how each handles a server-side field rejection arriving after a client-valid
submit, and how each wires aria-invalid/aria-describedby — with the honest
answer for whether Signal Forms gets that last one for free or not.

## Done means

1. `ng build`, `ng lint`, and existing `ng test` all still pass — the
   reactive dialog and its tests are completely untouched.
2. The new signal-forms demo, opened by hand, actually validates author/text/
   background against the rules above, actually disables during a simulated
   submit, and actually surfaces a simulated server-side field rejection.
3. The comparison doc exists and is specific to what got built, not
   boilerplate.

Do not claim parity with the reactive version anywhere in code comments or
the comparison doc unless it was actually verified to behave the same way —
if something is rougher, weaker, or missing in the preview API, say so
plainly instead of smoothing it over.
