import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { JsonPipe } from '@angular/common';
import {
  FormField,
  form,
  maxLength,
  required,
  submit,
  validate,
  type FieldTree,
} from '@angular/forms/signals';

import {
  CreateQuoteRequest,
  DEFAULT_QUOTE_BACKGROUND_URL,
  QUOTE_BACKGROUND_OPTIONS,
  QUOTE_LIMITS,
} from '../../../../core/models/quote';

/**
 * Side-by-side rebuild of QuoteFormDialog's three fields (author/text/
 * background) using the experimental `@angular/forms/signals` preview API,
 * for a hands-on comparison with the reactive-forms version. Not wired into
 * the real create-quote flow -- see docs/day14-task2-comparison.md for what
 * was actually found while building this, including two things that turned
 * out different from what a skim of the API surface would suggest:
 *
 * 1. THE FIELD-BINDING DIRECTIVE IS NOT CALLED `Field`. The exported type
 *    named `Field<TValue>` (from `@angular/forms/signals`) is just the shape
 *    `() => FieldState<TValue>` -- a FieldTree is one. The directive that
 *    actually binds a FieldTree to a native control is `FormField`, whose
 *    template selector (verified in
 *    node_modules/@angular/forms/types/_structure-chunk.d.ts) is
 *    `[formField]`, not `[field]`. Every doc example floating around uses
 *    `[field]`; this exact install (Angular 21.2.21) does not have a
 *    directive with that selector.
 *
 * 2. NATIVE `aria-invalid`/`aria-describedby` ARE NOT SET FOR YOU. Grepping
 *    the compiled directive (node_modules/@angular/forms/fesm2022/signals.mjs,
 *    setNativeDomProperty()) shows it only ever sets `disabled`, `readonly`,
 *    `required`, `min`, `max`, `minLength`, `maxLength` on a plain
 *    `<input>`/`<textarea>`/`<select>` -- there is no "aria-" string
 *    anywhere in that file. `FormUiControl`'s `invalid`/`errors` inputs are
 *    real, but they only get wired into a component that *declares* those
 *    inputs itself (a custom `FormValueControl`); a bare native element does
 *    not declare them, so it gets nothing. This template sets
 *    aria-invalid/aria-describedby by hand, exactly like TextField/
 *    TextareaField/SelectField do for the reactive version -- confirmed by
 *    reading the rendered DOM in quote-form-signal-demo.spec.ts.
 */
@Component({
  selector: 'app-quote-form-signal-demo',
  templateUrl: './quote-form-signal-demo.html',
  styleUrl: './quote-form-signal-demo.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormField, JsonPipe],
})
export class QuoteFormSignalDemo {
  protected readonly backgroundOptions = QUOTE_BACKGROUND_OPTIONS;

  /**
   * `form()` wants a WritableSignal as its source of truth, not a plain
   * object -- unlike FormGroup, which owns its own value storage.
   */
  private readonly model = signal<CreateQuoteRequest>({
    author: '',
    text: '',
    backgroundImageUrl: DEFAULT_QUOTE_BACKGROUND_URL,
  });

  /**
   * The whole form as one call: no separate FormControl per field, no
   * `nonNullable`/typed-FormControl boilerplate, and the schema function
   * below is genuinely shorter than the reactive version's FormGroup
   * definition for the same three fields. What it does NOT save is the
   * message text -- `noWhitespace()` in the reactive version returns a bare
   * `{ whitespace: true }` and leaves the English up to
   * shared/forms/validation-messages.ts; here each rule states its own
   * `message` inline. Net effect for this exact form: fewer lines overall,
   * but the reactive version's per-app validation-messages.ts does not
   * exist here, so a second field with the same rule would duplicate this
   * wording rather than share it.
   */
  /**
   * `maxLength()` is not only a validator: it also sets `MAX_LENGTH`
   * metadata on the field, which `FormField` reads and applies as the
   * native `maxlength` DOM attribute on the bound `<input>`/`<textarea>`
   * (confirmed in fesm2022/signals.mjs's `setNativeDomProperty`, and by
   * `ng build` itself refusing a hand-written `[attr.maxlength]` on an
   * element that also has `[formField]`, with "Binding to
   * '[attr.maxlength]' is not allowed on nodes using the '[formField]'
   * directive"). TextField has to take `maxLength` as its own input and
   * bind it by hand; here the template does not mention it at all.
   */
  protected readonly quoteForm = form(this.model, (p) => {
    required(p.author, { message: 'Author is required.' });
    maxLength(p.author, QUOTE_LIMITS.authorMaxLength, {
      message: `Author must be ${QUOTE_LIMITS.authorMaxLength} characters or less.`,
    });
    // Same rule as shared/forms/no-whitespace.validator.ts, replicated by
    // hand: null for an empty value (that's required()'s job, see the
    // comment on noWhitespace() for why one mistake should not produce two
    // error messages), a 'whitespace' error otherwise.
    validate(p.author, ({ value }) => {
      const v = value();
      if (v.length === 0 || v.trim().length !== 0) {
        return undefined;
      }
      return { kind: 'whitespace', message: 'Author cannot be only spaces.' };
    });

    required(p.text, { message: 'Quote text is required.' });
    maxLength(p.text, QUOTE_LIMITS.textMaxLength, {
      message: `Quote text must be ${QUOTE_LIMITS.textMaxLength} characters or less.`,
    });
    validate(p.text, ({ value }) => {
      const v = value();
      if (v.length === 0 || v.trim().length !== 0) {
        return undefined;
      }
      return { kind: 'whitespace', message: 'Quote text cannot be only spaces.' };
    });

    // Always populated by the <select>'s own default, but stated anyway so
    // the schema documents the real rule rather than relying on the UI to
    // uphold it silently.
    required(p.backgroundImageUrl, { message: 'Choose a background.' });
  });

  protected readonly savedRequest = signal<CreateQuoteRequest | null>(null);

  /**
   * `submit()`'s own action can return errors targeted at a field via that
   * error's `fieldTree` -- see the type signature of `submit()` in
   * _structure-chunk.d.ts and its own doc example (a "username taken"
   * rejection). That IS a built-in mechanism for feeding a server-side
   * rejection back into field state, which is narrower than the brief's
   * working assumption that there was none -- but it only covers "the
   * action ran and the server said no". There is still nothing built in
   * for the reactive version's actual shape, `Record<string, readonly
   * string[]>` keyed by the API's field names -- that mapping (API key ->
   * FieldTree) has to be written by hand, same as the reactive dialog's
   * `applyFieldError()`, just inside the action instead of an effect.
   *
   * Two more things confirmed by reading _validation_errors-chunk.mjs
   * rather than assumed:
   *   - `submit()` itself calls `markAllAsTouched()` on the whole field
   *     tree before checking validity, so -- unlike the reactive dialog,
   *     which has to call `this.form.markAllAsTouched()` by hand in
   *     `submit()` -- nothing here needs to do that.
   *   - a field's `submissionErrors` (what a targeted rejection sets) is a
   *     `linkedSignal` sourced on that field's own value, so editing the
   *     rejected field clears the rejection automatically -- the reactive
   *     dialog's `applyFieldError()` has an explicit comment about why
   *     clearing an apiError has to go through `updateValueAndValidity()`
   *     rather than a plain `setErrors(null)`; here there is nothing to
   *     write by hand for that at all.
   */
  protected async onSubmit(): Promise<void> {
    const succeeded = await submit(this.quoteForm, {
      action: async () => {
        await fakeDelay(900);

        const { author, text, backgroundImageUrl } = this.quoteForm().value();

        // Simulated server-side rejection, arriving *after* the form was
        // client-valid -- mirrors the reactive dialog's `fieldErrors`
        // input, just triggered locally instead of by a real 400. Chosen
        // to reject backgroundImageUrl specifically because that field has
        // no client-side rule that could ever fail on its own (it is
        // always one of six known-good values), so this is the only way to
        // see a rejection on it at all.
        if (backgroundImageUrl === DEFAULT_QUOTE_BACKGROUND_URL) {
          return {
            kind: 'server',
            message: 'That background is temporarily unavailable. Pick another.',
            fieldTree: this.quoteForm.backgroundImageUrl,
          };
        }

        this.savedRequest.set({
          author: author.trim(),
          text: text.trim(),
          backgroundImageUrl: backgroundImageUrl.trim(),
        });
        return undefined;
      },
    });

    // `submit()` only calls `onInvalid` for a form that was already invalid
    // *before* the action ran -- a rejection the action itself returns does
    // not run it (verified in quote-form-signal-demo.spec.ts: the
    // background field ends up invalid, but nothing here decides to focus
    // it unless this check does). So both paths -- client-invalid on entry,
    // and server-rejected after a client-valid submit -- are handled by the
    // same one check, run after every submit attempt.
    if (!succeeded) {
      this.focusFirstInvalid();
    }
  }

  /**
   * `FieldState.focusBoundControl()` is built in -- no viewChild refs to the
   * three field components, unlike the reactive version's authorField/
   * textField/backgroundField. It focuses whatever native element (or
   * custom control) most recently registered itself as this field's
   * binding, which for this template is always the one `<input>`/
   * `<textarea>`/`<select>` bound to it.
   */
  private focusFirstInvalid(): void {
    if (this.quoteForm.author().invalid()) {
      this.quoteForm.author().focusBoundControl();
      return;
    }

    if (this.quoteForm.text().invalid()) {
      this.quoteForm.text().focusBoundControl();
      return;
    }

    if (this.quoteForm.backgroundImageUrl().invalid()) {
      this.quoteForm.backgroundImageUrl().focusBoundControl();
    }
  }

  /**
   * Same bar as TextField/TextareaField/SelectField's `hasError()`: invalid
   * AND (touched or dirty), so the field does not open already showing red.
   * `FieldState.touched`/`dirty`/`invalid` are plain signals, read fresh on
   * every call the same way the reactive fields read a non-signal
   * `FormControl` fresh on every call -- neither can be a `computed()` for
   * the same reason (a `FormControl` is not a signal at all; a `FieldTree`
   * called as a function returns a new-looking `FieldState` read, not
   * something `computed()` would know to depend on before it happens once).
   */
  protected hasError(field: FieldTree<string>): boolean {
    const state = field();
    return state.invalid() && (state.touched() || state.dirty());
  }

  protected errorMessage(field: FieldTree<string>): string | null {
    return field().errors()[0]?.message ?? null;
  }

  protected isRequired(field: FieldTree<string>): boolean {
    return field().required();
  }

  /**
   * `FieldState.reset()` clears touched/dirty for the whole tree (and, given
   * a value, the model too) in one call -- the reactive dialog's equivalent
   * is `this.form.reset({...}); this.form.markAsUntouched();` as two
   * separate steps.
   */
  protected onCancel(): void {
    this.quoteForm().reset({
      author: '',
      text: '',
      backgroundImageUrl: DEFAULT_QUOTE_BACKGROUND_URL,
    });
    this.savedRequest.set(null);
  }
}

function fakeDelay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
