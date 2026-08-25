import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { beforeEach, describe, expect, it } from 'vitest';

import { QUOTE_LIMITS } from '../../../../core/models/quote';
import { QuoteFormDialog } from './quote-form-dialog';

/**
 * Focus management on submit -- the one gap the hand-coded a11y wiring in
 * TextField/TextareaField/SelectField left open. These tests drive the real
 * DOM (the visible "Save quote" button, not the protected submit() method)
 * because what is being verified is exactly what a keyboard/screen-reader user
 * experiences: which native element ends up with focus, not just which
 * FormControl ends up invalid.
 */
describe('QuoteFormDialog', () => {
  beforeEach(() => {
    // jsdom does not implement <dialog>'s showModal()/close() -- Modal (the
    // shared component this dialog is built on) calls them from an effect as
    // soon as `open` becomes true, so without a stand-in every test here would
    // fail in Modal rather than in the focus logic under test.
    if (!HTMLDialogElement.prototype.showModal) {
      HTMLDialogElement.prototype.showModal = function (this: HTMLDialogElement): void {
        this.setAttribute('open', '');
      };
    }
    if (!HTMLDialogElement.prototype.close) {
      HTMLDialogElement.prototype.close = function (this: HTMLDialogElement): void {
        this.removeAttribute('open');
      };
    }

    TestBed.configureTestingModule({
      imports: [QuoteFormDialog],
    });
  });

  function createOpenDialog() {
    const fixture = TestBed.createComponent(QuoteFormDialog);
    fixture.componentRef.setInput('open', true);
    fixture.detectChanges();
    return fixture;
  }

  function saveButton(fixture: ReturnType<typeof createOpenDialog>): HTMLButtonElement {
    const buttons = fixture.debugElement.queryAll(By.css('.btn'));
    const save = buttons.find((debugEl) => (debugEl.nativeElement as HTMLElement).textContent?.trim() === 'Save quote');

    if (!save) {
      throw new Error('Save quote button not found');
    }

    return save.nativeElement as HTMLButtonElement;
  }

  it('focuses the Author field when submitting an empty form', async () => {
    const fixture = createOpenDialog();
    await fixture.whenStable();

    saveButton(fixture).click();
    fixture.detectChanges();
    await fixture.whenStable();

    const focused = fixture.debugElement.query(By.css('input.field__input'))
      .nativeElement as HTMLInputElement;

    expect(document.activeElement).toBe(focused);
  });

  it('focuses the Text field, not Author, when author is valid but text is over the limit', async () => {
    const fixture = createOpenDialog();
    await fixture.whenStable();

    const author = fixture.debugElement.query(By.css('input.field__input')).nativeElement as HTMLInputElement;
    const text = fixture.debugElement.query(By.css('textarea.field__input')).nativeElement as HTMLTextAreaElement;

    author.value = 'Seneca';
    author.dispatchEvent(new Event('input'));
    text.value = 'x'.repeat(QUOTE_LIMITS.textMaxLength + 1);
    text.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();

    saveButton(fixture).click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.activeElement).toBe(text);
    expect(document.activeElement).not.toBe(author);
  });

  it('focuses the Background field when the server rejects it via fieldErrors', async () => {
    const fixture = createOpenDialog();
    await fixture.whenStable();

    const author = fixture.debugElement.query(By.css('input.field__input')).nativeElement as HTMLInputElement;
    const text = fixture.debugElement.query(By.css('textarea.field__input')).nativeElement as HTMLTextAreaElement;

    author.value = 'Seneca';
    author.dispatchEvent(new Event('input'));
    text.value = 'A short quote.';
    text.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();

    fixture.componentRef.setInput('fieldErrors', {
      backgroundImageUrl: ['backgroundImageUrl must start with /quote-backgrounds/.'],
    });
    fixture.detectChanges();
    await fixture.whenStable();

    const background = fixture.debugElement.query(By.css('select.field__input')).nativeElement as HTMLSelectElement;

    expect(document.activeElement).toBe(background);
  });
});
