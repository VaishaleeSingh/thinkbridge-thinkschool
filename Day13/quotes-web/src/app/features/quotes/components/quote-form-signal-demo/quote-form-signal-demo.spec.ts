import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { beforeEach, describe, expect, it } from 'vitest';

import { DEFAULT_QUOTE_BACKGROUND_URL, QUOTE_LIMITS } from '../../../../core/models/quote';
import { QuoteFormSignalDemo } from './quote-form-signal-demo';

/**
 * What this proves, and why each of these was actually run rather than
 * assumed from the API surface:
 *
 * 1. The native input/textarea/select rendered by `[formField]` do NOT get
 *    `aria-invalid`/`aria-describedby` for free -- so before submitting,
 *    those attributes must be absent (nothing to describe yet), and after
 *    an invalid submit they must be present and pointing at real, visible
 *    error text. If Signal Forms wired this automatically, the "absent
 *    before submit" assertions below would still pass by coincidence, but
 *    the whole point of these tests is to have actually looked.
 * 2. Focus moves to the first invalid field on an invalid submit, same bar
 *    as QuoteFormDialog's own tests.
 * 3. A submitted, client-valid form disables Save/Cancel and shows "Saving…"
 *    while the fake 900ms submit is in flight.
 * 4. A simulated server-side rejection of backgroundImageUrl, arriving after
 *    a client-valid submit, shows up as a real error on that field and
 *    moves focus to it -- and editing the field clears the rejection
 *    without a second submit.
 */
describe('QuoteFormSignalDemo', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [QuoteFormSignalDemo],
    });
  });

  function createFixture() {
    const fixture = TestBed.createComponent(QuoteFormSignalDemo);
    fixture.detectChanges();
    return fixture;
  }

  function author(fixture: ReturnType<typeof createFixture>): HTMLInputElement {
    return fixture.debugElement.query(By.css('#sf-author')).nativeElement as HTMLInputElement;
  }

  function text(fixture: ReturnType<typeof createFixture>): HTMLTextAreaElement {
    return fixture.debugElement.query(By.css('#sf-text')).nativeElement as HTMLTextAreaElement;
  }

  function background(fixture: ReturnType<typeof createFixture>): HTMLSelectElement {
    return fixture.debugElement.query(By.css('#sf-background')).nativeElement as HTMLSelectElement;
  }

  function saveButton(fixture: ReturnType<typeof createFixture>): HTMLButtonElement {
    return fixture.debugElement.query(By.css('button[type="submit"]'))
      .nativeElement as HTMLButtonElement;
  }

  function cancelButton(fixture: ReturnType<typeof createFixture>): HTMLButtonElement {
    return fixture.debugElement.query(By.css('button[type="button"]'))
      .nativeElement as HTMLButtonElement;
  }

  function setValue(el: HTMLInputElement | HTMLTextAreaElement, value: string): void {
    el.value = value;
    el.dispatchEvent(new Event('input'));
  }

  /**
   * The fake submit uses a real `setTimeout(ms)`, not a zone-tracked or
   * fakeAsync-controlled one -- this app is zoneless, and nothing patches
   * timers under Vitest here. `fixture.whenStable()` only waits for
   * Angular's own pending-task tracking (renders, pending effects), not for
   * an arbitrary timer nobody told it about, so a real wait is the only way
   * to observe the far side of the simulated 900ms request.
   */
  function waitForFakeSubmit(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 950));
  }

  it('renders no aria-invalid/aria-describedby before any interaction', () => {
    const fixture = createFixture();

    const input = author(fixture);
    expect(input.getAttribute('aria-invalid')).toBeNull();
    expect(input.getAttribute('aria-describedby')).toBeNull();
  });

  it('sets aria-invalid and aria-describedby pointing at real visible error text after an invalid submit', async () => {
    const fixture = createFixture();

    saveButton(fixture).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const input = author(fixture);
    expect(input.getAttribute('aria-invalid')).toBe('true');

    const describedBy = input.getAttribute('aria-describedby');
    expect(describedBy).toBe('sf-author-error');

    const errorEl = fixture.debugElement.query(By.css(`#${describedBy}`)).nativeElement as HTMLElement;
    expect(errorEl.textContent?.trim()).toBe('Author is required.');
  });

  it('applies maxlength as a real DOM attribute without any manual [attr.maxlength] binding', () => {
    const fixture = createFixture();

    expect(author(fixture).getAttribute('maxlength')).toBe(String(QUOTE_LIMITS.authorMaxLength));
    expect(text(fixture).getAttribute('maxlength')).toBe(String(QUOTE_LIMITS.textMaxLength));
  });

  it('rejects a whitespace-only author the same way noWhitespace() does -- required() alone would accept it', async () => {
    const fixture = createFixture();

    setValue(author(fixture), '   ');
    setValue(text(fixture), 'A short quote.');
    fixture.detectChanges();
    await fixture.whenStable();

    saveButton(fixture).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const input = author(fixture);
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(input);

    const errorEl = fixture.debugElement.query(By.css('#sf-author-error')).nativeElement as HTMLElement;
    expect(errorEl.textContent?.trim()).toBe('Author cannot be only spaces.');
  });

  it('focuses the Author field on an empty invalid submit', async () => {
    const fixture = createFixture();

    saveButton(fixture).click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.activeElement).toBe(author(fixture));
  });

  it('focuses the Text field, not Author, when author is valid but text is over the limit', async () => {
    const fixture = createFixture();

    setValue(author(fixture), 'Seneca');
    setValue(text(fixture), 'x'.repeat(QUOTE_LIMITS.textMaxLength + 1));
    fixture.detectChanges();
    await fixture.whenStable();

    saveButton(fixture).click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.activeElement).toBe(text(fixture));
    expect(document.activeElement).not.toBe(author(fixture));
  });

  it('disables Save and Cancel and shows "Saving…" while the fake submit is in flight, for a client-valid submit', async () => {
    const fixture = createFixture();

    setValue(author(fixture), 'Seneca');
    setValue(text(fixture), 'A short quote.');
    fixture.detectChanges();
    await fixture.whenStable();

    const save = saveButton(fixture);
    const cancel = cancelButton(fixture);

    save.click();
    fixture.detectChanges();

    expect(save.disabled).toBe(true);
    expect(cancel.disabled).toBe(true);
    expect(save.textContent?.trim()).toBe('Saving…');

    await waitForFakeSubmit();
    fixture.detectChanges();

    expect(save.disabled).toBe(false);
    expect(cancel.disabled).toBe(false);
  });

  it('surfaces a simulated server-side rejection of backgroundImageUrl after a client-valid submit, and focuses it', async () => {
    const fixture = createFixture();

    setValue(author(fixture), 'Seneca');
    setValue(text(fixture), 'A short quote.');
    fixture.detectChanges();
    await fixture.whenStable();

    // backgroundImageUrl is left at its default, which onSubmit() always
    // rejects -- see the component's own comment on why.
    saveButton(fixture).click();
    fixture.detectChanges();
    await waitForFakeSubmit();
    fixture.detectChanges();

    const select = background(fixture);
    expect(select.getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(select);

    const describedBy = select.getAttribute('aria-describedby');
    expect(describedBy).toBe('sf-background-error');
    const errorEl = fixture.debugElement.query(By.css(`#${describedBy}`)).nativeElement as HTMLElement;
    expect(errorEl.textContent?.trim()).toBe(
      'That background is temporarily unavailable. Pick another.',
    );

    // Author/text were valid and untouched by the rejection.
    expect(author(fixture).getAttribute('aria-invalid')).toBeNull();
  });

  it('clears the server rejection on backgroundImageUrl as soon as its value changes, without a second submit', async () => {
    const fixture = createFixture();

    setValue(author(fixture), 'Seneca');
    setValue(text(fixture), 'A short quote.');
    fixture.detectChanges();
    await fixture.whenStable();

    saveButton(fixture).click();
    fixture.detectChanges();
    await waitForFakeSubmit();
    fixture.detectChanges();

    const select = background(fixture);
    expect(select.getAttribute('aria-invalid')).toBe('true');

    select.value = select.options[1].value;
    select.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(select.getAttribute('aria-invalid')).toBeNull();
  });

  it('saves successfully once a non-default background is chosen', async () => {
    const fixture = createFixture();

    setValue(author(fixture), 'Seneca');
    setValue(text(fixture), 'A short quote.');
    const select = background(fixture);
    select.value = select.options[1].value;
    select.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();

    saveButton(fixture).click();
    fixture.detectChanges();
    await waitForFakeSubmit();
    fixture.detectChanges();

    expect(background(fixture).getAttribute('aria-invalid')).toBeNull();
    const result = fixture.debugElement.query(By.css('.signal-demo__result pre')).nativeElement as HTMLElement;
    expect(result.textContent).toContain('Seneca');
    expect(result.textContent).not.toContain(DEFAULT_QUOTE_BACKGROUND_URL);
  });
});
