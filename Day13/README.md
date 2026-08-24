# Day 13 — Quotes App (Angular + .NET API)

This folder is the Day 13 task. In simple words: build a working website
(front end) in Angular that talks to the real Quotes API (the back end from
Day 7), so a user can sign in, see quotes, add or delete quotes, and organise
them into collections.

## What the task was

1. Build the front end in **Angular 21**, using the newer style: standalone
   components, signals, and no Zone.js (called "zoneless").
2. Connect it to the **real API** — no fake/sample data. Every screen (sign
   in, quotes list, collections) actually calls the API in `Day7/piece2`.
3. Cover the main features:
   - Sign in / register (the API had no way to create a user before, so that
     was added too).
   - View, add, and delete quotes.
   - Create collections, add quotes to a collection, remove them, and delete
     a whole collection.
   - Handle errors properly (e.g. a wrong login, an expired session, a form
     with bad input) instead of the screen breaking or going blank.
4. Prove it works — with screenshots and test runs, not just claims.

## What is inside this folder

| Folder | What it holds |
|---|---|
| `quotes-web/` | The actual Angular application (the code) |
| `verification/` | Scripts and screenshots that prove the app works |
| `docs/` | The write-up: what was tested, how, and what is still pending |

## How to run it

You need the API running first — the app has no data of its own.

```bash
# Terminal 1 — start the API
cd Day7/piece2
dotnet user-secrets --project QuotesApi set "Jwt:Secret" "<at least 32 characters>"
dotnet run --project QuotesApi        # runs at http://localhost:5059

# Terminal 2 — start the Angular app
cd Day13/quotes-web
npm install
npm start                             # runs at http://localhost:4200
```

Then open `http://localhost:4200` in a browser and create an account (there
is no existing login — you make a new one the first time).

## Things that were fixed during review

- **"New collection" button was not working.** The code was silently
  swallowing the error instead of showing it, so nothing seemed to happen.
  This is fixed, and a test was added so it cannot silently break again.
- **No way to delete a collection.** This API endpoint did not exist. It was
  added, following the same security rules already used for deleting a
  quote (only the owner can delete their own collection).
- **Delete button was not clickable in some cases.** A styling issue caused
  an invisible layer to sit on top of the button. Fixed.

## How to check the proof

- `verification/screenshots/` has pictures of the app actually working:
  creating a quote, deleting a quote, doing the same for a collection, and
  the real API response shown alongside some of them as evidence.
- `verification/verify-ui.mjs` is an automated script that opens the app in
  a browser and checks that everything behaves correctly. The last run
  passed all checks — see `docs/browser-verification-output.txt`.
- `docs/day13-angular-signals-zoneless-submission.md` is the full report,
  including anything that was **not** tested (so nothing is claimed that
  was not actually checked).

## Known limitation

The newest change (deleting a collection) was tested against a stand-in
version of the API, not the real one, because the machine used for testing
did not have the .NET SDK installed. This is written clearly in the report
so it is not mistaken for something fully verified end to end.
