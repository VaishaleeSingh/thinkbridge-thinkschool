# Day 13 — Angular 21 signals, zoneless, standalone

A front end for this repository's own API, in `Day13/quotes-web`. Angular 21.2,
standalone throughout, no NgModules, no zone.js, signals as the only state
mechanism.

The API is not new and was not rewritten. Two things were added to it, both
because a browser client needs them and nothing before Day 13 did — a CORS policy
and a way to create an account. Those changes are in `Day7/piece2`, in place: the
API is one application carried forward, and a second copy of 159 C# files under
`Day13/` would have been a fork pretending to be a snapshot.

---

## What was inspected first

Before any code: the existing repository, `Day7/piece2` (the current API), its
endpoint files, models, read models, `QuotesDbContext`, `AuthService`,
`InfrastructureExtensions`, `launchSettings.json`, the CI workflow, `.gitignore`,
and the repo conventions in `README.md`.

That inspection produced the real contracts every model in `src/app/core/models`
is typed from, and it surfaced two gaps that had to be closed before a browser
could talk to the API at all:

| Gap | Why it existed | What was added |
|---|---|---|
| No CORS policy | Every client before Day 13 was a server, a CLI or a test — none of which the browser's same-origin policy applies to | `QuotesApi/Configuration/CorsOptions.cs`, `QuotesApi/Extensions/CorsExtensions.cs`, two calls in `Program.cs`, a `Cors:AllowedOrigins` entry in `appsettings.Development.json` |
| No way to create a user | The API could verify a password but never set one; there is no register endpoint and no seed, so every user row had to be inserted by hand | `POST /api/auth/register` in `AuthEndpointExtensions.cs` |

Both are narrow. CORS names its origins rather than allowing any, lists only the
two headers and three verbs the SPA actually uses, does not allow credentials
(this API is bearer-authenticated, never cookie-authenticated), and fails at
startup on a malformed origin rather than producing a browser error nobody can
trace. Register validates, hashes through the existing `PasswordHasher`, returns
409 on a duplicate, and returns the same token pair a login does.

---

## Architecture

**Standalone, no NgModules.** There is no `AppModule` and no feature module.
`main.ts` calls `bootstrapApplication(App, appConfig)`; every component,
directive and pipe declares its own `imports`. `grep -r "NgModule" src` returns
nothing.

**Zoneless.** Angular 21 scaffolds without zone.js — it is not in
`node_modules`, and no polyfill is configured, so Zone-based change detection is
not available to this application even by accident.
`provideZonelessChangeDetection()` is stated explicitly in `app.config.ts` anyway,
as the greppable declaration of the mode and as insurance against a future
dependency pulling zone.js back in.

What that means in practice, and the reason the state layer looks the way it
does: change detection still runs — what changes is what schedules it. Zone.js
patches every async browser API so Angular can be told "something finished, check
everything". Zoneless removes the patching and the guessing: a view is refreshed
when a signal it reads changes, a template listener fires, `markForCheck` is
called, or a view is attached. So state that is not a signal, mutated outside a
template listener, will not re-render anything. A promise resolving inside
`QuotesStore.load()` updates the UI because `quotes` is a signal the template
reads — not because a request completed.

**Signals.** `signal()` for pushed-in facts, `computed()` for everything
derivable, `effect()` only for genuine side effects. The four view states of every
API-driven screen are `computed()`, not flags:

```ts
readonly showLoading  = computed(() => this.loading() && this.quotes().length === 0);
readonly showError    = computed(() => !this.loading() && this.failure() !== null);
readonly showEmpty    = computed(() => !this.loading() && this.failure() === null && this.quotes().length === 0);
readonly showNoMatches= computed(() => !this.showLoading() && this.quotes().length > 0 && this.matchCount() === 0);
```

Deriving them makes "empty state and a list on screen at once" unrepresentable
rather than merely unlikely. `effect()` appears seven times in the whole
application, every one of them a side effect on something outside Angular's
rendering: `sessionStorage` (the session), `localStorage` plus the `data-theme`
attribute (the theme), `showModal()`/`close()` and the body scroll lock (the
dialog), a form control synced to an authoritative input, and server-side
validation messages pushed onto form controls. No `effect()` fetches data and
none computes a derived value.

**`inject()`, not constructor injection.** Every service and component in
`src/app` uses `inject()`. No new code has a parameterised constructor.

**Modern control flow.** `@if` / `@else if`, `@for` with an explicit `track`, and
`@switch` in `Loader`. `grep -rn "\*ngIf\|\*ngFor\|\*ngSwitch" src` returns
nothing.

---

## Shared components

Thirteen, in `src/app/shared/components`, each configured through signal inputs
and each reused. None takes a feature model or touches the API.

| Component | Configured by | Reused in |
|---|---|---|
| `Button` | `variant`, `size`, `type`, `disabled`, `loading`, `loadingLabel`, `fullWidth`, `ariaLabel` | everywhere — 20+ call sites |
| `Card` | `variant`, `padding`, `interactive` | quote card, collection card, filter bar, sign-in panel |
| `TextField` | `control`, `label`, `type`, `placeholder`, `hint`, `autocomplete`, `maxLength` | sign-in (×2), quote form, collection form, both filter bars |
| `TextareaField` | + `rows`, live character counter | quote form |
| `SelectField` | `control`, `label`, `options` | page-size control |
| `Badge` | `tone` | quote card, collection card, collection detail |
| `Loader` | `variant` (skeleton / spinner), `label`, `rows` | quotes page, collections page, collection detail, quote picker |
| `EmptyState` | `title`, `description`, projected actions | quotes, collections, collection detail, quote picker, not-found |
| `ErrorState` | `title`, `message`, `retryable`, `retrying`, `retry` output | quotes, collections, collection detail |
| `Modal` | `open`, `title`, `size`, `busy`, `closed` output, footer slot | quote form, collection form, quote picker, confirm dialog |
| `ConfirmDialog` | `title`, `message`, `confirmLabel`, `busy` | delete a quote, remove from a collection |
| `PageHeader` | `title`, `subtitle`, projected actions | all three main pages |
| `Pagination` | `page`, `size`, `total`, `busy`, `pageChange` output | quotes page |

Plus `shared/forms` (a validation-message function and a `noWhitespace`
validator), `shared/pipes/RelativeTimePipe`, and `shared/utils/nextId`.

Two decisions worth stating because they went against the obvious option:

- **The field components take the `FormControl`, not a value.** A
  `ControlValueAccessor` wrapper has to re-expose validity, touched and disabled
  through a second set of inputs that can disagree with the control. Handed the
  control, there is one copy of that state.
- **`Card` has no `clickable` output.** An input named `clickable` leads
  straight to a clickable `<div>`, which no keyboard or screen reader can use.
  Cards that navigate contain a real `<a>` whose hit area is stretched over the
  card (`collection-card.scss`); `interactive` only controls how the card
  *reacts*.

---

## API integration

Every endpoint the API exposes is connected. No mock data exists in `src/`.

| Endpoint | Used by |
|---|---|
| `POST /api/auth/register` | sign-in page, "create one" mode |
| `POST /api/auth/login` | sign-in page |
| `POST /api/auth/refresh` | the interceptor, on an expired access token |
| `POST /api/auth/logout` | sign out |
| `GET /api/quotes?page=&size=` | quotes page; also the collection detail's quote picker |
| `POST /api/quotes` | new-quote dialog |
| `DELETE /api/quotes/{id}` | quote card, behind a confirmation |
| `GET /api/collections` | collections page |
| `POST /api/collections` | new-collection dialog |
| `GET /api/collections/{id}` | collection detail |
| `POST /api/collections/{id}/items` | quote picker |
| `DELETE /api/collections/{id}/items/{quoteId}` | collection detail, behind a confirmation |
| `DELETE /api/collections/{id}` | collection card, behind a confirmation — added after initial delivery; see "Post-delivery fixes" below |

`GET /api/quotes/{id}` is typed and available on `QuotesApi` but no screen needs
a single quote, so nothing calls it.

Three things the client does because of what the API actually is, rather than
what a generic client would assume:

- **Two collection types, not one.** The API's Day-12 CQRS-lite split returns a
  different shape per screen — the list carries a count and no quotes, the detail
  carries the quotes and each item's `addedAt`. `CollectionListItem` and
  `CollectionDetail` keep that difference instead of merging into one type with
  everything optional.
- **`POST /items` returns the write aggregate, not the read model** — no quote
  text, no `addedAt`. So the client ignores that response body and re-reads the
  detail, rather than patching local state with a shape the screen cannot render.
- **The quotes filter is client-side and says so.** `GET /api/quotes` takes
  `page` and `size` and nothing else; there is no search parameter. The filter
  narrows the current page and the UI states that in the field's hint and in its
  match count ("1 of 12 on this page match"). Fetching every quote in order to
  filter locally would misrepresent what that endpoint is for.

Errors are normalised once, in `core/models/api-failure.ts`, because the API can
fail in five shapes that look nothing alike: `ProblemDetails`,
`ValidationProblemDetails` with a per-field dictionary, a bare 401/403/404 with no
body, a 409 with a title and detail, and a browser-level status 0. Field errors
are routed to the form control they name; everything else becomes one sentence a
person can act on. Nothing is swallowed.

The one non-trivial piece is the interceptor. The API's access tokens last 15
minutes and its refresh tokens last 7 days, so without a refresh path every user
is dumped at the sign-in screen a quarter of an hour in, losing whatever they were
typing, while the credential that would have fixed it sits unused in storage. It
refreshes once, retries once, and stops — and `AuthStore.refresh()` shares one
in-flight promise, because this API treats a re-presented refresh token as theft
and revokes the entire token family. Three concurrent 401s each firing their own
refresh would not waste a request; it would sign the user out.

---

## Design

Aqua and cream, with a black-and-aqua dark theme. Every colour, radius, shadow,
spacing step, type size and duration is a custom property in
`src/styles/_tokens.scss`. No component contains a hex value.

| | Light | Dark |
|---|---|---|
| Ground | `#fbf7ef` cream | `#0b0f0f` near-black |
| Surface | `#ffffff` | `#141b1b` |
| Accent | `#0c7d7b` aqua (4.9:1 on cream) | `#3ccfc9` aqua (8.1:1 on black) |
| Text | `#1d2a2a` (13.9:1) | `#edf2f1` (15.4:1) |
| Muted text | `#5c6b6b` (5.4:1) | `#9db0ae` (7.6:1) |

The dark theme is a redefinition of about twenty colour tokens, not a second
stylesheet — which is only possible because they are custom properties rather
than Sass variables. It is declared twice on purpose: under
`@media (prefers-color-scheme: dark)` scoped to `:root:not([data-theme='light'])`
so an OS preference works on first paint before any script runs, and under
`:root[data-theme='dark']` so the in-app toggle wins in both directions. The
accent has to move between themes — the light aqua is a 1.9:1 whisper on black —
which is why `--color-on-primary` exists: an aqua fill takes white text in light
and near-black text in dark.

Type is Avenir Next LT Pro with a geometric-humanist fallback chain, no webfont
downloaded, and three fluid `clamp()` sizes so headings shrink on a phone without
a media query and stop growing on a 1920px monitor.

---

## Responsive design

Mobile-first, and literally so: the base rules are the phone layout and the three
breakpoint mixins (`sm` 640px, `md` 768px, `lg` 1024px) are min-width only. There
is not one max-width media query in the codebase, which is the structural
guarantee that nothing was designed for desktop and then patched.

The specific decisions, rather than a claim of responsiveness:

- Every grid column is `minmax(0, 1fr)`, never `1fr`. A grid track's default
  floor is `min-content`, so `1fr` lets one long unbroken string push the grid
  wider than the viewport — the most common cause of horizontal page scroll.
  Cards also carry `min-width: 0` for the same reason.
- Navigation takes its own full-width row below 768px so both links keep a 44px
  target, and the signed-in email is hidden below 640px rather than wrapping the
  header onto a third row.
- Dialogs rise from the bottom of the screen on a phone (`align-items: flex-end`)
  and centre from 640px; their footers stack with the primary action on top and
  go side-by-side when there is room; the panel is capped at `min(85dvh, 44rem)`
  and scrolls internally, so its buttons are never below the fold.
- The sign-in panel is top-aligned on a phone and centred from 768px — a
  vertically centred form scrolls off the top when the on-screen keyboard opens.
- Long text is handled where it comes from the API: quote bodies are clamped to
  six lines in a card and three in a collection row, the picker truncates to one,
  and `overflow-wrap: break-word` is set once at document level for every element
  that renders API text.
- The quote picker's candidate list is its own scroll container, so a long list
  scrolls inside the dialog instead of making the dialog taller than the screen.

Measured, not asserted: at 375, 430, 768, 1024, 1440 and 1920 px the browser run
asserts `documentElement.scrollWidth <= clientWidth` on the quotes page, the
collections page and an open dialog — 18 assertions, all passing — and reads the
rendered `grid-template-columns` back to confirm 1-up below 640, 2-up from 640,
3-up from 1024. Screenshots at each width are in `verification/screenshots/`.

---

## Animations

Four keyframes, and each one exists because a state change would otherwise appear
from nowhere:

| Animation | Where |
|---|---|
| `fade-in-up` | cards entering a grid (staggered, capped past the sixth), dialog panels, validation messages, empty and error states |
| `fade-in` | dialog backdrop, empty state |
| `spin` | button and loader spinners |
| `skeleton-sweep` | the loading skeleton's shimmer |

Plus token-driven transitions on buttons (including a 1px press), cards (hover
lift and `:focus-within`), inputs (focus ring), and nav links. Nothing loops
except the two loading indicators, and both stop existing when their request
resolves.

Reduced motion is one rule, not one per component, which is only possible because
every component animates via `--duration-fast` / `--duration-base` rather than
hardcoded milliseconds. Durations collapse to 1ms and animations to a single
iteration rather than being removed outright — an element whose entrance starts at
`opacity: 0` would stay invisible forever if the animation were simply switched
off.

---

## Verification

Everything below was run. Commands and outputs, not summaries.

**Production build** — `npx ng build`, clean, no warnings. 280.60 kB initial
(79.28 kB transferred), well inside the 500 kB budget; every page lazily loaded
(`quotes-page` 25.11 kB, `collection-detail-page` 12.72 kB, `collections-page`
8.60 kB, `sign-in-page` 5.13 kB, `not-found-page` 0.6 kB).

**Type checking** — no TypeScript errors. `strict`, plus
`noImplicitOverride`, `noPropertyAccessFromIndexSignature`, `noImplicitReturns`,
`noFallthroughCasesInSwitch` and `strictTemplates`. No `any` anywhere in `src`.

**Lint** — `npx ng lint`: all files pass, including
`@angular-eslint/template/*` accessibility rules. It found two real problems
during development, both in the dialog, and both were fixed rather than silenced:
`click-events-have-key-events` and `interactive-supports-focus` on the
click-outside-to-dismiss handler. The fix was to rebuild `Modal` on the native
`<dialog>` element with `showModal()` — which also closed a gap that had been
noted as unresolved: focus is now genuinely trapped, and returns to the element
that opened the dialog, because the browser does it. Two suppressions remain, on
that one element, each with a written justification (the rules want a keydown and
a tabindex on what is a backdrop, where Escape and a focusable close button are
the keyboard paths).

**Unit tests** — `npx ng test` (vitest): **6 files, 37 tests, all passing.**
They cover the failure-shape normaliser, the JWT claim reader, both stores'
signal transitions (loading → success → derived values, loading → failure →
error state, loading → empty → empty state, filter without a request, page-size
reset, validation errors returned rather than thrown, the ownership rule), the
refresh-and-retry interceptor including that it does not loop and does not refresh
when there was no token, and the relative-time pipe including the
missing-`Z` timestamp case. `CollectionsStore` has its own spec file, added after
initial delivery specifically to pin the create-failure bug fixed below — see
"Post-delivery fixes."

**Browser flows** — `node verification/verify-ui.mjs`, headless Chromium against
the running dev server: **95 of 95 checks passing**, re-run in full just now.
Five of these were added after initial delivery specifically for collection
deletion and the create-failure regression — see "Post-delivery fixes." Full
output in `browser-verification-output.txt`. It exercises:

- routing: signed-out redirect, `returnUrl` carried through a deep link and
  honoured after signing in, the wildcard route rendering inside the layout,
  sign-out clearing `sessionStorage` and blocking re-entry
- forms: empty submit shows field errors and sends nothing, email and
  minimum-length rules, whitespace-only rejected the way the API rejects it, the
  80-character name cap, the textarea counter
- the four states on the quotes page: skeleton, list, empty (`No quotes yet` with
  a way forward), error (the API's own `ProblemDetails` detail, with a working
  retry) — plus "nothing on this page matches" as a state distinct from empty
- create and delete a quote end to end, including the total changing 18 → 19 → 18
- collections: empty state, create, open, the picker excluding quotes already in
  the collection (18 → 16), two adds, `added … ago` per row, the count badge
  recomputing, confirm-then-remove, and Escape cancelling without removing
- ownership: a quote owned by another user offers no delete control; a quote with
  no recorded creator is deletable but is *not* labelled "yours"
- the silent token refresh: exactly one `POST /api/auth/refresh` on an expired
  access token, the original request retried, no visible interruption
- the theme: the toggle setting `data-theme`, the page repainting to
  `rgb(11, 15, 15)`, and the choice surviving a reload
- accessibility behaviour: focus moving into the dialog, Escape closing it, the
  skip link as the first tab stop
- six viewport widths, as described above
- no unexpected console errors or unhandled exceptions across the whole run
  (deliberately provoked 400/401/404/500 responses are filtered by status; a
  thrown exception would still fail the run)

Two bugs were found by this run and fixed in the application: the brand link and
the nav link both had the accessible name "Quotes" (the brand now says "Quotes
home"), and the staggered card entrance covered only the first six cards, so on a
24-per-page view the later cards animated in *before* the earlier ones.

**API changes** — reviewed by diff, not run. See Known issues.

---

## Post-delivery fixes

Two things were fixed after initial delivery, in response to a real report
against the running application ("the New collection option doesn't work") and
a follow-up question ("is there a delete-collection endpoint?"). Both are stated
here rather than folded silently into the sections above, because a change made
after a submission was already reviewed should say so.

1. **`DELETE /api/collections/{id}` did not exist.** The API had `DELETE
   /api/collections/{id}/items/{quoteId}` (remove one quote from a collection)
   but nothing to delete a collection itself. Added in
   `Day7/piece2/QuotesApi/Extensions/CollectionEndpointExtensions.cs`, following
   the same two-check shape as `DELETE /api/quotes/{id}`: the `can-delete-
   collections` claim policy runs from the token before the handler executes,
   ownership (`collection.OwnerId == caller`) is checked after the collection is
   loaded, and it returns 403 rather than 404 for someone else's collection so
   the response doesn't confirm or deny that the id exists. Wired into the client
   as `CollectionsApi.remove()`, `CollectionsStore.remove()`, a Delete button on
   `CollectionCard`, and a `ConfirmDialog` on `CollectionsPage` — the same shape
   `QuotesPage` already used for deleting a quote.

   Adding the button surfaced a real layout bug before it ever reached a browser:
   `CollectionCard`'s name is a link whose `::after` is stretched with `inset: 0`
   over the *entire* card, so the click target extends past the visible text (see
   "Shared components" above on why `Card` has no `clickable` output). An
   absolutely positioned element stacks above a static sibling regardless of DOM
   order, so the new Delete button — added as a plain footer — would have sat
   underneath that stretched link, and clicking "Delete" would have silently
   navigated to the collection instead. Fixed by giving `.collection__footer` its
   own stacking context (`position: relative; z-index: 1`). The verification run
   below asserts the confirm dialog actually opens on that click, which is what
   would have failed if this had shipped unfixed.

2. **The actual "New collection doesn't work" bug**: `CollectionsStore.create()`
   sent any create failure that was not a field-validation error — a 401, a 403,
   a 500 — to `failure`, the same signal a failed *load* uses. `failure` drives
   `showError`, which replaces the whole list with a full-page error state. So
   the dialog closed as if the create had worked (`create()` still returns `{}`,
   its empty-fieldErrors case, which the page reads as success), and the entire
   collections list vanished behind a generic error the moment afterward — which
   reads exactly like the button did nothing. `QuotesStore` never had this
   problem because it already routes a non-validation create/delete failure to a
   separate `mutationFailure` signal; `CollectionsStore` had no such signal at
   all until this fix added one (`actionError` on the store, a dismissable
   banner above the list on `CollectionsPage`, matching `QuotesPage`'s existing
   one).

   `collections-store.spec.ts` (new — `CollectionsStore` had no unit tests
   before this) pins the fix directly: a test named for the bug forces a 401 on
   `create()` and asserts the list survives and `showError` stays false. The
   browser run adds the same check end to end, forcing a 500 through the stub's
   new `collectionCreateFails` mode and asserting the existing "Stoics worth
   rereading" card is still on screen with the failure shown as a dismissable
   banner above it, not a full-page error (`04b-collections-create-failure-
   preserves-list.png`).

A third thing was investigated and turned out not to be a bug: a screenshot from
the real API showed `GET /api/quotes?page=1&size=48` returning 401 with `WWW-
Authenticate: ... "The token expired at ..."` while the collection detail page
was open. That request is `CollectionDetailPage.ngOnInit()` eagerly warming the
"Add a quote" picker's candidate pool on page load, before the picker is even
opened — not a bug by itself, but it does mean an already-expired access token
gets discovered earlier than it otherwise would. Confirmed with the person who
saw it that the picker opened with quotes listed correctly afterward, meaning
the interceptor's silent refresh-and-retry (already covered by two of the 95
browser checks and by `auth-interceptor.spec.ts`) recovered exactly as designed.
Recorded here because it looked like a bug from the network tab alone and was
worth ruling out explicitly rather than assuming.

---

## Known issues

Stated because they are true, not because they were found late.

1. **No .NET SDK has been available in either environment used to write this**,
   so nothing here has been built or run from this side — every claim about the
   C# is from reading the diff, not executing it. That said, the person running
   this application on their own machine, with the real API and a real database,
   has since confirmed the original two changes work end to end: registering,
   signing in, listing and creating quotes, and creating and populating a
   collection all function against the live `Day7/piece2`, including the
   interceptor's silent token refresh recovering a genuinely expired access
   token (see "Post-delivery fixes"). What remains unverified against the real
   API specifically is the **newest** endpoint, `DELETE /api/collections/{id}`
   — added in this same round of fixes and, unlike the rest, not yet exercised
   by anyone against the running `Day7/piece2`. Run `dotnet build
   Day7/piece2/QuotesApi.slnx` if it has not already been built, and actually
   delete a collection through the running application once, before treating
   that endpoint as done.
2. **No tests were added for the new API endpoint.** `POST /api/auth/register`
   and the CORS policy have no integration test in
   `Quotes.Tests.Integration/AuthEndpointTests.cs`. Writing tests that have never
   been executed would have been worse than writing none — they would look like
   coverage. This is the first thing to add on a machine with the SDK.
3. **The UI has not been driven against the real API.** Every flow above was
   verified against a contract-faithful stub (`verification/README.md` explains
   what that is, why it exists, and the two ways it initially drifted). What is
   unverified end to end is precisely the wire: JSON casing from
   `System.Text.Json`, the real CORS middleware's behaviour on error responses,
   real JWT signing and expiry, and real EF Core error shapes.
4. **CI does not cover any of this.** `.github/workflows/ci.yml` builds and tests
   `Day5/piece2/QuotesApi.slnx` only — it has not tracked the current day since
   Day 7, so the patched API is not built by CI and `quotes-web` has no CI job at
   all. Left alone deliberately rather than changed in passing; it is its own
   piece of work.
5. **`Cors:AllowedOrigins` is configured for development only.** A deployed SPA
   needs its origin added wherever that environment's configuration lives, and the
   production `apiBaseUrl` set to match. One without the other is a browser-side
   failure that looks like an outage.
6. **The quotes filter cannot see past the current page**, because the API has no
   search parameter. The UI is explicit about this rather than hiding it, but it
   is a limitation a user will notice.
7. **iOS Safari can still rubber-band the page behind an open dialog.** It
   ignores `overflow: hidden` on `<body>`. The fix trades a cosmetic problem for a
   scroll-position bug, so it was left, commented, in `_base.scss`.
8. **The stub API is not a substitute for the real one** and should not become
   one. It exists to force three states a healthy API will not produce.
