import { ChangeDetectionStrategy, Component, effect, input, output } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import { COLLECTION_LIMITS } from '../../../../core/models/collection';
import { Button } from '../../../../shared/components/button/button';
import { Modal } from '../../../../shared/components/modal/modal';
import { TextField } from '../../../../shared/components/text-field/text-field';
import { noWhitespace } from '../../../../shared/forms/no-whitespace.validator';

/** The "new collection" dialog -- one field, same contract as QuoteFormDialog. */
@Component({
  selector: 'app-collection-form-dialog',
  templateUrl: './collection-form-dialog.html',
  styleUrl: './collection-form-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, Modal, Button, TextField],
})
export class CollectionFormDialog {
  readonly open = input(false);
  readonly submitting = input(false);
  readonly fieldErrors = input<Readonly<Record<string, readonly string[]>>>({});

  readonly submitted = output<string>();
  readonly cancelled = output<void>();

  protected readonly limits = COLLECTION_LIMITS;

  protected readonly form = new FormGroup({
    name: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.maxLength(COLLECTION_LIMITS.nameMaxLength),
        noWhitespace(),
      ],
    }),
  });

  constructor() {
    effect(() => {
      if (this.open()) {
        this.form.reset({ name: '' });
        this.form.markAsUntouched();
      }
    });

    effect(() => {
      const message = this.fieldErrors()['name']?.[0];
      const control = this.form.controls.name;

      if (message) {
        control.setErrors({ apiError: message });
        control.markAsTouched();
      } else if (control.hasError('apiError')) {
        control.updateValueAndValidity();
      }
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitted.emit(this.form.getRawValue().name.trim());
  }
}
