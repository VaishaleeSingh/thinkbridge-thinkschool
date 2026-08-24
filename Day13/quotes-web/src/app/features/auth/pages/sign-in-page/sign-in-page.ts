import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';

import { ApiFailure } from '../../../../core/models/api-failure';
import { AuthStore } from '../../../../core/services/auth-store';
import { Button } from '../../../../shared/components/button/button';
import { Card } from '../../../../shared/components/card/card';
import { TextField } from '../../../../shared/components/text-field/text-field';

type Mode = 'sign-in' | 'register';

/** The API's own floor for a new password -- see /api/auth/register. */
const PASSWORD_MIN_LENGTH = 8;

/**
 * Sign in, or create an account -- one page, because the two differ by one API
 * call and one word of copy. Two pages would mean two copies of the same form,
 * the same validation and the same error handling.
 *
 * A typed FormGroup (FormControl<string>, nonNullable) rather than a loose one:
 * `form.value.email` is `string`, not `string | null | undefined`, so nothing
 * downstream has to defend against a value the form cannot actually produce.
 */
@Component({
  selector: 'app-sign-in-page',
  templateUrl: './sign-in-page.html',
  styleUrl: './sign-in-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, Card, TextField, Button],
})
export class SignInPage {
  private readonly authStore = inject(AuthStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly mode = signal<Mode>('sign-in');

  /** Both come straight from the store -- this page keeps no copy of either. */
  protected readonly submitting = this.authStore.isSubmitting;
  protected readonly failure = this.authStore.error;

  protected readonly form = new FormGroup({
    email: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.email],
    }),
    password: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.minLength(PASSWORD_MIN_LENGTH)],
    }),
  });

  protected readonly isRegistering = computed(() => this.mode() === 'register');

  protected readonly heading = computed(() =>
    this.isRegistering() ? 'Create an account' : 'Sign in',
  );

  protected readonly submitLabel = computed(() =>
    this.isRegistering() ? 'Create account' : 'Sign in',
  );

  protected readonly submitBusyLabel = computed(() =>
    this.isRegistering() ? 'Creating account…' : 'Signing in…',
  );

  protected readonly switchPrompt = computed(() =>
    this.isRegistering() ? 'Already have an account?' : 'No account yet?',
  );

  protected readonly switchAction = computed(() =>
    this.isRegistering() ? 'Sign in instead' : 'Create one',
  );

  protected readonly passwordHint = computed(() =>
    this.isRegistering() ? `At least ${PASSWORD_MIN_LENGTH} characters.` : '',
  );

  /**
   * A summary is only worth showing for a failure that is NOT already sitting
   * under a field: a 401 ("that email and password do not match") has no field to
   * attach to, while a 400 on `email` does, and showing it twice reads as two
   * problems.
   */
  protected readonly summaryMessage = computed(() => {
    const failure = this.failure();

    if (!failure) {
      return null;
    }

    return hasFieldErrors(failure) ? null : failure.message;
  });

  protected switchMode(): void {
    this.mode.update((current) => (current === 'sign-in' ? 'register' : 'sign-in'));

    // The password rules differ between the two modes only in what the API will
    // accept, but a stale "must be at least 8 characters" error under a
    // now-correct password is confusing, so validation starts fresh.
    this.form.controls.password.reset('');
    this.form.controls.password.markAsUntouched();
  }

  protected async submit(): Promise<void> {
    // Touch everything first: without this, submitting an untouched empty form
    // shows no errors at all, because each field only reveals its own error once
    // touched.
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { email, password } = this.form.getRawValue();

    const succeeded = this.isRegistering()
      ? await this.authStore.register(email, password)
      : await this.authStore.signIn(email, password);

    if (!succeeded) {
      this.applyServerFieldErrors();
      return;
    }

    // A deep link followed while signed out comes back here as returnUrl; see
    // authGuard. Falls back to the quotes list, which is the app's front door.
    const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/quotes';
    await this.router.navigateByUrl(returnUrl);
  }

  /**
   * Moves the API's per-field messages onto the controls they belong to, so a
   * server-side rule reads exactly like a client-side one instead of appearing
   * as a banner above a form that looks valid.
   *
   * `apiError` is the key firstValidationMessage() gives priority to -- the
   * server's answer about a specific value beats this client's guess.
   */
  private applyServerFieldErrors(): void {
    const fieldErrors = this.failure()?.fieldErrors ?? {};

    for (const [field, messages] of Object.entries(fieldErrors)) {
      const control =
        field === 'email'
          ? this.form.controls.email
          : field === 'password'
            ? this.form.controls.password
            : null;

      if (control && messages.length > 0) {
        control.setErrors({ apiError: messages[0] });
        control.markAsTouched();
      }
    }
  }
}

function hasFieldErrors(failure: ApiFailure): boolean {
  return Object.keys(failure.fieldErrors).some(
    (field) => field === 'email' || field === 'password',
  );
}
