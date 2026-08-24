import { AbstractControl } from '@angular/forms';

/**
 * Turns a control's first validation error into a sentence a person can act on.
 *
 * WHY IT IS CENTRAL: Angular gives you `{ required: true }` and
 * `{ maxlength: { requiredLength: 200, actualLength: 214 } }`. Turning those into
 * English is work every form would otherwise repeat, and the results drift --
 * "Author is required" on one page, "Please enter an author" on the next. One
 * function means one wording.
 *
 * `fieldLabel` is the field's own visible label, so the message names what the
 * user is looking at rather than a form-control key they have never seen.
 *
 * The `apiError` case is how a server-side validation problem (the API's
 * ValidationProblemDetails, keyed by field) reaches the field it belongs to --
 * see ApiFailure.fieldErrors and how the feature forms apply it.
 */
export function firstValidationMessage(
  control: AbstractControl,
  fieldLabel: string,
): string | null {
  const errors = control.errors;

  if (!errors) {
    return null;
  }

  // A server message always wins: it is the authoritative answer about this
  // exact value, where the client rules are only a guess made before asking.
  if (typeof errors['apiError'] === 'string') {
    return errors['apiError'];
  }

  if (errors['required']) {
    return `${fieldLabel} is required.`;
  }

  if (errors['email']) {
    return 'Enter a valid email address.';
  }

  const minLength = errors['minlength'] as { requiredLength: number } | undefined;
  if (minLength) {
    return `${fieldLabel} must be at least ${minLength.requiredLength} characters.`;
  }

  const maxLength = errors['maxlength'] as { requiredLength: number } | undefined;
  if (maxLength) {
    return `${fieldLabel} must be ${maxLength.requiredLength} characters or less.`;
  }

  if (errors['whitespace']) {
    return `${fieldLabel} cannot be only spaces.`;
  }

  // Deliberately generic rather than dumping the error object: an unknown
  // validator is a bug in this function, and the user should not read its
  // internals.
  return `${fieldLabel} is not valid.`;
}

