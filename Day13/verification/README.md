# Verification harness

Two scripts. Neither is part of the application, and the application does not
know either exists.

| File | What it is |
|---|---|
| `stub-api.mjs` | A stand-in for `QuotesApi` on `http://localhost:5059` |
| `verify-ui.mjs` | Drives the running UI in headless Chromium and asserts what it does |

## Why a stub API exists at all

Not to develop against — `quotes-web` points at the real API and every screen was
built against its actual contracts. It exists for two reasons:

1. **Three states cannot be requested from a healthy API.** "The list failed with
   a 500", "the list came back empty", and "the access token just expired" are
   states the UI must handle correctly, and a working API will not produce any of
   them on demand. The stub exposes `POST /__verify/mode` so each one can be
   forced and the UI's response actually observed rather than assumed.
2. **The machine that ran this verification had no .NET SDK.** That is stated
   plainly in the submission report, along with what it means: the flows below
   were verified against a contract-faithful double, not against the real API.
   Running them against the real API is a step for someone with the SDK, and the
   report says so rather than implying it was done.

The stub mirrors the real contract deliberately and narrowly: the same routes,
the same status codes, the same `ProblemDetails` and `ValidationProblemDetails`
bodies, the same CORS policy, timestamps in the same
no-timezone-designator format, and the same aggregate invariants — author ≤ 200,
text ≤ 1000, collection name ≤ 80, at most 50 quotes per collection, no duplicate
quote in a collection, and delete permitted only for the quote's creator.

Where it drifts from the real API, the UI is being verified against the wrong
thing. Two drifts were found and fixed while writing it, both worth recording
because both produced a *passing-looking* UI that was doing the wrong thing:

- Error responses were sent **without CORS headers**. The real API's CORS
  middleware runs before the endpoints and stamps every response, error responses
  included. Without them the browser blocked a perfectly good 500 body, and the
  client correctly reported "could not reach the API" — so the test looking for
  the API's error message failed for a reason that had nothing to do with the app.
- Every seeded quote had `createdByUserId: null`, which the API's ownership rule
  treats as "no rule applies". With no third-party-owned quote in the data, the
  "someone else's quote offers no delete control" path could not be exercised at
  all.

## Running it

```bash
# terminal 1
node verification/stub-api.mjs          # listens on http://localhost:5059

# terminal 2
cd quotes-web && npm start              # http://localhost:4200

# terminal 3
npm i -D playwright                     # once
node verification/verify-ui.mjs
```

It exits non-zero if any check fails, prints one line per check, and writes
screenshots to `verification/screenshots/`. The last recorded run is in
`../docs/browser-verification-output.txt`.
