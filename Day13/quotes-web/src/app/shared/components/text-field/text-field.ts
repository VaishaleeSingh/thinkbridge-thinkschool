import { ChangeDetectionStrategy, Component, ElementRef, input, viewChild } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';

import { firstValidationMessage } from '../../forms/validation-messages';
import { nextId } from '../../utils/unique-id';

/**
 * A labelled text input, wired to a typed reactive FormControl.
 *
 * WHY IT TAKES A CONTROL INSTEAD OF IMPLEMENTING ControlValueAccessor: a CVA
 * wrapper has to re-expose everything the control already knows -- validity,
 * touched, disabled -- through a second set of inputs, and the two can disagree.
 * Handed the control itself, this component reads that state directly and there
 * is only one copy of it. The parent keeps a typed FormGroup and this stays a
 * presentation component.
 *
 * WHAT IT GUARANTEES, so no page has to remember it:
 *   - a real <label for> pointing at the input's id (not a placeholder standing
 *     in for a label, which disappears the moment someone types)
 *   - aria-describedby wired to the hint or the error, whichever is showing
 *   - aria-invalid while invalid, so the state is not conveyed by colour alone
 *   - the error only after the field has been touched, so a form does not open
 *     covered in red
 */
@Component({
  selector: 'app-text-field',
  templateUrl: './text-field.html',
  styleUrl: './text-field.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
})
export class TextField {
  readonly control = input.required<FormControl<string>>();
  readonly label = input.required<string>();

  readonly type = input<'text' | 'email' | 'password' | 'search'>('text');
  readonly placeholder = input('');
  readonly hint = input('');
  readonly autocomplete = input('');
  readonly maxLength = input<number | null>(null);

  /** Rendered inside the label, not as a placeholder or a colour. */
  readonly showOptionalHint = input(false);

  protected readonly inputId = nextId('text-field');
  protected readonly hintId = `${this.inputId}-hint`;
  protected readonly errorId = `${this.inputId}-error`;

  private readonly inputRef = viewChild.required<ElementRef<HTMLInputElement>>('input');

  /** Moves keyboard/screen-reader focus to the native input, e.g. after an invalid submit. */
  focus(): void {
    this.inputRef().nativeElement.focus();
  }

  /**
   * Plain methods, NOT computed(): a FormControl is not a signal, so a computed
   * built on it would cache the first answer and never recompute. These are
   * re-evaluated by change detection, which in a zoneless application runs when
   * the control's own value/blur listeners fire -- exactly when the answer can
   * have changed.
   */
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

  protected describedBy(): string | null {
    if (this.hasError()) {
      return this.errorId;
    }
    return this.hint() ? this.hintId : null;
  }
}
