# Day 14, Task 2 — Submission

## Exercise

THE BRIEF

I asked the agent to rebuild the same "New quote" form (same 3 fields as
the real form: author, quote text, and a background picture) using
Angular's new experimental Signal Forms API instead of the older reactive
forms style. Real API used: POST /api/quotes from the Week-1 Quotes API —
author (required, no spaces-only, max 200 chars), text (same rules, max
1000 chars), backgroundImageUrl (always one of 6 preset images in this
app). I told it not to touch the real form at all — build this as a
separate, throwaway demo page just for comparison.

THE AGENT'S OUTPUT

It built a new page at /quotes/signal-forms-demo with the same 3 fields,
same validation rules, same "move focus to the first wrong field" behavior
as the real form. It also added a fake "saving..." delay and a fake
"server rejected this" case so both forms could be tested the same way.

VERIFICATION — STATES I TESTED

- Pristine (nothing typed yet) — no errors shown
- Empty submit — both required errors show, focus jumps to Author
- Spaces-only name — correctly rejected (not accepted as "filled in")
- Text over 1000 characters — correctly rejected, focus moves to the text
  field, not back to Author
- Submitting — Save button disables and shows "Saving..."
- Server says no (simulated) — real error text shows, focus moves to the
  rejected field
- Clean, successful submit — form saves and shows the result

I ran this both as automated browser tests (19/19 passed) and by hand,
with screenshots, logged into my own account.

ONE BUG I CAUGHT AND MADE IT FIX

The new demo page put its content inside its own `<main>` tag, but it's
already shown inside the app's layout which has its own `<main>` tag too —
so the page had two "main" landmarks nested inside each other. This is a
real accessibility problem (a screen reader shouldn't see two "main"
areas). I caught this by running an accessibility checker (axe) against
the live page, confirmed no other page in the app does this, and had the
agent fix it by changing that wrapper to a `<section>` instead. Re-tested
after — problem gone, nothing else broke.

SIGNAL FORMS vs REACTIVE FORMS — QUICK COMPARISON

Simpler: less code to define the form itself (one function vs building a
typed FormGroup by hand); it automatically marks all fields as "touched" on
submit, and moving focus to an invalid field needs no extra wiring.
Not simpler: it does NOT give you screen-reader-friendly markup
(aria-invalid, aria-describedby) for free — you still have to write that by
hand, exactly like the old way. Also, connecting a "the server rejected
this field" error back to the right field still has to be written by hand
either way.

WHAT BREAKS IF THE API CHANGES

If the API adds a new required field, both forms need a matching line
added by hand — neither one guesses new fields on its own. If the 200/1000
character limits change, only one shared constant needs updating and both
forms follow it automatically.

## GitHub link

https://github.com/thinkbridge-thinkschool/VaishaleeSingh/pull/39

## Notes for mentor (optional)

This demo is separate from the real form on purpose — it's not linked in
the app's menu, you reach it by typing `/quotes/signal-forms-demo` in the
URL. Also fixed in this PR: a few images (logo, background photos) were
missing from the app the whole time — added those too.

## What did you learn this session? (optional)

That a new, "simpler" way of doing something doesn't automatically mean
it's better everywhere — Signal Forms cut down on form-setup code, but
screen-reader support still had to be written by hand, same as before. The
new API just doesn't save you that part.

## What would break this? (optional)

If the API added a brand new field, this form wouldn't automatically pick
it up — someone would have to notice and add it by hand, on both the old
and new form.
