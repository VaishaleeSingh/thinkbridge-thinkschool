# Reactive forms vs. Signal Forms preview, for one real form

What this is: a comparison grounded in actually building the same three
fields (author text input, text textarea, backgroundImageUrl select) twice —
once already existing as `QuoteFormDialog` (reactive forms), once new as
`QuoteFormSignalDemo` (`@angular/forms/signals`, Angular 21.2.21, confirmed
`experimental`) — and running both. Every claim below was checked by reading
the installed package's `.d.ts`/compiled `.mjs`, by what `ng build` accepted
or rejected, or by what a Vitest spec observed in the rendered DOM. Where
something wasn't checked, it isn't claimed.

Files: `quote-form-dialog.{ts,html}` and its three field components
(reactive, untouched) vs. `quote-form-signal-demo.{ts,html}` (new). Both are
demo/dialog components, not reused elsewhere, except that the reactive
version's field components (`TextField`, `TextareaField`, `SelectField`) are
also used by other forms in this app and the signal demo's markup is not —
that asymmetry matters below and is called out where it does.

## Boilerplate for these three fields

Non-comment, non-blank lines: `quote-form-dialog.ts` is 125,
`quote-form-signal-demo.ts` is 116 — close enough not to call a difference.
The form-definition code itself is shorter in Signal Forms: one `form()`
call with a schema function replaces a typed `FormGroup` of three
`FormControl`s plus each one's `nonNullable`/`validators` array. But that
comparison is not the whole picture:

- The reactive dialog's `.html` is 49 lines because it delegates all
  label/error/aria markup to three shared field components
  (`text-field.ts` 82 + `.html` 32, `textarea-field.ts` 75 + `.html` 44,
  `select-field.ts` 65 + `.html` 30 lines) that are reused by other forms in
  this app. The signal demo's `.html` is 112 lines because it writes that
  markup inline, once, for this one form — nothing here is proven reusable.
  A fairer statement than "Signal Forms needs less code" is: for a *second*
  form with the same three field types, the reactive version pays close to
  zero extra `TextField`/`TextareaField`/`SelectField` cost, while a signal-
  forms app would need to either write the equivalent inline markup again or
  build its own reusable `FormValueControl` components — which this demo did
  not attempt, so nothing here says how much *that* would cost.
- Message text is inline per-rule here (`required(p.author, { message:
  'Author is required.' })`) instead of centralized. The reactive version's
  62-line `validation-messages.ts` turns `{ maxlength: {...} }` into English
  once for the whole app; this demo's messages are one-off strings on each
  `required()`/`maxLength()`/`validate()` call. For one form that's a wash;
  for many forms sharing the same three rules, the reactive app has one
  place to fix a wording bug and this demo's approach has one per call site
  (unless a shared message helper were built here too — not attempted).

## The whitespace-only rule

`shared/forms/no-whitespace.validator.ts`'s `noWhitespace()` deliberately
returns `null` for an empty value (so an empty field reports only
`required`, not `required` + `whitespace`) and `{ whitespace: true }` for a
non-empty, all-whitespace one. `quote-form-signal-demo.ts` replicates this
with `validate(p.author, ({ value }) => { const v = value(); if (v.length
=== 0 || v.trim().length !== 0) return undefined; return { kind:
'whitespace', message: '...' }; })` — same two branches, same reasoning,
same result, just written against `FieldContext.value()` instead of
`AbstractControl.value`. Confirmed by a spec test (`quote-form-signal-demo
.spec.ts`, "rejects a whitespace-only author..."): submitting `"   "` for
author shows exactly `"Author cannot be only spaces."`, not a `required`
message, matching the reactive rule's own stated intent. Signal Forms has no
built-in "not just whitespace" validator any more than reactive forms does
— both hand-roll the same one-line check, in the same shape (a function
over the field's current value returning null-or-an-error).

## `maxLength()` is more than a validator — one real surprise

`maxLength(p.author, QUOTE_LIMITS.authorMaxLength, {...})` does not only
validate; it sets `MAX_LENGTH` field metadata, and `FormField` (the
directive behind `[formField]`) reads that and applies it as the native
`maxlength` DOM attribute — confirmed by `ng build` itself refusing a
hand-written `[attr.maxlength]` on the same element with error `NG8022:
Binding to '[attr.maxlength]' is not allowed on nodes using the
'[formField]' directive`, and by a spec assertion that `author.getAttribute
('maxlength')` is `"200"` with no such binding in the template at all. The
reactive `TextField` needs a `maxLength` input wired to `[attr.maxlength]`
by hand for the same effect. Same story for `required`/`disabled`/
`readonly`/`min`/`max`/`minLength` — confirmed by reading
`setNativeDomProperty()` in `fesm2022/signals.mjs`, which sets exactly those
seven and nothing else on a native element.

## Server-side field rejection arriving after a client-valid submit

**The brief's working assumption — "no built-in concept for this" — was
half right.** Reading `submit()`'s type signature and its own doc example in
`_structure-chunk.d.ts`, and then reading `_validation_errors-chunk.mjs`'s
implementation directly:

- `submit(form, { action })`'s `action` can return a `ValidationError` (or
  array of them) carrying a `fieldTree` pointing at *any* field in the tree
  — including a nested one, like `this.quoteForm.backgroundImageUrl` — and
  `submit()` calls `setSubmissionErrors()` to route each error onto exactly
  that field. That much is a real, built-in mechanism; it's what
  `QuoteFormDialog`'s reactive `fieldErrors` input does not have an
  equivalent for.
- What is **not** built in, and had to be written by hand exactly the way
  the reactive dialog's `applyFieldError()` does: mapping the API's actual
  shape — `Record<string, readonly string[]>`, keyed by field name as a
  string (`{ backgroundImageUrl: ['message'] }`) — onto a `FieldTree`
  reference. `submit()`'s mechanism wants an actual `FieldTree`, decided
  inside the same function that already has one in scope
  (`this.quoteForm.backgroundImageUrl`); it does nothing to help translate
  an arbitrary string key from an HTTP response into that reference. A real
  integration (parsing an actual `ValidationProblemDetails`) would still
  need a small lookup table from field name to `FieldTree`, same shape of
  work as the reactive version's per-field `applyFieldError()` calls.
- Two more things found by reading the same file, not assumed: `submit()`
  calls `markAllAsTouched()` on the whole tree *before* checking validity,
  so — unlike the reactive dialog, which calls
  `this.form.markAllAsTouched()` itself inside `submit()` — nothing in
  `quote-form-signal-demo.ts` has to do that by hand. And a field's
  server-set errors (`submissionErrors`) are a `linkedSignal` sourced on
  that field's own value — so editing the rejected field clears the
  rejection with no code at all, whereas the reactive dialog's
  `applyFieldError()` has an explicit comment explaining why clearing an
  `apiError` needs `updateValueAndValidity()` rather than a plain
  `setErrors(null)`. Confirmed by a spec test: after the simulated
  rejection on `backgroundImageUrl`, changing the `<select>`'s value alone
  (no re-submit) clears its `aria-invalid` immediately.
- One implementation quirk worth flagging for anyone testing this by hand
  or scripting it (e.g. Playwright): the native-control binding only
  listens for the `input` DOM event, not `change` — confirmed by grepping
  `signals.mjs` and by a failing-then-fixed spec test. A `<select>` driven
  by dispatching only a `change` event (a natural first guess, since that's
  what selects fire on a real user pick and what the reactive `SelectField`
  test above tolerates via Angular's own `ReactiveFormsModule` handling)
  will not update the signal-forms model at all; `input` is required.
- `onInvalid` (a `submit()` option) only fires for the client-invalid path
  — a rejection the `action` itself returns does not call it. Both paths
  need to end up at the same focus-management code regardless, so
  `quote-form-signal-demo.ts` checks `submit()`'s own boolean return value
  after every attempt instead of relying on `onInvalid` alone.

## aria-invalid / aria-describedby: the honest answer

**No, Signal Forms does not wire these for free on a native element.**
This was checked three ways, not assumed:

1. `FormUiControl`'s doc comments describe `invalid`/`errors`/etc. as
   *inputs the directive fills in if the bound component declares them* —
   i.e. this is for a custom `FormValueControl` component that opts in by
   declaring an `invalid` input, not something that reaches a bare
   `<input>`.
2. Grepping the compiled directive, `fesm2022/signals.mjs`, for
   `aria-invalid` / `aria-describedby` / `setAttribute` finds no ARIA
   attribute-setting code anywhere in the file — only the seven DOM
   properties in `setNativeDomProperty()` listed above.
3. A spec test (`quote-form-signal-demo.spec.ts`, "renders no aria-invalid/
   aria-describedby before any interaction") confirms it directly on the
   rendered DOM: with `[formField]="quoteForm.author"` and no other markup,
   the native `<input>` has neither attribute before any submit is
   attempted.

So `quote-form-signal-demo.html` sets both by hand, on every field, exactly
like `TextField`/`TextareaField`/`SelectField` do for the reactive version —
`[attr.aria-invalid]="hasError(...) ? 'true' : null"` and
`[attr.aria-describedby]` pointing at the id of whichever `<p>` is actually
showing (the error when there is one, the hint otherwise). A second spec
test confirms the two are not just present but correct: after an invalid
submit, `aria-describedby` on the author input resolves to an element whose
visible text is exactly `"Author is required."` — not a static hard-coded
id that happens not to 404.

## Focus management on invalid submit

Both move focus to the first invalid field, and both were verified to do so
in the actual rendered DOM (not just "the control became invalid").
`QuoteFormSignalDemo` uses `FieldState.focusBoundControl()` — built in, no
`viewChild` refs to the field components needed, unlike the reactive
dialog's `authorField`/`textField`/`backgroundField` `viewChild.required()`
trio kept purely so `.focus()` could be called on them. This is a genuine,
verified simplification: three fewer `viewChild` lines and three fewer
`focus()` passthrough methods on the field components, replaced by one
method call already on the `FieldState` every field exposes.

## Net take

For this exact three-field form: Signal Forms' schema function is a real
improvement over assembling a typed `FormGroup`, its `submit()` genuinely
handles "mark everything touched" and "target a field-level error" without
being asked, and `focusBoundControl()` removes the reactive version's
`viewChild` boilerplate outright. It is *not* an aria/accessibility win by
itself — that work is identical in kind and amount to the reactive version,
just moved into this component's own template instead of three shared field
components; and it is not a complete answer for "server rejected this
field" — the API-response-shape-to-field mapping is still hand-written
either way. Being `@experimental` (every exported symbol in
`_structure-chunk.d.ts` and `signals.d.ts` carries that tag) is not a
formality: the directive is called `FormField`/`[formField]`, not `Field`/
`[field]` as most examples elsewhere assume, and at least one behavioral
detail (native controls listening for `input`, not `change`) is exactly the
kind of thing that changes without notice at this stage.
