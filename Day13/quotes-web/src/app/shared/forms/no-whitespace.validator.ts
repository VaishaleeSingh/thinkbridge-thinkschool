import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/**
 * Rejects a value that is only whitespace.
 *
 * `Validators.required` alone accepts a single space, which the API then rejects
 * -- its `Quote.Create` and `Collection` constructor both use
 * string.IsNullOrWhiteSpace. So a form with only `required` lets a user submit
 * " " and get a 400 back for a field that looks filled in.
 *
 * Returns null for an empty value on purpose: "is it present" is
 * `Validators.required`'s question, and a field reporting two errors for one
 * mistake would show whichever message happened to be checked first.
 */
export function noWhitespace(): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const value = control.value;

    if (typeof value !== 'string' || value.length === 0) {
      return null;
    }

    return value.trim().length === 0 ? { whitespace: true } : null;
  };
}
