# Verification scripts

This folder holds three things:

| File | What it does |
|---|---|
| `stub-api.mjs` | A stand-in for the real Quotes API, used only for testing |
| `verify-ui.mjs` | Opens the app in a real (headless) browser and checks the whole thing — sign in, quotes, collections, paging, etc. |
| `verify-quote-form.mjs` | Same idea, but focused on the "New quote" form — keyboard use, screen-reader wiring, and what happens when it fails |
| `verify-signal-form-demo.mjs` | Same idea again, but for the experimental Signal Forms version of that same form (see below) |
| `screenshots/` | Pictures the scripts took while running, as proof |

Neither script is part of the actual app. The app never imports them and
doesn't know they exist — they exist only to test the app from the outside,
the same way a person clicking around would.

## Why there's a fake API (`stub-api.mjs`)

The real app talks to the real API the whole time it's being built. The stub
only gets used for the automated test scripts, and only for two reasons:

1. **Some situations can't be triggered on demand.** Things like "the server
   is down", "the list came back empty", or "your login expired" are cases
   the app has to handle well, but you can't just ask a healthy, working API
   to fail on command. The stub can be told to fake these on purpose
   (`POST /__verify/mode`), so the test can check the app actually handles
   them instead of just hoping it does.
2. **No .NET installed on the machine that first wrote these tests.** That's
   written down plainly in the submission notes — the tests below were run
   against a copy that behaves the same as the real API, not the real API
   itself. Running them against the real one too is a good next step for
   anyone who has .NET installed.

The stub is built to behave exactly like the real API: same URLs, same error
formats, same limits (author ≤ 200 characters, quote text ≤ 1000, and so on).
If it ever behaves differently from the real thing, the tests are checking
the wrong thing — so any difference found while building it got written down
and fixed. Two were:

- Error responses from the stub were missing a header the browser needs
  (CORS) to even show them — so a real failure looked like "can't reach the
  server" instead of the actual error.
- Every test quote had no owner recorded, which the API treats as "anyone
  can delete this" — so there was no way to test "you can't delete someone
  else's quote" until a properly-owned quote was added to the test data.

## What's the Signal Forms demo, in plain words

The "New quote" form in the real app is built with **reactive forms**,
Angular's long-standing way of building forms. Angular is now trying out a
new, experimental way to build forms called **Signal Forms** — it isn't
finished or officially recommended yet, but it's worth trying so we know
what it's actually like, not just what the docs claim.

So there's a second, throwaway copy of the exact same "New quote" form
(same 3 fields, same rules — an author, the quote text, a background
picture) built with Signal Forms instead. It lives at its own page,
`/quotes/signal-forms-demo`, and doesn't touch or replace the real form in
any way — it's there purely so the two can be compared side by side. It's
not linked from anywhere in the app's normal navigation; you have to type
the URL in directly.

`verify-signal-form-demo.mjs` checks this second form the same way
`verify-quote-form.mjs` checks the real one: empty submit, a whitespace-only
name, text that's too long, what happens while it's saving, and what
happens if the server says no. `docs/day14-task2-comparison.md` (one level
up) writes out, in plain terms, where the new way was actually simpler and
where it was actually more work — based on what happened while building it,
not a guess.

## How to run it

Open three terminals:

```bash
# Terminal 1 — the fake API
node stub-api.mjs                # runs on http://localhost:5059

# Terminal 2 — the real app
cd ../quotes-web
npm start                        # runs on http://localhost:4200

# Terminal 3 — one of these, whichever you want to run
npm install                      # only needed the first time (installs Playwright etc.)
npx playwright install chromium  # also only needed the first time
node verify-ui.mjs               # checks the whole app
node verify-quote-form.mjs       # checks just the "New quote" form
node verify-signal-form-demo.mjs # checks the experimental Signal Forms copy of that form
```

Each line printed is one check: `PASS` or `FAIL`, plus a short note on what
was found. If anything fails, the script exits with an error so it can be
caught automatically (e.g. in CI) instead of someone having to read every
line by hand. Screenshots taken along the way land in `screenshots/`. The
last full run's output is saved in `../docs/browser-verification-output.txt`.
