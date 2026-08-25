import { ChangeDetectionStrategy, Component, ElementRef, input, viewChild } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';

import { firstValidationMessage } from '../../forms/validation-messages';
import { nextId } from '../../utils/unique-id';

/** One option in a SelectField. Value is a string because <option> values are. */
export interface SelectOption {
  readonly value: string;
  readonly label: string;
}

/**
 * A labelled native <select>.
 *
 * Native, not a custom dropdown: a listbox rebuilt from divs has to reimplement
 * keyboard navigation, type-ahead, screen-reader semantics and the platform's own
 * mobile picker -- and usually reimplements the first three incompletely. The
 * only thing given up is full control of the option list's appearance.
 */
@Component({
  selector: 'app-select-field',
  templateUrl: './select-field.html',
  styleUrl: './select-field.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
})
export class SelectField {
  readonly control = input.required<FormControl<string>>();
  readonly label = input.required<string>();
  readonly options = input.required<readonly SelectOption[]>();

  readonly hint = input('');

  protected readonly inputId = nextId('select-field');
  protected readonly hintId = `${this.inputId}-hint`;
  protected readonly errorId = `${this.inputId}-error`;

  private readonly selectRef = viewChild.required<ElementRef<HTMLSelectElement>>('select');

  /** Moves keyboard/screen-reader focus to the native select, e.g. after an invalid submit. */
  focus(): void {
    this.selectRef().nativeElement.focus();
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

  protected describedBy(): string | null {
    if (this.hasError()) {
      return this.errorId;
    }
    return this.hint() ? this.hintId : null;
  }
}
