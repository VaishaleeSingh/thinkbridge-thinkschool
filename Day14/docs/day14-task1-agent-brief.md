# Day 14, task 1 — the brief handed to the agent

Recorded verbatim, before the agent ran, so the review that follows can be read
against what was actually asked rather than against what turned out to be built.

---

## Context

There is already a hand-coded reactive create/edit-quote form in this app:
`features/quotes/components/quote-form-dialog/` plus three shared field
components it composes — `shared/components/text-field/`,
`shared/components/textarea-field/`, `shared/components/select-field/`. Read
all four before changing anything. They already provide:

- a real `<label for>` on every field, `aria-describedby` pointed at whichever
  of hint/error/counter is currently showing, and `aria-invalid` while invalid
- `Validators.required` + `Validators.maxLength` + a custom `noWhitespace()`
  validator, matching the API's own rules
- error text shown only after `touched || dirty`, so the form doesn't open
  already covered in red
- a hidden submit button inside the `<form>` so Enter submits it, with the
  visible Save/Cancel buttons living in the modal's footer, outside the form

Do not rebuild this. Your job is one gap in it, described below.

## The API — real contract, from `QuotesApi/Models/Quote.cs` and
`QuotesApi/Extensions/QuoteEndpointExtensions.cs` (Day7/piece2/QuotesApi)

    POST /api/quotes
    body: { author: string | null, text: string | null, backgroundImageUrl?: string | null }
    requires bearer token + "can-edit-quotes" scope

Server-side validation, in this exact order, each returning
`400 ValidationProblem` keyed by lowercase field name:

- `author`: required (whitespace-only counts as empty), max 200 characters
- `text`: required (whitespace-only counts as empty), max 1000 characters
- `backgroundImageUrl`: OPTIONAL — if omitted the server derives a default
  deterministically from the text; if provided, must start with
  `/quote-backgrounds/` and be ≤500 characters

There is no `title`, `category`, `tags`, or any other field. The client's
`QUOTE_LIMITS` constant (200 / 1000) already matches this exactly — verify
that yourself before touching anything, rather than trusting this brief.

## The one gap: focus management on submit

`QuoteFormDialog.submit()` currently does this when the form is invalid:

```ts
if (this.form.invalid) {
  this.form.markAllAsTouched();
  return;
}
```

This makes every invalid field's error text appear, but moves focus nowhere.
A sighted mouse user sees the red text appear next to whatever they left
blank. A keyboard or screen-reader user gets no signal at all — focus stays
on the submit button (or wherever it was), and nothing announces that
anything happened, let alone which field needs fixing.

**Requirement:** on an invalid submit, move focus to the first invalid
control's native input/textarea/select element, in DOM order (author, then
text, then background) — not just the first one Angular happens to iterate.
Do this without changing what TextField/TextareaField/SelectField already
render; you may add a way for the parent to ask a field component for a
reference to its focusable element (a method or an `ElementRef`), but do not
duplicate the aria wiring those components already own.

Also handle the server-error case: when `fieldErrors` arrives from a failed
API submit (the second `effect()` in the constructor), the field(s) it
marks invalid should receive the same treatment — focus the first one the
server rejected, not just the client-invalid ones.

## Done means

- `npx ng test`, `npx ng lint`, `npx ng build` all clean
- A test proving: submitting an empty form focuses the Author field (the
  first one in DOM order with an error)
- A test proving: submitting with a valid author but an over-limit quote text
  focuses the Text field, not Author
- A test proving: a server-rejected `backgroundImageUrl` (simulated via the
  `fieldErrors` input) focuses that field, not the first DOM field, when
  author/text are otherwise valid
- No new `any`, no new field invented, no change to the validators'
  limits already in `QUOTE_LIMITS`
