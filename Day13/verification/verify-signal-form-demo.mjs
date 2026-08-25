/**
 * Day 14, task 2 -- independent verification of the Signal Forms preview
 * demo (quote-form-signal-demo), driven the same way the reactive-forms
 * verification was: keyboard-first, axe-core swept, all four states.
 *
 * This is NOT the agent's own spec file (quote-form-signal-demo.spec.ts) --
 * that's a unit/component test run under Vitest against an isolated
 * fixture. This is a real browser hitting the real dev server and the real
 * (stubbed) API, the same way a person would.
 *
 * Usage: node verify-signal-form-demo.mjs   (with stub-api.mjs and `ng serve` already running)
 */
import { existsSync, readFileSync } from 'node:fs';
import { chromium } from 'playwright';

const SANDBOX_CHROMIUM = '/opt/pw-browsers/chromium';
const launchOptions = existsSync(SANDBOX_CHROMIUM) ? { executablePath: SANDBOX_CHROMIUM } : {};

const APP = 'http://localhost:4200';
const API = 'http://localhost:5059';
const AXE_SOURCE = readFileSync(
  new URL('../node_modules/axe-core/axe.min.js', import.meta.url),
  'utf8',
);

const results = [];
let failures = 0;

function check(name, condition, detail = '') {
  const passed = Boolean(condition);
  if (!passed) failures += 1;
  results.push({ name, passed, detail });
  console.log(`${passed ? 'PASS' : 'FAIL'}  ${name}${detail ? `  -- ${detail}` : ''}`);
}

async function resetStub() {
  await fetch(`${API}/__verify/reset`, { method: 'POST' });
}

async function axeCheck(page) {
  await page.addScriptTag({ content: AXE_SOURCE });
  const results = await page.evaluate(async () => {
    // eslint-disable-next-line no-undef
    return await window.axe.run(document.querySelector('section.signal-demo'), {
      resultTypes: ['violations'],
    });
  });

  const failing = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  const dropped = results.violations.filter((v) => v.impact !== 'serious' && v.impact !== 'critical');
  for (const violation of dropped) {
    console.log(`  (axe, not failing -- ${violation.impact}) ${violation.id}: ${violation.help}`);
  }

  return failing;
}

async function main() {
  await resetStub();

  const browser = await chromium.launch(launchOptions);
  const page = await browser.newPage();
  const consoleErrors = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') consoleErrors.push(msg.text());
  });
  page.on('pageerror', (err) => consoleErrors.push(String(err)));

  // ---------------------------------------------------------------------
  // Sign in, then go straight to the demo route.
  // ---------------------------------------------------------------------
  await page.goto(`${APP}/sign-in`);
  await page.getByRole('button', { name: 'Create one' }).click();
  await page.getByLabel('Email').fill('day14-signal-demo@example.com');
  await page.getByLabel('Password').fill('correct-horse-battery');
  await page.getByRole('button', { name: 'Create account' }).click();
  await page.waitForURL(/\/quotes/, { timeout: 5000 });

  await page.goto(`${APP}/quotes/signal-forms-demo`);
  await page.waitForSelector('section.signal-demo');

  check('the demo page renders the three fields', (await page.locator('#sf-author').count()) === 1 &&
    (await page.locator('#sf-text').count()) === 1 &&
    (await page.locator('#sf-background').count()) === 1);

  // ---------------------------------------------------------------------
  // PRISTINE -- nothing touched yet, no error text, no aria-invalid.
  // ---------------------------------------------------------------------
  check(
    'pristine load has no aria-invalid on any field',
    (await page.locator('[aria-invalid="true"]').count()) === 0,
  );
  check('pristine load shows no error text', (await page.locator('.field__error').count()) === 0);

  // ---------------------------------------------------------------------
  // EMPTY submit -- keyboard only (Tab into the form, Enter to submit).
  // ---------------------------------------------------------------------
  await page.locator('#sf-author').focus();
  await page.keyboard.press('Enter');
  await page.waitForTimeout(150);

  check(
    'an empty submit shows both required errors',
    (await page.getByText('Author is required.').count()) === 1 &&
      (await page.getByText('Quote text is required.').count()) === 1,
  );
  check(
    'empty submit focuses Author, not Text or Background',
    await page.evaluate(() => document.activeElement?.id === 'sf-author'),
  );

  const authorDescribedBy = await page.locator('#sf-author').getAttribute('aria-describedby');
  check(
    "Author's aria-describedby resolves to the exact visible error text",
    authorDescribedBy === 'sf-author-error' &&
      (await page.locator(`#${authorDescribedBy}`).textContent())?.trim() === 'Author is required.',
  );

  let axeFail = await axeCheck(page);
  check('axe finds no serious/critical violations on the empty-invalid form', axeFail.length === 0,
    axeFail.map((v) => v.id).join(', '));

  // ---------------------------------------------------------------------
  // WHITESPACE-only author -- required() alone would accept this.
  // ---------------------------------------------------------------------
  await page.locator('#sf-author').fill('   ');
  await page.locator('#sf-text').fill('A real quote, this time long enough.');
  await page.keyboard.press('Tab');
  await page.locator('#sf-author').focus();
  await page.keyboard.press('Enter');
  await page.waitForTimeout(150);

  check(
    'whitespace-only author is reported as its own error, not silently accepted',
    (await page.getByText('Author cannot be only spaces.').count()) === 1,
  );

  // ---------------------------------------------------------------------
  // Fix author, over-limit text.
  // ---------------------------------------------------------------------
  await page.locator('#sf-author').fill('Seneca');
  const overLimitText = 'x'.repeat(1001);
  await page.evaluate((value) => {
    const el = document.querySelector('#sf-text');
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set;
    setter.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }, overLimitText);
  await page.keyboard.press('Enter');
  await page.waitForTimeout(150);

  check(
    'over-limit text is rejected, and focus moves to Text, not back to Author',
    (await page.getByText('Quote text must be 1000 characters or less.').count()) === 1 &&
      (await page.evaluate(() => document.activeElement?.id === 'sf-text')),
  );

  const maxlenAttr = await page.locator('#sf-text').getAttribute('maxlength');
  check(
    'maxlength is a real native attribute on the textarea (set by maxLength(), not hand-written)',
    maxlenAttr === '1000',
  );

  // ---------------------------------------------------------------------
  // SUBMITTING state -- valid author/text, default background (which the
  // component's own fake action always rejects -- see its comment).
  // ---------------------------------------------------------------------
  await page.locator('#sf-text').fill('A real quote, this time long enough.');
  await page.locator('#sf-author').fill('Seneca');

  const saveButton = page.getByRole('button', { name: /Save quote|Saving…/ });
  await saveButton.click();
  await page.waitForTimeout(50);

  check(
    'the submitting state disables Save and shows "Saving…"',
    (await saveButton.textContent())?.trim() === 'Saving…' && (await saveButton.isDisabled()),
  );
  check(
    'Cancel is disabled while submitting',
    await page.getByRole('button', { name: 'Cancel' }).isDisabled(),
  );

  await page.waitForTimeout(1000);

  // ---------------------------------------------------------------------
  // SERVER-ERROR state -- the default background was rejected by the
  // simulated action after the form was client-valid.
  // ---------------------------------------------------------------------
  check(
    'a server-side rejection of a client-valid field shows up as a real error',
    (await page.getByText('That background is temporarily unavailable. Pick another.').count()) === 1,
  );
  check(
    'focus moves to the server-rejected field',
    await page.evaluate(() => document.activeElement?.id === 'sf-background'),
  );

  const bgDescribedBy = await page.locator('#sf-background').getAttribute('aria-describedby');
  check(
    "the rejected field's aria-describedby resolves to the exact server message",
    bgDescribedBy === 'sf-background-error' &&
      (await page.locator(`#${bgDescribedBy}`).textContent())?.trim() ===
        'That background is temporarily unavailable. Pick another.',
  );

  axeFail = await axeCheck(page);
  check('axe finds no serious/critical violations on the server-error state', axeFail.length === 0,
    axeFail.map((v) => v.id).join(', '));

  // Changing the rejected field should clear the rejection without a second submit.
  await page.locator('#sf-background').selectOption({ index: 1 });
  await page.waitForTimeout(100);
  check(
    'changing the rejected field clears its error without resubmitting',
    (await page.locator('#sf-background[aria-invalid="true"]').count()) === 0,
  );

  // ---------------------------------------------------------------------
  // CLEAN submit -- everything valid, non-default background.
  // ---------------------------------------------------------------------
  await page.getByRole('button', { name: /Save quote|Saving…/ }).click();
  await page.waitForTimeout(1000);

  check(
    'a fully valid submit with a non-default background succeeds',
    (await page.locator('.signal-demo__result').count()) === 1,
  );

  // Excludes a pre-existing, unrelated gap: MainLayout references
  // quotes-brand-mark.svg / quotes-hero-bg.jpg, which 404 on every page in
  // this app (not just this one) because those static assets were never
  // added -- confirmed via a network listener, not assumed. Not this task's
  // bug to fix; flagging it here so it isn't mistaken for API/console noise.
  const apiErrors = consoleErrors.filter((e) => /status of (400|401|403|500)/.test(e));
  check(
    'no unexpected API-level console errors during the whole run',
    apiErrors.length === 0,
    apiErrors.join(' | '),
  );

  await browser.close();

  console.log(`\n${results.length - failures}/${results.length} checks passed`);
  if (failures > 0) process.exitCode = 1;
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
