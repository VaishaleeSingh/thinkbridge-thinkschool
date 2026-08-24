/**
 * Drives the built Angular application in a real browser and checks what it
 * actually does.
 *
 * Everything asserted here is asserted against the running UI: the four view
 * states, the signal transitions behind them, the forms, the dialogs, paging,
 * the silent token refresh, the theme switch, and six viewport widths. Nothing is
 * inferred from the source.
 *
 * Run against the stub API (see stub-api.mjs) because the error, empty and
 * expired-token paths cannot be produced on demand by a healthy API. Everything
 * else it does is contract-identical to the real one.
 *
 * Usage: node verify-ui.mjs   (with stub-api.mjs and `ng serve` already running)
 */
import { mkdirSync } from 'node:fs';
import { chromium } from 'playwright';

const APP = 'http://localhost:4200';
const API = 'http://localhost:5059';
const SHOTS = new URL('./screenshots/', import.meta.url).pathname;

mkdirSync(SHOTS, { recursive: true });

const results = [];
let failures = 0;

function check(name, condition, detail = '') {
  const passed = Boolean(condition);
  if (!passed) failures += 1;
  results.push({ name, passed, detail });
  console.log(`${passed ? 'PASS' : 'FAIL'}  ${name}${detail ? `  -- ${detail}` : ''}`);
}

async function setMode(mode) {
  await fetch(`${API}/__verify/mode`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(mode),
  });
}

async function resetStub() {
  await fetch(`${API}/__verify/reset`, { method: 'POST' });
}

/**
 * Fails the run if the page overflows horizontally -- the responsive killer.
 *
 * Two measurements, because the document-level one alone is weak: it reports the
 * scrollable width, and any ancestor with `overflow-x: hidden` makes that equal
 * the viewport no matter how wide the content really is. So it also walks the
 * rendered boxes and names the widest offender, which cannot be masked and tells
 * you which element to fix.
 */
async function assertNoHorizontalOverflow(page, label) {
  const overflow = await page.evaluate(() => {
    const root = document.documentElement;
    const viewport = root.clientWidth;

    let worst = null;

    for (const element of document.body.querySelectorAll('*')) {
      const box = element.getBoundingClientRect();

      // Zero-size nodes and anything intentionally off-screen (the skip link,
      // visually-hidden text) are not layout overflow.
      if (box.width === 0 || box.height === 0 || box.left < -1) {
        continue;
      }

      const spill = box.right - viewport;

      if (spill > 1 && (!worst || spill > worst.spill)) {
        worst = {
          spill: Math.round(spill),
          selector: `${element.tagName.toLowerCase()}.${String(element.className).split(' ')[0]}`,
        };
      }
    }

    return { scrollWidth: root.scrollWidth, viewport, worst };
  });

  check(
    `no horizontal overflow: ${label}`,
    overflow.scrollWidth <= overflow.viewport + 1 && overflow.worst === null,
    overflow.worst
      ? `${overflow.worst.selector} overflows by ${overflow.worst.spill}px`
      : `scrollWidth ${overflow.scrollWidth} vs viewport ${overflow.viewport}`,
  );
}

/** Whether the page behind a dialog can still be scrolled. */
async function isScrollLocked(page) {
  return page.evaluate(() => document.body.classList.contains('has-modal'));
}

const browser = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium' });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

const consoleErrors = [];
page.on('console', (message) => {
  if (message.type() === 'error') consoleErrors.push(message.text());
});
page.on('pageerror', (error) => consoleErrors.push(`pageerror: ${error.message}`));

const requests = [];
page.on('request', (request) => requests.push(`${request.method()} ${request.url()}`));

/**
 * Every /api/ response, with its body -- so a screenshot can be paired with
 * the actual request/response that produced the state on screen, not just a
 * claim that one happened. Kept small (status + method + path + a size-capped
 * body) rather than the full Playwright Response object, which is not
 * serializable into the page for the overlay below.
 */
const apiResponses = [];
page.on('response', async (response) => {
  const url = response.url();
  if (!url.includes('/api/')) return;

  let bodyText = '';
  try {
    bodyText = await response.text();
  } catch {
    bodyText = '';
  }

  apiResponses.push({
    method: response.request().method(),
    path: url.replace(API, ''),
    status: response.status(),
    body: bodyText.length > 400 ? `${bodyText.slice(0, 400)}…` : bodyText,
  });
});

/** The most recent captured response whose method+path match, or null. */
function lastApiResponse(method, pathPattern) {
  for (let i = apiResponses.length - 1; i >= 0; i -= 1) {
    const entry = apiResponses[i];
    if (entry.method === method && pathPattern.test(entry.path)) return entry;
  }
  return null;
}

/**
 * Screenshots the current page with a real captured API exchange rendered as a
 * visible panel over it -- so the image is evidence of what the network
 * actually did, not just what the UI happened to show afterward. The panel is
 * added and removed via page.evaluate rather than left in the DOM, so it
 * cannot be mistaken by any later selector for real application markup.
 */
async function shotWithApiEvidence(path, response, caption) {
  const label = response
    ? `${response.method} ${response.path} → ${response.status}\n${response.body || '(empty body)'}`
    : 'No matching API call was captured for this step.';

  await page.evaluate(
    ({ label, caption }) => {
      const panel = document.createElement('div');
      panel.id = '__verify-api-panel';
      panel.style.cssText =
        'position:fixed;left:0;right:0;bottom:0;z-index:2147483647;' +
        'background:#0b0f0f;color:#3ccfc9;font:12px/1.5 ui-monospace,monospace;' +
        'padding:12px 16px;max-height:34vh;overflow:auto;white-space:pre-wrap;' +
        'border-top:2px solid #3ccfc9;box-shadow:0 -4px 16px rgba(0,0,0,0.4);';
      const title = document.createElement('div');
      title.textContent = caption;
      title.style.cssText = 'color:#edf2f1;font-weight:700;margin-bottom:6px;';
      panel.appendChild(title);
      const body = document.createElement('div');
      body.textContent = label;
      panel.appendChild(body);
      document.body.appendChild(panel);
    },
    { label, caption },
  );

  await page.screenshot({ path: `${SHOTS}${path}`, fullPage: true });

  await page.evaluate(() => document.getElementById('__verify-api-panel')?.remove());
}

await resetStub();

// ---------------------------------------------------------------------------
// 1. Unauthenticated routing
// ---------------------------------------------------------------------------
await page.goto(APP);
await page.waitForURL(/\/sign-in/);
check('signed-out visit to / redirects to /sign-in', page.url().includes('/sign-in'));

await page.goto(`${APP}/collections`);
await page.waitForURL(/\/sign-in\?returnUrl=/);
check(
  'deep link while signed out carries returnUrl',
  page.url().includes('returnUrl=%2Fcollections'),
  page.url(),
);

// ---------------------------------------------------------------------------
// 2. Form validation before any request is made
// ---------------------------------------------------------------------------
const requestsBefore = requests.length;
await page.getByRole('button', { name: 'Sign in', exact: true }).click();
await page.waitForTimeout(150);

check(
  'submitting an empty form shows field errors',
  (await page.getByText('Email is required.').count()) === 1 &&
    (await page.getByText('Password is required.').count()) === 1,
);
check(
  'an invalid form sends no request',
  requests.slice(requestsBefore).filter((entry) => entry.includes('/api/auth')).length === 0,
);

await page.getByLabel('Email').fill('not-an-email');
await page.getByLabel('Password').fill('short');
await page.getByRole('button', { name: 'Sign in', exact: true }).click();
await page.waitForTimeout(150);
check(
  'client-side email and length rules report before submitting',
  (await page.getByText('Enter a valid email address.').count()) === 1 &&
    (await page.getByText('Password must be at least 8 characters.').count()) === 1,
);

// ---------------------------------------------------------------------------
// 3. A wrong password: the API's bare 401, rendered as a sentence
// ---------------------------------------------------------------------------
await page.getByLabel('Email').fill('dev@example.com');
await page.getByLabel('Password').fill('correct-horse-battery');
await page.getByRole('button', { name: 'Sign in', exact: true }).click();
await page.waitForTimeout(400);

check(
  'a 401 from /login is shown as a session message, not as an HTTP string',
  (await page.getByRole('alert').filter({ hasText: 'session has expired' }).count()) === 1,
);

// ---------------------------------------------------------------------------
// 4. Register, and land where the deep link pointed
// ---------------------------------------------------------------------------
await page.getByRole('button', { name: 'Create one' }).click();
await page.getByLabel('Email').fill('dev@example.com');
await page.getByLabel('Password').fill('correct-horse-battery');
await page.getByRole('button', { name: 'Create account' }).click();

await page.waitForURL(/\/collections/, { timeout: 5000 });
check('registering signs in and honours returnUrl', page.url().endsWith('/collections'));

// ---------------------------------------------------------------------------
// 5. Collections: the empty state, then create
// ---------------------------------------------------------------------------
await page.waitForSelector('app-empty-state');
check(
  'an empty API response renders the empty state, not blank space',
  (await page.getByRole('heading', { name: 'No collections yet' }).count()) === 1,
);

await page.getByRole('button', { name: 'New collection' }).first().click();
await page.waitForSelector('dialog[open]');
check('the create dialog opens as a modal <dialog>', await page.locator('dialog[open]').isVisible());

// Focus is inside the dialog, which is what showModal() buys.
const focusInsideDialog = await page.evaluate(() =>
  Boolean(document.activeElement?.closest('dialog')),
);
check('focus moves into the dialog on open', focusInsideDialog);

// The API caps a collection name at 80 characters, and the field carries that
// as a maxlength -- so an over-long name cannot be typed at all rather than
// being typed and then rejected. Asserting the cap, not a message: there is no
// message because there is no invalid state to report.
await page.getByLabel('Name').fill('x'.repeat(81));
const cappedName = await page.getByLabel('Name').inputValue();
check(
  "the name field caps input at the API's 80-character limit",
  cappedName.length === 80,
  `${cappedName.length} characters accepted`,
);

await page.getByLabel('Name').fill('');
await page.getByRole('button', { name: 'Create collection' }).click();
await page.waitForTimeout(200);
check(
  'an empty name is reported on the field, and nothing is sent',
  (await page.getByText('Name is required.').count()) === 1 &&
    (await page.locator('dialog[open]').count()) === 1,
);

await page.getByLabel('Name').fill('Stoics worth rereading');
await page.screenshot({ path: `${SHOTS}collections-create-01-form-filled.png`, fullPage: true });

await page.getByRole('button', { name: 'Create collection' }).click();
await page.waitForSelector('app-collection-card', { timeout: 5000 });
check(
  'creating a collection closes the dialog and shows the new card',
  (await page.locator('dialog[open]').count()) === 0 &&
    (await page.getByRole('link', { name: 'Stoics worth rereading' }).count()) === 1,
);
check(
  'the header subtitle recomputes from the loaded list, pluralised correctly',
  (await page.getByText('1 collection, 0 quotes in total.').count()) === 1,
);
await shotWithApiEvidence(
  'collections-create-02-success-with-api-response.png',
  lastApiResponse('POST', /^\/api\/collections$/),
  'Create collection — actual request/response captured from the network',
);

// The regression this exists for: CollectionsStore.create()'s catch-all used
// to set `failure` (the load-error signal) instead of `mutationFailure` for
// anything that was not a field or bare-400 validation error -- so a 401/403/
// 500 on create closed the dialog as if it had worked and then replaced the
// WHOLE list with a full-page error. That is what "New collection doesn't
// work" actually was. Forcing exactly that response here and asserting the
// list survives is what pins the fix.
await setMode({ collectionCreateFails: true });
await page.getByRole('button', { name: 'New collection' }).first().click();
await page.waitForSelector('dialog[open]');
await page.getByLabel('Name').fill('Should not blank the list');
await page.getByRole('button', { name: 'Create collection' }).click();
await page.waitForTimeout(400);
check(
  'a non-validation create failure does not blank the list behind a full-page error',
  (await page.getByRole('link', { name: 'Stoics worth rereading' }).count()) === 1 &&
    (await page.getByRole('heading', { name: 'Could not load collections' }).count()) === 0,
);
check(
  'the failure surfaces as a dismissable action error instead, above the still-visible list',
  (await page.getByRole('heading', { name: 'That action did not work' }).count()) === 1,
);
await shotWithApiEvidence(
  'collections-create-03-bug-fix-failed-create-does-not-blank-list.png',
  lastApiResponse('POST', /^\/api\/collections$/),
  'Bug fix evidence — a forced 500 on create no longer replaces the list with a full-page error',
);

// ---------------------------------------------------------------------------
// 6. Collection detail: add and remove quotes
// ---------------------------------------------------------------------------
await page.getByRole('link', { name: 'Stoics worth rereading' }).click();
await page.waitForURL(/\/collections\/\d+/);
await page.waitForSelector('app-empty-state');
check(
  'an empty collection shows its own empty state',
  (await page.getByRole('heading', { name: 'Nothing in this collection yet' }).count()) === 1,
);

// The picker is checkbox-based (select several, add as one batch), not a
// per-row "Add" button -- the .picker__item is a <label> wrapping a checkbox
// and the quote text, so the whole row toggles the box rather than firing an
// immediate add. See add-quote-dialog.html/ts for why: a label's own
// activation behaviour is what makes the row clickable and keyboard-operable
// with no click handler of this component's own.
await page.getByRole('button', { name: 'Add a quote' }).first().click();
await page.waitForSelector('dialog[open]');
const candidateCount = await page.locator('.picker__item').count();
check('the picker lists candidate quotes', candidateCount > 0, `${candidateCount} candidates`);

// A short gap between the two clicks, not a Playwright nicety: with zero delay
// between them, Playwright's second synthetic click can land before the first
// click's zoneless OnPush re-render has painted, and the DOM checkbox ends up
// checked (a real browser side effect of the click, independent of Angular)
// while the component's own signal-driven button text still reports the count
// from before it. No human clicks two different rows with zero elapsed time
// between them, so this is a timing artifact of the test, not the product --
// confirmed by reproducing it in isolation and finding a 30ms gap already
// enough to make the count correct every time.
await page.locator('.picker__item').nth(0).click();
await page.waitForTimeout(200);
await page.locator('.picker__item').nth(1).click();
await page.waitForTimeout(200);
check(
  'the batch button counts exactly the checked rows',
  (await page.getByRole('button', { name: 'Add selected (2)' }).count()) === 1,
);

await page.getByRole('button', { name: /^Add selected/ }).click();
await page.waitForTimeout(600);

const remainingCandidates = await page.locator('.picker__item').count();
check(
  'quotes already in the collection drop out of the picker',
  remainingCandidates === candidateCount - 2,
  `${candidateCount} -> ${remainingCandidates}`,
);

await page.getByRole('button', { name: 'Done' }).click();
await page.waitForTimeout(300);
await page.waitForSelector('app-collection-quote-row');
check(
  'the collection shows the added quotes',
  (await page.locator('app-collection-quote-row').count()) === 2,
);
check(
  'each row shows when it was added to THIS collection',
  (await page.getByText(/added .*ago|added now/).count()) === 2,
);
check(
  'the count badge recomputed',
  (await page.getByText('2 of 50 quotes').count()) === 1,
);
await shotWithApiEvidence(
  'collections-add-quote-with-api-response.png',
  lastApiResponse('POST', /^\/api\/collections\/\d+\/items$/),
  'Add a quote to a collection — actual request/response captured from the network',
);

await page.locator('app-collection-quote-row').first().getByRole('button', { name: /^Remove/ }).click();
await page.waitForSelector('dialog[open]');
check(
  'removing asks first',
  (await page.getByRole('heading', { name: 'Remove from this collection?' }).count()) === 1,
);

// Escape closes it, natively.
check('an open dialog locks the page behind it', await isScrollLocked(page));

await page.keyboard.press('Escape');
await page.waitForTimeout(250);
check('Escape closes a dialog', (await page.locator('dialog[open]').count()) === 0);
check(
  'cancelling removed nothing',
  (await page.locator('app-collection-quote-row').count()) === 2,
);

// The regression this exists for: ConfirmDialog is rendered inside an @if with
// [open]="true", so cancelling DESTROYS the modal rather than closing it. Effects
// do not run on destroy, so the scroll lock stayed on <body> and the page behind
// could not be scrolled again until a reload -- after every confirmation in the
// application.
check(
  'the scroll lock is released when a dialog is destroyed while open',
  (await isScrollLocked(page)) === false,
);

// Focus return: the opener is still on the page this time, so focus must come
// back to it rather than being dropped at the top of the document.
await page.getByRole('button', { name: 'Add a quote' }).first().click();
await page.waitForSelector('dialog[open]');
await page.keyboard.press('Escape');
await page.waitForTimeout(300);

const focusedAfterClose = await page.evaluate(
  () => document.activeElement?.getAttribute('aria-label') ?? document.activeElement?.textContent?.trim() ?? '',
);
check(
  'focus returns to the control that opened the dialog',
  focusedAfterClose.includes('Add a quote'),
  `focus landed on "${focusedAfterClose}"`,
);

await page.locator('app-collection-quote-row').first().getByRole('button', { name: /^Remove/ }).click();
await page.getByRole('button', { name: 'Remove', exact: true }).click();
await page.waitForTimeout(700);
check(
  'confirming removes exactly one quote',
  (await page.locator('app-collection-quote-row').count()) === 1,
);
check(
  'the scroll lock is released after confirming, too',
  (await isScrollLocked(page)) === false,
);

// ---------------------------------------------------------------------------
// 6b. Deleting a whole collection (new: DELETE /api/collections/{id})
//
// A SECOND, disposable collection is created and deleted here rather than
// deleting "Stoics worth rereading" -- section 10b below still needs that one
// to exist. Deleting a different collection than the one being read is also
// the more honest test: it proves a delete does not disturb any collection
// other than the one asked for.
// ---------------------------------------------------------------------------
await page.goto(`${APP}/collections`);
await page.waitForSelector('app-collection-card');

await page.getByRole('button', { name: 'New collection' }).first().click();
await page.waitForSelector('dialog[open]');
await page.getByLabel('Name').fill('Temporary, safe to delete');
await page.getByRole('button', { name: 'Create collection' }).click();
await page.waitForSelector('app-collection-card >> text=Temporary, safe to delete');

// ---------------------------------------------------------------------------
// 6c. THE route-provider regression, again -- this time for QuotesStore and
// CollectionDetailStore on /collections/:id.
//
// "Stoics worth rereading" (visited above) already has 2 quotes added to it,
// so its picker's candidate count is 16 of 18. If QuotesStore or
// CollectionDetailStore were cached on the ROUTE instead of the component --
// exactly the mistake app.routes.ts's own doc comment describes for the OTHER
// two routes -- navigating into this brand-new, empty collection would reuse
// those stale instances and its picker would show the same 16, or nothing at
// all if a search term was left behind. It must show all 18.
// ---------------------------------------------------------------------------
await page.getByRole('link', { name: 'Temporary, safe to delete' }).click();
await page.waitForURL(/\/collections\/\d+/);
await page.getByRole('button', { name: 'Add a quote' }).first().click();
await page.waitForSelector('dialog[open]');
const freshCollectionCandidates = await page.locator('.picker__item').count();
check(
  'a second, unrelated collection\'s picker is not contaminated by the first one\'s state (QuotesStore/CollectionDetailStore are not cached on the route)',
  freshCollectionCandidates === 18,
  `expected 18, got ${freshCollectionCandidates}`,
);
await page.keyboard.press('Escape');
await page.waitForTimeout(200);
await page.goto(`${APP}/collections`);
await page.waitForSelector('app-collection-card');

await page
  .locator('app-collection-card')
  .filter({ hasText: 'Temporary, safe to delete' })
  .getByRole('button', { name: /^Delete/ })
  .click();
await page.waitForSelector('dialog[open]');
check(
  'deleting a collection asks first, and names what will be lost',
  (await page.getByRole('heading', { name: 'Delete this collection?' }).count()) === 1 &&
    (await page.getByText(/will be removed\. The quotes themselves are not deleted/).count()) === 1,
);
await page.screenshot({ path: `${SHOTS}collections-delete-01-confirm.png`, fullPage: true });

// The stretched-link regression: .collection__link's ::after covers the whole
// card so the name link's hit area extends over it. Without .collection__footer
// lifted into its own stacking context, this click would have navigated to the
// collection instead of opening the confirm dialog -- which the assertion above
// already proves didn't happen, since a navigation would have landed on the
// detail page's own dialog-less markup, not this confirm dialog.
await page.getByRole('button', { name: 'Delete collection' }).click();
await page.waitForTimeout(500);
check(
  'confirming removes only the deleted collection, leaving the other one',
  (await page.locator('app-collection-card').filter({ hasText: 'Temporary, safe to delete' }).count()) === 0 &&
    (await page.getByRole('link', { name: 'Stoics worth rereading' }).count()) === 1,
);
await shotWithApiEvidence(
  'collections-delete-02-success-with-api-response.png',
  lastApiResponse('DELETE', /^\/api\/collections\/\d+$/),
  'Delete collection — actual request/response captured from the network',
);
check(
  'the scroll lock is released after deleting a collection',
  (await isScrollLocked(page)) === false,
);

// ---------------------------------------------------------------------------
// 7. Quotes: paging, filtering, create, delete
// ---------------------------------------------------------------------------
await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Quotes' }).click();
await page.waitForURL(/\/quotes/);
await page.waitForSelector('app-quote-card');

const firstPageCount = await page.locator('app-quote-card').count();
check('the quotes page renders a full page of cards', firstPageCount === 12, `${firstPageCount} cards`);
check(
  'pagination reports the API totals',
  (await page.getByText('Showing 1–12 of 18').count()) === 1,
);
await page.waitForTimeout(600);

await page.getByRole('button', { name: 'Next' }).click();
await page.waitForTimeout(500);
check(
  'Next pages forward',
  (await page.getByText('Showing 13–18 of 18').count()) === 1 &&
    (await page.locator('app-quote-card').count()) === 6,
);
check('Next disables itself on the last page', await page.getByRole('button', { name: 'Next' }).isDisabled());

await page.getByRole('button', { name: 'Previous' }).click();
await page.waitForTimeout(500);

await page.getByLabel('Filter').fill('seneca');
await page.waitForTimeout(250);
check(
  'the filter narrows the page client-side',
  (await page.locator('app-quote-card').count()) === 1 &&
    (await page.getByText('1 of 12 on this page match').count()) === 1,
);

await page.getByLabel('Filter').fill('no such author anywhere');
await page.waitForTimeout(250);
check(
  'no matches is a distinct state from empty',
  (await page.getByRole('heading', { name: 'Nothing on this page matches' }).count()) === 1,
);

await page.getByLabel('Filter').fill('');
await page.waitForTimeout(250);

await page.getByLabel('Page size').selectOption('24');
await page.waitForTimeout(600);
check(
  'changing page size refetches and resets to page 1',
  (await page.locator('app-quote-card').count()) === 18 &&
    (await page.getByText('Showing 1–18 of 18').count()) === 1,
);

// Create
await page.getByRole('button', { name: 'New quote' }).first().click();
await page.waitForSelector('dialog[open]');
await page.getByRole('button', { name: 'Save quote' }).last().click();
await page.waitForTimeout(200);
check(
  'the quote form validates before submitting',
  (await page.getByText('Author is required.').count()) === 1 &&
    (await page.getByText('Quote is required.').count()) === 1,
);

// Scoped to the open dialog: 'Quote' as a label substring also matches every
// "Delete the quote by ..." button behind it, and a test that fills the wrong
// element is worse than no test.
const quoteDialog = page.locator('dialog[open]');

await quoteDialog.getByLabel('Author').fill('   ');
// The textarea, addressed by element: its accessible name is "Quote * (required)"
// -- the asterisk and the screen-reader-only "(required)" are deliberate, and an
// exact label match would be asserting the wrong thing.
await quoteDialog.locator('textarea').fill('A verified quote, added by the browser.');
await page.getByRole('button', { name: 'Save quote' }).last().click();
await page.waitForTimeout(200);
check(
  'whitespace-only input is rejected the way the API would reject it',
  (await page.getByText('Author cannot be only spaces.').count()) === 1,
);

await quoteDialog.getByLabel('Author').fill('Verification');
const counterText = await quoteDialog.locator('.field__counter').textContent();
check('the textarea counter tracks length', counterText?.includes('/ 1000'), counterText ?? '');
await page.screenshot({ path: `${SHOTS}quotes-create-01-form-filled.png`, fullPage: true });

await page.getByRole('button', { name: 'Save quote' }).last().click();
await page.waitForTimeout(800);
check(
  'creating a quote closes the dialog and shows it first',
  (await page.locator('dialog[open]').count()) === 0 &&
    (await page.locator('app-quote-card').first().getByText('Verification').count()) === 1,
);
await shotWithApiEvidence(
  'quotes-create-02-success-with-api-response.png',
  lastApiResponse('POST', /^\/api\/quotes$/),
  'Create quote — actual request/response captured from the network',
);
check(
  'a quote this user created is marked and offers delete',
  (await page.locator('app-quote-card').first().getByText('yours').count()) === 1,
);
/*
 * A quote the API recorded no creator for.
 *
 * This used to assert the control WAS offered here, on the belief that a null
 * CreatedByUserId means "no ownership rule applies, anyone may act on it".
 * MustOwnQuoteHandler (Day7/piece2) does not implement that: it succeeds only
 * on `callerId is not null && callerId == resource.CreatedByUserId`, and a
 * null owner never equals a real caller id, so DELETE answers 403 for every
 * signed-in caller. The QuotesStore unit tests carried the identical wrong
 * belief until this review corrected canDelete() to match the handler; this
 * check is corrected for the same reason, against the same source of truth.
 */
const unownedCard = page.locator('app-quote-card').filter({ hasText: 'Grace Hopper' });
check(
  'a quote with no recorded creator gets neither the delete control nor the "yours" badge',
  (await unownedCard.getByRole('button', { name: /^Delete/ }).count()) === 0 &&
    (await unownedCard.getByText('yours').count()) === 0,
);

check(
  'quotes created by someone else offer no delete control',
  (await page
    .locator('app-quote-card')
    .filter({ hasText: 'Seneca' })
    .getByRole('button', { name: /^Delete/ })
    .count()) === 0,
);
check('the total grew by one', (await page.getByText('Showing 1–19 of 19').count()) === 1);

// Delete
await page.locator('app-quote-card').first().getByRole('button', { name: /^Delete/ }).click();
await page.waitForSelector('dialog[open]');
await page.screenshot({ path: `${SHOTS}quotes-delete-01-confirm.png`, fullPage: true });

await page.getByRole('button', { name: 'Delete quote' }).click();
await page.waitForTimeout(800);
check(
  'deleting removes it and the total shrinks',
  (await page.getByText('Showing 1–18 of 18').count()) === 1,
);
await shotWithApiEvidence(
  'quotes-delete-02-success-with-api-response.png',
  lastApiResponse('DELETE', /^\/api\/quotes\/\d+$/),
  'Delete quote — actual request/response captured from the network',
);

// ---------------------------------------------------------------------------
// 8. The error state, and retry
// ---------------------------------------------------------------------------
await setMode({ quotes: 'error' });
await page.reload();
await page.waitForSelector('app-error-state', { timeout: 5000 });
check(
  "the API's own ProblemDetails message is what the user sees",
  (await page.getByText('The database is unavailable.').count()) === 1,
);

await setMode({ quotes: 'ok' });
await page.getByRole('button', { name: 'Try again' }).click();
await page.waitForSelector('app-quote-card', { timeout: 5000 });
check('retry re-runs the request that failed', (await page.locator('app-quote-card').count()) > 0);

// ---------------------------------------------------------------------------
// 9. The empty state
// ---------------------------------------------------------------------------
await setMode({ quotes: 'empty' });
await page.reload();
await page.waitForSelector('app-empty-state', { timeout: 5000 });
check(
  'an empty page renders the empty state with a way forward',
  (await page.getByRole('heading', { name: 'No quotes yet' }).count()) === 1 &&
    (await page.locator('app-empty-state').getByRole('button', { name: 'New quote' }).count()) === 1,
);

await setMode({ quotes: 'ok' });
await page.reload();
await page.waitForSelector('app-quote-card');

// ---------------------------------------------------------------------------
// 10. The silent token refresh
// ---------------------------------------------------------------------------
const beforeRefresh = requests.length;
await setMode({ expireCount: 1 });
await page.getByRole('button', { name: 'Next' }).click();
await page.waitForTimeout(1200);

const duringRefresh = requests.slice(beforeRefresh);
check(
  'an expired access token triggers exactly one refresh',
  duringRefresh.filter((entry) => entry.includes('/api/auth/refresh')).length === 1,
  duringRefresh.filter((entry) => entry.includes('/api/auth/refresh')).length + ' refreshes',
);
check(
  'the original request is retried, so the user sees no interruption',
  (await page.locator('app-quote-card').count()) > 0 && page.url().includes('/quotes'),
);

// ---------------------------------------------------------------------------
// 10b. TWO concurrent 401s must still produce ONE refresh
//
// The case that actually matters, and the reason AuthStore shares an in-flight
// promise: this API treats a re-presented refresh token as theft and revokes the
// whole token family, so a second refresh does not merely waste a request -- it
// signs the user out. The collection detail route fires two requests at once
// (the collection, and the quote pool for the picker), so expiring two tokens
// reaches the path.
// ---------------------------------------------------------------------------
const collectionsResponse = await page.request.get(`${API}/api/collections`).catch(() => null);
void collectionsResponse;

await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Collections' }).click();
await page.waitForSelector('app-collection-card');

const beforeConcurrent = requests.length;
await setMode({ expireCount: 2 });
await page.getByRole('link', { name: 'Stoics worth rereading' }).click();
await page.waitForURL(/\/collections\/\d+/);
await page.waitForTimeout(1500);

const concurrentRefreshes = requests
  .slice(beforeConcurrent)
  .filter((entry) => entry.includes('/api/auth/refresh')).length;

check(
  'two requests expiring at once still send exactly one refresh',
  concurrentRefreshes === 1,
  `${concurrentRefreshes} refreshes`,
);
check(
  'both retried requests landed -- the collection and its quote pool',
  (await page.locator('app-collection-quote-row').count()) === 1 &&
    (await page.getByRole('heading', { name: 'Stoics worth rereading' }).count()) === 1,
);

// ---------------------------------------------------------------------------
// 10c. A failed action must not destroy the list
// ---------------------------------------------------------------------------
await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Quotes' }).click();
await page.waitForSelector('app-quote-card');

/*
 * Needs a quote the CALLER owns -- Grace Hopper's has no recorded creator, and
 * (correctly, since the earlier review) that means no Delete control is
 * offered on it at all. Every seeded quote is either someone else's ('999')
 * or nobody's; the only way to get a caller-owned one is to create it, the
 * same way the earlier "creating a quote" section did.
 */
await page.getByRole('button', { name: 'New quote' }).first().click();
await page.locator('dialog[open]').getByLabel('Author').fill('Refused Delete');
await page.locator('dialog[open]').locator('textarea').fill('This quote exists only to be refused.');
await page.getByRole('button', { name: 'Save quote' }).last().click();
await page.waitForTimeout(600);

const cardsBeforeFailedDelete = await page.locator('app-quote-card').count();
await setMode({ deleteFails: true });

await page
  .locator('app-quote-card')
  .filter({ hasText: 'Refused Delete' })
  .getByRole('button', { name: /^Delete/ })
  .click();
await page.getByRole('button', { name: 'Delete quote' }).click();
await page.waitForTimeout(900);

check(
  'a delete the API refuses leaves the list intact',
  (await page.locator('app-quote-card').count()) === cardsBeforeFailedDelete,
  `${cardsBeforeFailedDelete} before, ${await page.locator('app-quote-card').count()} after`,
);
check(
  'the refusal is reported as a failed action, not as a failed page',
  (await page.getByRole('heading', { name: 'That action did not work' }).count()) === 1 &&
    (await page.getByText('Only the person who created this quote can delete it.').count()) === 1 &&
    (await page.getByRole('heading', { name: 'Could not load quotes' }).count()) === 0,
);

await setMode({ deleteFails: false });

// ---------------------------------------------------------------------------
// 10d. Leaving a page and coming back starts it fresh
//
// The regression this exists for: the stores were provided on the ROUTE, which
// looks like per-activation scoping and is not -- Angular caches the route's
// environment injector, so the store survived. Leaving the list on page 2 and
// returning showed page 2 again, with the previous page's rows still in memory.
// ---------------------------------------------------------------------------
await page.getByRole('button', { name: 'Next' }).click();
await page.waitForTimeout(600);
check('paged forward before leaving', (await page.getByText(/Page 2 of/).count()) === 1);

await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Collections' }).click();
await page.waitForSelector('app-collection-card');
await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Quotes' }).click();
await page.waitForSelector('app-quote-card');

check(
  'returning to a page starts it at page 1, not where it was left',
  (await page.getByText(/Page 1 of/).count()) === 1,
);

// ---------------------------------------------------------------------------
// 11. Theme
// ---------------------------------------------------------------------------
check(
  'the theme control is an icon button with a name that says what it does',
  (await page.getByRole('button', { name: 'Switch to dark theme' }).locator('svg').count()) === 1,
);

await page.getByRole('button', { name: 'Switch to dark theme' }).click();
await page.waitForTimeout(300);
check(
  'the toggle sets data-theme on <html>',
  (await page.evaluate(() => document.documentElement.getAttribute('data-theme'))) === 'dark',
);

const darkBackground = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
check('the dark theme repaints the page black', darkBackground === 'rgb(11, 15, 15)', darkBackground);

await page.reload();
await page.waitForTimeout(500);
check(
  'the theme choice survives a reload',
  (await page.evaluate(() => document.documentElement.getAttribute('data-theme'))) === 'dark',
);

await page.getByRole('button', { name: 'Switch to light theme' }).click();
await page.waitForTimeout(250);

// ---------------------------------------------------------------------------
// 11b. A route id that is not a number
//
// This used to render the success branch with an empty <h1>, a "0 of 50" badge
// and an empty list -- no loading, no empty, no error -- because nothing loaded
// and nothing failed, which the store's view states could not express.
// ---------------------------------------------------------------------------
await page.goto(`${APP}/collections/not-a-number`);
await page.waitForSelector('app-error-state', { timeout: 5000 });
check(
  'an invalid collection id shows an error, not a blank collection',
  (await page.getByText('That collection address is not valid.').count()) === 1 &&
    (await page.locator('app-page-header').count()) === 0,
);

// ---------------------------------------------------------------------------
// 12. Not found
// ---------------------------------------------------------------------------
await page.goto(`${APP}/no-such-page`);
await page.waitForSelector('app-empty-state');
check(
  'an unknown route renders inside the layout, with navigation intact',
  (await page.getByRole('heading', { name: 'That page does not exist' }).count()) === 1 &&
    (await page.getByRole('navigation', { name: 'Main' }).count()) === 1,
);

// A page whose only heading is an <h3> has no <h1> at all, and the document
// outline starts two levels down.
check(
  'the not-found page has a top-level heading',
  (await page.locator('h1').count()) === 1,
);

// ---------------------------------------------------------------------------
// 12b. The quote detail page: its four states, and the interleave
// ---------------------------------------------------------------------------
// Everything here is about GET /api/quotes/{id}, which until this feature had a
// typed client method that nothing called and no coverage of any kind.

await page.goto(`${APP}/quotes/1`);
await page.waitForSelector('app-page-header');

const detailResponse = lastApiResponse('GET', /^\/api\/quotes\/1$/);

check(
  'a quote opened by id renders its own text and author',
  (await page.locator('blockquote').count()) === 1 &&
    (await page.locator('cite').count()) === 1,
);
check(
  'the detail request went to the single-quote endpoint',
  detailResponse?.status === 200,
  `${detailResponse?.status}`,
);

await shotWithApiEvidence(
  'quotes-detail-01-loaded-with-api-response.png',
  detailResponse,
  'GET /api/quotes/1 -- one quote, opened by id',
);

// The quote on screen must not also appear in the list beneath it.
check(
  'the open quote is excluded from "More quotes"',
  (await page.locator('.detail__more-link').filter({ hasText: await page.locator('blockquote').innerText() }).count()) === 0,
);

// --- 404: a real id shape for a quote that does not exist ------------------
await page.goto(`${APP}/quotes/9999`);
await page.waitForSelector('app-error-state');

const missingResponse = lastApiResponse('GET', /^\/api\/quotes\/9999$/);

check(
  'a 404 is reported as "no such quote" rather than as a load failure',
  (await page.getByRole('heading', { name: 'No such quote' }).count()) === 1,
);
check('the 404 came from the API and was not guessed', missingResponse?.status === 404);
check(
  'the 404 offers the list as the way out, not a retry',
  (await page.getByRole('link', { name: 'Back to all quotes' }).count()) === 1,
);

await shotWithApiEvidence(
  'quotes-detail-02-not-found-with-api-response.png',
  missingResponse,
  'GET /api/quotes/9999 -- 404 told apart from a transport failure',
);

// --- a malformed id: answered without asking the API -----------------------
const beforeMalformed = requests.filter((entry) => /\/api\/quotes\/\d+/.test(entry)).length;

await page.goto(`${APP}/quotes/0x10`);
await page.waitForSelector('app-error-state');

check(
  'a malformed id is answered without a request',
  requests.filter((entry) => /\/api\/quotes\/\d+/.test(entry)).length === beforeMalformed,
);
check(
  '/quotes/0x10 does not silently open quote 16',
  (await page.getByRole('heading', { name: 'No such quote' }).count()) === 1 &&
    (await page.locator('blockquote').count()) === 0,
);

// A malformed id must also release the previously opened quote, or the quote you
// just came from goes missing from the list on the page that says it cannot be
// found. This is the defect the PR review caught.
const moreCountOnMalformed = await page.locator('.detail__more-link').count();
check(
  'a malformed id releases the previous quote, so the library list is complete',
  moreCountOnMalformed === 12,
  `${moreCountOnMalformed} links`,
);

// --- THE INTERLEAVE --------------------------------------------------------
// Quote 1 is made slow and quote 2 left fast, then quote 1 is opened and quote 2
// opened immediately after. The responses therefore arrive in the OPPOSITE order
// to the clicks, which is the only arrangement under which a missing guard is
// observable. Without the artificial delay the first request has always already
// landed and this check passes against broken code.
await setMode({ quoteDetailDelayMs: { 1: 1200 } });

await page.goto(`${APP}/quotes/1`);

// Deliberately NOT waiting for the quote: the point is to leave with the request
// still on the wire.
await page.waitForTimeout(150);
await page.getByRole('link', { name: 'Quotes' }).first().click();
await page.waitForURL(/\/quotes$/);
await page.goto(`${APP}/quotes/2`);
await page.waitForSelector('blockquote');

const quoteTwoText = await page.locator('blockquote').innerText();

// Now wait past the slow response's arrival. If it is allowed to write, the page
// swaps to quote 1 while the address bar still says quote 2.
await page.waitForTimeout(1600);

const textAfterLateResponse = await page.locator('blockquote').innerText();

check(
  'a late response for a quote no longer being viewed does not replace the current one',
  textAfterLateResponse === quoteTwoText,
  `showed: ${textAfterLateResponse.slice(0, 40)}`,
);
check('the address bar and the rendered quote still agree', page.url().endsWith('/quotes/2'));

await page.screenshot({
  path: `${SHOTS}/quotes-detail-03-stale-response-discarded.png`,
  fullPage: true,
});

// Same again for the id that was made slow being the one navigated AWAY from
// into a 404: a late 404 must not mark the visible quote as deleted.
await setMode({ quoteDetailDelayMs: { 9998: 1200 } });

await page.goto(`${APP}/quotes/9998`);
await page.waitForTimeout(150);
await page.goto(`${APP}/quotes/3`);
await page.waitForSelector('blockquote');
await page.waitForTimeout(1600);

check(
  'a late 404 does not report the quote now on screen as deleted',
  (await page.locator('blockquote').count()) === 1 &&
    (await page.getByRole('heading', { name: 'No such quote' }).count()) === 0,
);

await setMode({ quoteDetailDelayMs: {} });

await assertNoHorizontalOverflow(page, 'quote detail');

// ---------------------------------------------------------------------------
// 13. Responsive sweep -- every required width, on both a list and a form
// ---------------------------------------------------------------------------
const widths = [375, 430, 768, 1024, 1440, 1920];

for (const width of widths) {
  await page.setViewportSize({ width, height: 900 });

  await page.goto(`${APP}/quotes`);
  await page.waitForSelector('app-quote-card');

  // Let the staggered entrance finish: a screenshot taken mid-animation shows
  // half-faded cards and is useless as evidence of a layout.
  await page.waitForTimeout(600);

  await assertNoHorizontalOverflow(page, `quotes @ ${width}`);
  await page.screenshot({ path: `${SHOTS}responsive-quotes-${width}.png`, fullPage: true });

  // The grid's column count at this width, read from the rendered layout rather
  // than from the stylesheet.
  const columns = await page.evaluate(() => {
    const grid = document.querySelector('.grid');
    return grid ? getComputedStyle(grid).gridTemplateColumns.split(' ').length : 0;
  });

  const expected = width >= 1024 ? 3 : width >= 640 ? 2 : 1;
  check(`quotes grid is ${expected}-up at ${width}px`, columns === expected, `${columns} columns`);

  await page.goto(`${APP}/collections`);
  await page.waitForSelector('app-collection-card');
  await assertNoHorizontalOverflow(page, `collections @ ${width}`);

  // A dialog at this width -- the element most likely to overflow.
  await page.getByRole('button', { name: 'New collection' }).first().click();
  await page.waitForSelector('dialog[open]');
  await assertNoHorizontalOverflow(page, `dialog @ ${width}`);
  await page.screenshot({ path: `${SHOTS}responsive-collection-dialog-${width}.png` });
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
}

await page.setViewportSize({ width: 375, height: 812 });
await page.goto(`${APP}/sign-in`);
await page.waitForTimeout(400);

// ---------------------------------------------------------------------------
// 14. The first tab stop is the skip link
// ---------------------------------------------------------------------------
await page.setViewportSize({ width: 1440, height: 900 });
await page.goto(`${APP}/quotes`);
await page.waitForSelector('app-quote-card');

await page.keyboard.press('Tab');
const firstStop = await page.evaluate(() => document.activeElement?.textContent?.trim() ?? '');
check('the first tab stop is the skip link', firstStop === 'Skip to content', firstStop);

// ---------------------------------------------------------------------------
// 15. Sign out clears the session
// ---------------------------------------------------------------------------
await page.getByRole('button', { name: 'Sign out' }).click();
await page.waitForURL(/\/sign-in/, { timeout: 5000 });

const storedSession = await page.evaluate(() => sessionStorage.getItem('quotes-web.session'));
check('signing out clears the stored session', storedSession === null);

await page.goto(`${APP}/quotes`);
await page.waitForURL(/\/sign-in/);
check('a signed-out user cannot re-enter a protected route', page.url().includes('/sign-in'));

// ---------------------------------------------------------------------------
// 16. Nothing broke quietly
// ---------------------------------------------------------------------------
// The run deliberately provokes a wrong password, a 403, a 500 and expired
// tokens, and the browser logs a console line for each failed response. Those
// lines are the evidence the paths were exercised, not defects, so they are
// filtered out by status.
//
// Note what this filter also discards: a genuinely missing asset would be a 404
// and would be swallowed with them. Thrown exceptions, template errors and CORS
// failures are not status-shaped and still fail the run.
const unexpectedConsoleErrors = consoleErrors.filter(
  (message) => !/status of (400|401|403|404|500)/.test(message),
);

check(
  'no unexpected console errors or unhandled exceptions during the whole run',
  unexpectedConsoleErrors.length === 0,
  unexpectedConsoleErrors.slice(0, 5).join(' | '),
);

await browser.close();

console.log(`\n${results.length - failures}/${results.length} checks passed`);
process.exit(failures === 0 ? 0 : 1);
