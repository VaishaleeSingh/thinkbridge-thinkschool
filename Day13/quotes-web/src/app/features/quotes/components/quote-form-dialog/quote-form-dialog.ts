import { ChangeDetectionStrategy, Component, effect, input, output } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import {
  CreateQuoteRequest,
  DEFAULT_QUOTE_BACKGROUND_URL,
  QUOTE_BACKGROUND_OPTIONS,
  QUOTE_LIMITS,
  Quote,
} from '../../../../core/models/quote';
import { Button } from '../../../../shared/components/button/button';
import { Modal } from '../../../../shared/components/modal/modal';
import { SelectField, SelectOption } from '../../../../shared/components/select-field/select-field';
import { TextField } from '../../../../shared/components/text-field/text-field';
import { TextareaField } from '../../../../shared/components/textarea-field/textarea-field';
import { noWhitespace } from '../../../../shared/forms/no-whitespace.validator';

/**
 * The "new quote" dialog: a typed form inside the shared Modal.
 *
 * It reports what the user asked for and nothing more -- `submitted` carries the
 * request, and the parent decides what to do with it. This component never calls
 * the API, which is why it needs no error handling of its own beyond displaying
 * the field errors it is handed.
 */
@Component({
  selector: 'app-quote-form-dialog',
  templateUrl: './quote-form-dialog.html',
  styleUrl: './quote-form-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, Modal, Button, TextField, TextareaField, SelectField],
})
export class QuoteFormDialog {
  readonly open = input(false);
  readonly submitting = input(false);
  readonly quote = input<Quote | null>(null);

  /**
   * Field errors from the API's ValidationProblemDetails, keyed as the API keys
   * them ("author", "text"). Passed in rather than fetched, so this component
   * stays ignorant of HTTP.
   */
  readonly fieldErrors = input<Readonly<Record<string, readonly string[]>>>({});

  readonly submitted = output<CreateQuoteRequest>();
  readonly cancelled = output<void>();

  protected readonly limits = QUOTE_LIMITS;
  protected readonly backgroundOptions: readonly SelectOption[] = QUOTE_BACKGROUND_OPTIONS.map(
    (option) => ({
      value: option.url,
      label: option.label,
    }),
  );

  protected readonly form = new FormGroup({
    author: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.maxLength(QUOTE_LIMITS.authorMaxLength),
        noWhitespace(),
      ],
    }),
    text: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.maxLength(QUOTE_LIMITS.textMaxLength),
        noWhitespace(),
      ],
    }),
    backgroundImageUrl: new FormControl(DEFAULT_QUOTE_BACKGROUND_URL, {
      nonNullable: true,
      validators: [Validators.required],
    }),
  });

  protected readonly modalTitle = () => (this.quote() ? 'Edit quote' : 'New quote');

  constructor() {
    // Opening is what clears the form -- not closing. Clearing on close would
    // wipe what someone typed at the moment a failed submit is telling them to
    // fix it, and this dialog is closed by the parent only after a success.
    effect(() => {
      if (this.open()) {
        const quote = this.quote();

        this.form.reset({
          author: quote?.author ?? '',
          text: quote?.text ?? '',
          backgroundImageUrl: quote?.backgroundImageUrl ?? DEFAULT_QUOTE_BACKGROUND_URL,
        });
        this.form.markAsUntouched();
      }
    });

    // Server-side validation, moved onto the fields it concerns. A real side
    // effect on non-reactive objects (the controls), driven by a signal input --
    // which is what effect() is for.
    effect(() => {
      const errors = this.fieldErrors();

      this.applyFieldError(this.form.controls.author, errors['author']?.[0]);
      this.applyFieldError(this.form.controls.text, errors['text']?.[0]);
      this.applyFieldError(this.form.controls.backgroundImageUrl, errors['backgroundImageUrl']?.[0]);
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { author, text, backgroundImageUrl } = this.form.getRawValue();

    // Trimmed here as well as server-side: the API normalises whitespace itself
    // (IQuoteTextNormalizer), so sending " Seneca " would succeed and then come
    // back looking different from what was typed, which reads as the app having
    // changed it.
    this.submitted.emit({
      author: author.trim(),
      text: text.trim(),
      backgroundImageUrl: backgroundImageUrl.trim(),
    });
  }

  private applyFieldError(control: FormControl<string>, message: string | undefined): void {
    if (message) {
      control.setErrors({ apiError: message });
      control.markAsTouched();
      return;
    }

    // Clearing has to go through re-validation rather than setErrors(null):
    // otherwise dismissing a server error would also drop the client-side rules
    // and let an empty field submit.
    if (control.hasError('apiError')) {
      control.updateValueAndValidity();
    }
  }
}
