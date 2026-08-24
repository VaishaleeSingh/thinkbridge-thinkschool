# quotes-web

The Angular 21 front end for this repository's `QuotesApi` — signals-first,
standalone, zoneless. Day 13.

It is a real client of the real API: every screen reads and writes through
`/api/auth`, `/api/quotes` and `/api/collections`, and there is no mock data
anywhere in `src/`.

## Running it

The API has to be running first, because this application has no data of its own.

```bash
# terminal 1 -- the API, from the repository root
cd Day7/piece2
dotnet user-secrets --project QuotesApi set "Jwt:Secret" "<at least 32 characters>"
dotnet run --project QuotesApi        # http://localhost:5059

# terminal 2 -- this application
cd Day13/quotes-web
npm install
npm start                             # http://localhost:4200
```

Then open http://localhost:4200 and create an account. There is no seeded user:
`POST /api/auth/register` was added to the API on Day 13 precisely because there
was previously no way to make one.

### The two ends of the wire

| Setting | Where | Value |
|---|---|---|
| API address | `src/environments/environment.ts` | `http://localhost:5059` |
| Allowed origin | `Day7/piece2/QuotesApi/appsettings.Development.json` | `http://localhost:4200` |

Both halves have to agree. If the API is running and the browser still reports
that it cannot be reached, the origin above is the first thing to check — a
mismatch shows up as a CORS failure, which the browser reports as a request that
never happened rather than as a rejection.

The development build talks to the API cross-origin on purpose rather than
proxying through the dev server, so the API's own CORS policy is exercised on
every local run instead of first being tested in a deployed environment. The
production build uses an empty base URL — same-origin — see
`src/environments/environment.production.ts`.

## Commands

```bash
npm start          # dev server, http://localhost:4200
npm run build      # production build into dist/
npm test           # unit tests (vitest)
npm run lint       # eslint, including the template accessibility rules
```

## How it is put together

```text
src/
├── app/
│   ├── core/            # things with no UI: models, API services, stores, guards, the interceptor
│   ├── shared/          # 13 reusable components, plus form helpers, a pipe and an id helper
│   ├── features/        # auth, quotes, collections, not-found -- each with pages/, components/, services/
│   ├── layouts/         # main-layout (signed in) and auth-layout (signed out)
│   ├── app.routes.ts    # every route, lazily loaded, with per-route store providers
│   └── app.config.ts    # every provider the application has
├── environments/        # the only files that know where the API lives
└── styles/              # design tokens, base, field, animations -- no component styles
```

Three rules the codebase follows, and the reason for each:

- **State lives in a store, derived state is a `computed()`.** Feature stores
  (`QuotesStore`, `CollectionsStore`, `CollectionDetailStore`) own the signals;
  pages compose components and hold only what belongs to the screen, such as
  which dialog is open. Nothing recalculates a value that a `computed()` could
  derive, and `effect()` is used only for genuine side effects — browser storage,
  the `data-theme` attribute, opening a `<dialog>`, and pushing server-side
  validation onto form controls.
- **Colours, spacing, radii and durations come from `styles/_tokens.scss`.** No
  component contains a hex value. That is also what makes the dark theme ~20
  redefined tokens rather than a second stylesheet.
- **Shared components take appearance, not data.** `Card` knows nothing about
  quotes; `Button` knows nothing about the API. A shared component that imported
  a feature model would stop being shareable the moment a second feature needed
  it.

## Verification

`../docs/day13-angular-signals-zoneless-submission.md` is the report: what was
tested, how, and — explicitly — what was not.
