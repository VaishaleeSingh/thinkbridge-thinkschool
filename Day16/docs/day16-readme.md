# Day 16, explained simply

## What this task was asking for

Four things, in plain words:

1. **Lazy loading** — don't make the whole app download at once. Only download a page's code when someone actually visits that page.
2. **Auth guard** — if someone who isn't logged in tries to open a page meant for logged-in users, send them to the sign-in page instead.
3. **Route params** — a page that reads an id from the web address (like `/quotes/100004`) to know which specific thing to show.
4. **View Transition** — a smooth little animation when you move from the list of quotes to one quote's own page, instead of the page just snapping into place.

And the real point of the exercise: don't just have an AI build this and trust it. Check each piece actually works yourself.

## What we found before building anything

Three of the four things already existed in this app from earlier days. Reading the actual code confirmed it:

- Every page already only loads when visited (lazy loading — done).
- There was already a guard that sends a signed-out person to the sign-in page (auth guard — done).
- There was already a quote detail page that reads the quote's id from the URL (route params — done).

So the only real new work was #4, the View Transition — and there wasn't even a way to click from the quotes list into a quote's own page yet (clicking a quote only opened a small popup, not a full page).

## What we actually built

- Turned on the browser's View Transition feature in the app's router settings.
- Added a small round "expand" icon on every quote card. Clicking it opens that quote's own full page (instead of, or in addition to, the popup).
- Made the quote's picture animate smoothly from the card into the full page instead of just appearing.
- Made sure this animation turns itself off for people who've told their device to reduce motion (a real accessibility setting).

## How we checked it was really working (not just "the code looks right")

- **Lazy loading**: watched the browser's network activity. The quote-detail page's code only got downloaded the moment we clicked into a quote — not before.
- **Auth guard**: opened the app signed out, in a fresh browser tab. It really did redirect straight to the sign-in page.
- **Route param**: clicked a real quote card and watched the web address actually change to that quote's real id, with the real quote loading from the real backend.
- **View Transition**: this was the trickiest one to prove. Just because a browser *can* do view transitions doesn't mean our app was actually using the feature. So we set up a small check that counts how many times the browser's transition feature actually gets triggered — and confirmed it fires exactly once per real navigation, not zero times (silently doing nothing) and not many times (broken).

## The card icon

At first this had a text link ("Open full page"). Changed to a small icon instead so it matches the app's existing style, doesn't take extra room on the card, and still has a proper description for screen readers even though there's no visible text.

## What would break this

- If someone removed the new icon without adding another way to reach a quote's own page, the animation would have nothing to animate between.
- If the backend ever changed a quote's id to something that isn't plain digits, the existing id-checking code would reject it and the page would say "no such quote" for every real quote — that part was already built defensively, from before this task.
