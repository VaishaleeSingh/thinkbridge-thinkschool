# Day 16 — State management, signals first: verification

## Where I started

This app already handles all its important state with signals — no NgRx anywhere, not even a little. The quotes list, a single quote's page, a collection's page, login — all of it. So "use signals" wasn't really the new part of this task for me. The real ask was: find one small thing that's actually missing, and give it just enough state to work — not a full copy of everything the bigger features already have.

I found the thing that was missing: I could add a quote to a collection, but only from inside that one collection's own page. There was no way to do it from the quotes list itself — even though the button for it (the real API) already existed and was already working elsewhere. I wrote this up as a brief for the agent: `Day16-signals/docs/day16-signals-agent-brief.md`.

## What I got built

- A small helper called `CollectionPicker` that just remembers three things: my list of collections, which card's menu is currently open, and whether an "add" is in progress. That's it — no re-fetching everything after every change, the way the bigger `CollectionDetailStore` does. When an add succeeds, it just bumps that one collection's count by one, right there, instead of asking the server again.
- One shared copy of this helper for the whole quotes page — not a fresh one per card — so only one card's menu is ever open, and the collections list only gets fetched once no matter how many cards I click through.
- A small "+" icon on every quote card that opens that menu, lets me pick a collection, and shows the real error message if something goes wrong.
- 7 automated tests covering it.

## Two bugs I found, and what was actually wrong

**Bug 1 — I noticed the popup menu was getting cut off.** I flagged this twice, the second time with a screenshot showing the text wrapping and getting sliced off mid-word.

The first fix the agent tried didn't actually work — it said the fix was done, but when I pushed back and it double-checked with a real screenshot, the text was still cut off. It had reached for the wrong browser feature. The fix that actually worked: a different HTML feature built specifically so a small popup can float above literally everything else on the page, so it physically can't get trapped inside the card anymore. That fix then caused a second, sneakier issue — the menu started popping up in a completely wrong spot, way off-screen — because a floating element like this positions itself relative to the whole browser window, not the card it came from. It had to explicitly tell the menu "sit right above this exact button" using the button's real on-screen position.

**Bug 2 — I noticed the colours looked wrong in both light and dark mode.** What had happened: the popup had one fixed dark colour baked in no matter what theme I was using. It happened to blend in okay on dark mode purely by luck, but on light mode it was flat-out the wrong colour for its surroundings. I flagged this and had it fixed to pull its colours from the same shared colour system the rest of this app already uses — so it now automatically matches whichever theme I'm in, the same as the cards and the "add quote" popup around it.

![Popup in light theme — white background, dark readable text, matches the rest of the app](screenshots/day16-signals-menu-light-theme.png)

![Popup in dark theme — proper dark surface that matches the app's own dark chrome, not the old mismatched navy](screenshots/day16-signals-menu-dark-theme.png)

## How I actually checked this — not just "the code looks right"

**1. I confirmed the clipping is really fixed, with a real click** (not something triggered through code):

![Menu opens fully visible, nothing cut off](screenshots/day16-signals-collect-menu-unclipped.png)

**2. I made a real collection through the real page** — started empty, at 0 of 50 quotes.

**3. I added a real quote through the new "+" button.** The count updated to 1/50 instantly, without asking the server again for the whole list (I checked this by watching the actual network calls — zero extra ones fired). Then I opened that collection's own real page and saw the quote was genuinely sitting there — not just that the button flashed a checkmark:

![The quote really is in the collection, confirmed on its own page](screenshots/day16-signals-quote-in-collection-detail.png)

**4. I tried adding the exact same quote to the exact same collection a second time, on purpose.** I got a real error back — *"This quote is already in the collection."* I double-checked this wasn't something typed into the app's own code by searching for that exact sentence across the whole source — it only shows up in a test file's fake data, meaning what I actually saw on screen came straight from the real backend, not a made-up message:

![The real server error, not a generic "something went wrong"](screenshots/day16-signals-duplicate-add-400.png)

**5. I opened one card's menu, then a different card's menu right after.** The first one closed on its own, the second one opened, and — again I checked this via the real network calls — no extra request was made to re-fetch the collections list. It just reused what it already had:

![Opening a second card's menu closes the first one and reuses the same data](screenshots/day16-signals-second-card-only-one-open.png)

## The one call I'm making myself, not the AI

I'd keep this as a small signals-based helper, not turn it into a full store, for now. Here's roughly when I'd change my mind:

- **If more than one page needed the same collections list.** Right now only my quotes page uses this. If a second page also needed to show and update the same list and keep both in sync, a helper that only exists on one page stops being enough — that's when I'd move it somewhere more central.
- **If this state needed to survive moving between pages.** Right now, leaving the quotes page and coming back just starts fresh, on purpose — a quick re-fetch is cheap and not a problem for me. If losing that state on navigation ever became an actual bug (like in a multi-step form), that's my signal that it needs a longer-lived home.
- **If the update logic itself got complicated enough to deserve its own tests, separate from the page.** Right now "bump the count by one" is a couple of lines. If it grew into something like undo, or handling conflicting updates from two open tabs, that complexity would tell me it's outgrown being "just this page's helper."

None of that is true yet here, so I'm keeping it small and simple on purpose — reaching for something heavier would just be extra machinery this feature doesn't actually need.

## What "done" means for me on this task

- I only used real endpoints (`GET /api/collections`, `POST /api/collections/{id}/items`) — nothing made up.
- I didn't touch or remove anything existing — the existing popup preview, the "open full page" button, and the delete button all still work exactly as before.
- The state only holds exactly what this feature needs, nothing extra sitting around unused.
- I checked everything above against the real, running app and the real backend — not just the automated tests.
