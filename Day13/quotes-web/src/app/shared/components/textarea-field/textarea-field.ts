import { ChangeDetectionStrategy, Component, ElementRef, input, viewChild } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';

import { firstValidationMessage } from '../../forms/validation-messages';
import { nextId } from '../../utils/unique-id';

/**
 * A labelled multi-line input. Same contract as TextField -- it takes the
 * control, not a value -- with two additions that only matter for long text:
 * `rows`, and a live character counter.
 *
 * The counter exists because the API's limit is real (1000 characters for a
 * quote's text) and hitting it silently, with the input simply refusing further
 * keystrokes, reads as a broken text box rather than as a limit.
 */
@Component({
  selector: 'app-textarea-field',
  templateUrl: './textarea-field.html',
  styleUrl: './textarea-field.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
})
export class TextareaField {
  readonly control = input.required<FormControl<string>>();
  readonly label = input.required<string>();

  readonly placeholder = input('');
  readonly hint = input('');
  readonly rows = input(4);
  readonly maxLength = input<number | null>(null);

  protected readonly inputId = nextId('textarea-field');
  protected readonly hintId = `${this.inputId}-hint`;
  protected readonly errorId = `${this.inputId}-error`;
  protected readonly counterId = `${this.inputId}-counter`;

  private readonly textareaRef = viewChild.required<ElementRef<HTMLTextAreaElement>>('textarea');

  /** Moves keyboard/screen-reader focus to the native textarea, e.g. after an invalid submit. */
  focus(): void {
    this.textareaRef().nativeElement.focus();
  }

  protected isRequired(): boolean {
    return this.control().hasValidator(Validators.required);
  }

  protected hasError(): boolean {
    const control = this.control();
    return control.invalid && (control.touched || control.dirty);
  }

  protected errorMessage(): string | null {
    return firstValidationMessage(this.control(), this.label());
  }

  protected length(): number {
    return this.control().value.length;
  }

  /** Warn before the limit rather than at it, so there is time to edit. */
  protected isNearLimit(): boolean {
    const max = this.maxLength();
    return max !== null && this.length() > max * 0.9;
  }

  protected describedBy(): string | null {
    const ids = [
      this.hasError() ? this.errorId : this.hint() ? this.hintId : null,
      this.maxLength() !== null ? this.counterId : null,
    ].filter((id): id is string => id !== null);

    return ids.length > 0 ? ids.join(' ') : null;
  }
}
