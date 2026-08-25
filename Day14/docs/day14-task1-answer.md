# Day 14, Task 1 — Reactive forms + accessibility

## Starting point

A hand-coded reactive create/edit-quote form already existed
(`quote-form-dialog.ts` + three shared field components: `text-field`,
`textarea-field`, `select-field`), built earlier this week against the real
`POST /api/quotes` contract. It already had real `<label for>` associations,
`aria-invalid`/`aria-describedby` wired to whichever of hint/error/counter is
showing, `Validators.required`/`maxLength`/a custom `noWhitespace()` matching
the API's own rules, and errors shown only after `touched || dirty`. What it
was missing — and the entire scope of this brief — was focus management.

## The brief I gave the agent

> The existing form's a11y wiring is solid. The one gap: on an invalid
> submit, `form.markAllAsTouched()` makes every error appear but moves focus
> nowhere — a keyboard/screen-reader user gets no signal which field to fix.
>
> Real API contract (from `QuotesApi/Models/Quote.cs` and
> `QuoteEndpointExtensions.cs`, read directly, not assumed):
> `POST /api/quotes` — `{ author, text, backgroundImageUrl? }`. `author`
> required, whitespace-only counts as empty, max 200 chars. `text` same
> rule, max 1000 chars. `backgroundImageUrl` optional — server derives a
> default if omitted; if given, must start with `/quote-backgrounds/`, max
> 500 chars. No other fields exist.
>
> Add: on an invalid submit, move focus to the first invalid control's
> native element, in DOM order (author, text, background) — not whatever
> order Angular iterates the FormGroup in. Same treatment when a server
> `fieldErrors` response rejects a field the client thought was fine. Do
> this by exposing a `focus()` method on each of the three field components,
> without touching their existing aria/validation rendering. Three tests:
> empty submit → Author focused; valid author + over-limit text → Text
> focused, not Author; a server-rejected `backgroundImageUrl` (author/text
> valid) → Background focused.

## What the agent shipped

Added a `viewChild.required<ElementRef<...>>` + public `focus()` method to
each of `TextField`, `TextareaField`, `SelectField` — no change to their
existing label/aria/error rendering. In `QuoteFormDialog`:

```ts
private readonly authorField = viewChild.required(TextField);
private readonly textField = viewChild.required(TextareaField);
private readonly backgroundField = viewChild.required(SelectField);

protected submit(): void {
  if (this.form.invalid) {
    this.form.markAllAsTouched();
    this.focusFirstInvalidControl();
    return;
  }
  // ...
}

private focusFirstInvalidControl(): void {
  if (this.form.controls.author.invalid) { this.authorField().focus(); return; }
  if (this.form.controls.text.invalid) { this.textField().focus(); return; }
  if (this.form.controls.backgroundImageUrl.invalid) { this.backgroundField().focus(); }
}
```

The server-error effect calls the same `focusFirstInvalidControl()`, guarded
so it only fires when `applyFieldError` actually just marked something
invalid this run — not on the effect's very first pass, before the dialog
has even opened, which would otherwise steal focus for no reason.

Three tests were added driving the real DOM (clicking the visible "Save
quote" button, dispatching `input` events, asserting on
`document.activeElement`) rather than calling `submit()` directly — proving
what a keyboard user actually experiences, not just which `FormControl` ends
up invalid.

## The one thing I caught, and made it fix

Keyboard-driving the finished form for real turned up a bug the focus fix
didn't cause but directly undermined: opening the "New quote" dialog put
focus on the header's **"Close dialog" (×) button**, not on the Author
field or anywhere in the form. `showModal()`'s own autofocus algorithm
focuses the first focusable element in DOM order when nothing has an
`autofocus` attribute, and in `modal.html` the header (with the × button)
comes before the body — true of every dialog in the app, confirm dialogs
included. The practical effect: a keyboard user's very first keystroke after
opening the form — Enter, the natural "let's go" — closed the dialog instead
of doing anything with the field they meant to fill in. Careful focus
management on submit is worthless if the dialog mis-focuses on open.

Fixed in the shared `Modal` component (not the form itself, since every
dialog in the app was affected): after `showModal()`, focus is moved
explicitly to the first focusable element inside `.dialog__body`, falling
back to the browser's default (the × button) for a dialog whose body has no
focusable content at all, e.g. a plain confirm message.

## Verification log — states and edges actually exercised

Built a dedicated Playwright + axe-core harness (`verify-quote-form.mjs`),
driven almost entirely by keyboard, against the real form:

```
PASS  the dialog is a real <dialog>, opened as a modal
PASS  focus moved into the dialog on open                      (the Modal fix)
PASS  an empty submit shows both required errors
PASS  submitting an empty form focuses the Author field, not just marks it red
PASS  the focused Author field is wired for assistive tech      (aria-invalid + aria-describedby)
PASS  axe finds no serious/critical violations on the empty-invalid form
PASS  whitespace-only text is reported as empty, not silently accepted
PASS  focus moved to Text, not back to Author, for a text-only error
PASS  Escape closes the dialog
PASS  every dialog control is reachable by Tab alone, in a sensible order
PASS  the submitting state disables the form rather than allowing a double-submit
PASS  Cancel is disabled while submitting
PASS  the dialog closes once the submit actually resolves
PASS  a server-rejected field the client thought was fine is reported by name
PASS  focus moves to the server-rejected field, not wherever it happened to be
PASS  the dialog stayed open on a server rejection -- nothing was lost
PASS  axe finds no serious/critical violations on the server-error state
PASS  no unexpected console errors or unhandled exceptions during the whole run

18/18 checks passed
```

States/edges: empty, invalid (whitespace-only — the real invalid value a
keyboard can produce, since the textarea's `maxlength=1000` attribute makes
an over-the-limit string unreachable by typing), submitting (an artificial
900ms delay on the stub's `POST /api/quotes`, added specifically for this),
and server-error (a one-shot stub flag forces the API to reject
`backgroundImageUrl` even though the client considered it valid, proving the
focus fix's actual reason for existing). Axe-core run scoped to the open
dialog specifically — the whole document has one pre-existing, unrelated
color-contrast issue in the main nav (4.3:1 vs 4.5:1) that has nothing to do
with this form and would have made a real regression here hard to see
through the noise.

Unit tests: 56/56 passing (53 pre-existing + 3 new). Lint and build both
clean. The full pre-existing 110-check browser harness (`verify-ui.mjs`) was
re-run after the Modal change and still passes 110/110 — the fix is
additive, not a behavior change for the dialogs that already worked.

Re-run independently on my own machine (not just in the sandbox that built
it), against the real dev server and the stub API, with the same result:

![18/18 checks passed, run locally](../verification/screenshots/day14-quote-form-verification-run.png)

## A second pass, going deeper

Three more things worth recording, since "verify it before you stand behind
it" doesn't stop at the first green run:

**Axe was silently dropping findings.** The harness only failed on
serious/critical violations, which is right — but anything at moderate
impact was being discarded rather than reported, which reads as "axe found
nothing" when it may have found something real just below the failure
threshold. Fixed: moderate/minor violations are now logged (not failed on)
so nothing found disappears without a trace.

**`aria-describedby` was checked for existing, not for being right.** The
original check only asked "does this attribute have a value?" — which would
also pass if it pointed at a stale id, an empty node, or the hint instead of
the error. Strengthened to resolve the id and assert the referenced
element's actual text matches the error shown on screen, so a describedby
wired to the wrong (but non-empty) target would now be caught.

**The over-the-limit message was never actually tested — and testing it
found a real double-click race, which I then had to walk back.** The
textarea's own `maxlength=1000` attribute means the keyboard literally
cannot produce a value long enough to trigger `Validators.maxLength`, so the
first pass quietly settled for testing whitespace-only instead and never
exercised "Quote must be 1000 characters or less." at all. Setting the value
directly (bypassing the DOM's own limit) closed that gap and confirmed the
message. While driving the form that way, sending two `click` events on Save
with **zero** gap between them got a second `POST /api/quotes` through: the
existing guard (`Button.isDisabled()`) reads a signal *input* that is only
refreshed by Angular's change detection, so two dispatches with nothing
between them can both read "not loading yet." A fix (a synchronous timestamp
debounce) closed it — but broke a genuine, unrelated case in the existing
110-check harness: clicking "New collection" twice, once per collection,
minutes apart, which the debounce's cooldown window couldn't tell apart from
an accidental double-click. Reverted, because a real double-click, two Enter
presses, or a screen reader's own activation all cross at least one browser
task boundary — exactly where change detection gets its chance to catch up
— so the original guard already covers every double-click that can actually
happen. The zero-gap case has no real-world equivalent; the test now uses a
realistic ~20ms gap instead, and both the reasoning and the regression it
caused are recorded in `Button.onClick()`'s own comment for whoever touches
this next.

## What breaks if the Week-1 API contract changes

A new required field added to `POST /api/quotes`: the form would need a new
`FormControl` and field component — nothing here auto-discovers the
contract, by design, since a form silently accepting fields it doesn't
render would be worse. The 200/1000 character limits changing: only
`QUOTE_LIMITS` needs updating; `Validators.maxLength` and the error message
both read from it, so nothing else drifts. `backgroundImageUrl` becoming
truly required server-side with no default: no client change needed, since
the select already always has a value selected (`DEFAULT_QUOTE_BACKGROUND_URL`).
