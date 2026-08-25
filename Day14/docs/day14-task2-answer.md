# Day 14, Task 2 — Signal Forms preview

## Starting point

`quote-form-dialog.ts` (reactive forms) is the real, hand-verified,
Day-14-Task-1 form. This task rebuilds the same three fields side by side
with Angular's experimental Signal Forms preview API
(`@angular/forms/signals`, confirmed present in this exact install —
Angular 21.2.21), as a new, additive component. It does not touch or
replace the reactive dialog, and it is not wired into the real create-quote
flow — it lives at its own route, `/quotes/signal-forms-demo`, for a
hands-on comparison.

## The brief I gave the agent

> Read the real `POST /api/quotes` contract from `src/app/core/models/
> quote.ts` (mirrored from `QuotesApi/Models/Quote.cs` /
> `QuoteEndpointExtensions.cs`) — `{ author, text, backgroundImageUrl }`.
> `author`: required, whitespace-only counts as empty, max 200 chars.
> `text`: same rule, max 1000 chars. `backgroundImageUrl`: always one of six
> bundled options in this UI, defaulting to `DEFAULT_QUOTE_BACKGROUND_URL`.
> No other fields exist on this endpoint — don't invent one.
>
> Build a new, standalone demo component using `@angular/forms/signals`
> (confirmed installed at `node_modules/@angular/forms/types/signals.d.ts`)
> that rebuilds these same three fields with the same validation rules,
> including the exact whitespace-only rule from `shared/forms/
> no-whitespace.validator.ts` — not a looser or stricter approximation.
> Wire real `aria-invalid`/`aria-describedby` pointing at the actual visible
> error text, and actually check in the rendered DOM whether the preview
> API's field-binding directive gives you that for free on a plain native
> element, or whether it still has to be set by hand — don't assume either
> way, verify it. Add a simulated submitting state (disable Save/Cancel,
> "Saving…") and a simulated server-side field rejection arriving after a
> client-valid submit, the same shape as the reactive dialog's `fieldErrors`
> input, and show how Signal Forms surfaces that — there's no guarantee
> there's a built-in mechanism for it; check rather than assume. Move focus
> to the first invalid field on an invalid submit, same bar as the reactive
> version. Do not claim parity with the reactive version anywhere unless you
> actually verified it behaves the same way.
>
> Also write a short, specific comparison doc grounded in what you actually
> hit while building this — not a generic "pros and cons of experimental
> APIs" essay.

## What the agent shipped

New files only; `quote-form-dialog/` and its three shared field components
are untouched:

- `quote-form-signal-demo.ts` / `.html` / `.scss` / `.spec.ts` (10 tests)
- a new route, `quotes/signal-forms-demo`, added to `app.routes.ts`
  (placed before the `quotes/:id` param route so the literal path isn't
  swallowed as an id)
- `docs/day14-task2-comparison.md`

```ts
protected readonly quoteForm = form(this.model, (p) => {
  required(p.author, { message: 'Author is required.' });
  maxLength(p.author, QUOTE_LIMITS.authorMaxLength, { message: '...' });
  validate(p.author, ({ value }) => {
    const v = value();
    if (v.length === 0 || v.trim().length !== 0) return undefined;
    return { kind: 'whitespace', message: 'Author cannot be only spaces.' };
  });
  // ...same for text; backgroundImageUrl gets required() only.
});
```

Submit uses the preview API's own `submit()` against a fake 900ms action
that always rejects the default background (the only way to actually see a
server-side rejection on a field with no client-side rule that can fail on
its own):

```ts
if (backgroundImageUrl === DEFAULT_QUOTE_BACKGROUND_URL) {
  return {
    kind: 'server',
    message: 'That background is temporarily unavailable. Pick another.',
    fieldTree: this.quoteForm.backgroundImageUrl,
  };
}
```

Focus management uses `FieldState.focusBoundControl()` — no `viewChild`
refs, unlike the reactive dialog's three.

## The one thing I caught, and made it fix

Independently running axe-core against the live page (not the agent's own
spec suite — a real browser hitting the real dev server) surfaced two real,
non-pre-existing violations: `landmark-no-duplicate-main` and
`landmark-main-is-top-level`. Cause: the new component's template wrapped
its content in `<main class="signal-demo">`, but this component renders
*inside* `MainLayout`, which already has its own top-level `<main>` — so
every other page in the app (`quotes-page.html`, `quote-detail-page.html`)
uses `<section>` for exactly this reason, and this one didn't.

Sent back with the confirmed axe output and the sibling-page convention;
fixed by changing the wrapper to `<section class="signal-demo"
aria-label="Signal Forms demo">`. Re-ran `ng build`/`ng lint`/`ng test`
(66/66) and my own independent browser verification afterward — both axe
violations are gone, nothing else changed.

## Verification log

Two layers, run independently, not just the agent's own spec file:

**Unit (Vitest, 10 new tests in `quote-form-signal-demo.spec.ts`, isolated
fixture):** aria-invalid/aria-describedby absent before interaction, present
and pointing at real text after; `maxlength` set as a real DOM attribute
with no hand-written binding; whitespace-only rejected distinctly from
`required`; focus moves to the first invalid field; submitting disables
both buttons and shows "Saving…"; a simulated server rejection shows up on
the right field and clears on edit without a second submit; a clean submit
with a non-default background succeeds.

**Browser (`verify-signal-form-demo.mjs`, real dev server + stub API, run by
me independently, not written by the agent):**

```
PASS  the demo page renders the three fields
PASS  pristine load has no aria-invalid on any field
PASS  pristine load shows no error text
PASS  an empty submit shows both required errors
PASS  empty submit focuses Author, not Text or Background
PASS  Author's aria-describedby resolves to the exact visible error text
PASS  axe finds no serious/critical violations on the empty-invalid form
PASS  whitespace-only author is reported as its own error, not silently accepted
PASS  over-limit text is rejected, and focus moves to Text, not back to Author
PASS  maxlength is a real native attribute on the textarea (set by maxLength(), not hand-written)
PASS  the submitting state disables Save and shows "Saving…"
PASS  Cancel is disabled while submitting
PASS  a server-side rejection of a client-valid field shows up as a real error
PASS  focus moves to the server-rejected field
PASS  the rejected field's aria-describedby resolves to the exact server message
PASS  axe finds no serious/critical violations on the server-error state
PASS  changing the rejected field clears its error without resubmitting
PASS  a fully valid submit with a non-default background succeeds
PASS  no unexpected API-level console errors during the whole run

19/19 checks passed
```

States/edges exercised: pristine, empty-invalid, whitespace-only-invalid,
over-limit-invalid, submitting, server-error, clean submit — the same set
as the reactive form's own verification, driven the same way (real browser,
keyboard-first for the field-level checks, axe swept on the open form). One
transient flake was seen and not reproduced on two immediate re-runs (a
stray 401 on an account-creation call, not tied to any code path this
component touches) — attributed to stub-API/dev-server timing, the same
category as a flake noted in the reactive form's own verification log.

`ng build`, `ng lint`, and the full `ng test` suite (66/66 — 56 pre-existing
+ 10 new) all pass, confirming the reactive dialog and its own 3 tests are
completely untouched.

### Screenshots, run against the real dev server (localhost:4200) and stub API

Also fixed while verifying: `quotes-brand-mark.svg`, `quotes-hero-bg.jpg`,
and all 6 `quote-backgrounds/mountain-*.jpg` files referenced in code were
missing from `public/` — a pre-existing, app-wide gap (404s on every page,
not something this task introduced), sourced from Unsplash (Unsplash
License — free for any use) and added so the background picker and hero
image are showing real images below, not broken links.

![Quotes page with the hero background and brand mark now real images, not 404s](../verification/screenshots/day14-task2-quotes-page-images.png)

![Signal Forms demo, pristine — no errors, real images in the background picker](../verification/screenshots/day14-task2-signal-demo-pristine.png)

![Empty invalid submit — both required errors shown, focus on Author](../verification/screenshots/day14-task2-signal-demo-invalid.png)

![Submitting state — Save disabled, "Saving…"](../verification/screenshots/day14-task2-signal-demo-submitting.png)

![Server-side rejection of a client-valid background — real error text, focus moved to Background](../verification/screenshots/day14-task2-signal-demo-server-error.png)

![Clean submit with a non-default background — saved (simulated) result shown](../verification/screenshots/day14-task2-signal-demo-success.png)

## Comparison with the reactive version

Full write-up: `docs/day14-task2-comparison.md`. Summary — where it's
simpler: the schema function is genuinely shorter than assembling a typed
`FormGroup`; `submit()` calls `markAllAsTouched()` and lets a rejection
target a specific field without being asked; `focusBoundControl()` removes
three `viewChild` refs the reactive dialog needs purely for `.focus()`.
Where it's rough: aria wiring is not free on a native element — checked
three ways (the `.d.ts` doc comments, grepping the compiled directive for
any `aria-` code, and a spec test) and confirmed absent, so it's exactly as
much hand work as the reactive field components already do, just moved into
this component's own template instead of three shared, reusable ones.
Mapping an API's `Record<string, string[]>` field-error shape onto a
`FieldTree` is still hand-written either way — `submit()`'s targeted-error
mechanism is real, but it isn't that mapping. And one experimental-API trap
worth flagging for anyone else trying this: the field-binding directive is
`FormField`/`[formField]`, not `Field`/`[field]` as most floating examples
assume, and native controls only listen for the `input` event, not
`change` — a `<select>` driven by dispatching only `change` silently never
updates the model.

## What breaks if the Week-1 API contract changes

Same answer as the reactive version, for the same reason: a new required
field needs a new line in the schema function and a new field in the
template — nothing here auto-discovers the contract. The 200/1000 limits
changing needs one `QUOTE_LIMITS` update, same as the reactive form, since
both read from the same constant. `backgroundImageUrl` becoming truly
required server-side with no default: no change needed, since the select
already always has one of six known-good values selected.
