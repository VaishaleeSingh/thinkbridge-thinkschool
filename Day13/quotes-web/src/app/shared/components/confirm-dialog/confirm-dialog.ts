import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { Button } from '../button/button';
import { Modal } from '../modal/modal';

/**
 * "Are you sure?" -- composed from Modal rather than reimplementing it, so
 * focus handling, Escape and the scroll lock are the same code the other dialogs
 * use.
 *
 * It exists as its own component because a destructive confirmation has details
 * that are easy to get wrong once per call site: the confirm button carries the
 * danger styling and the loading state, the cancel button is the one that gets
 * initial focus, and the dialog cannot be dismissed while the delete is in
 * flight.
 */
@Component({
  selector: 'app-confirm-dialog',
  templateUrl: './confirm-dialog.html',
  styleUrl: './confirm-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal, Button],
})
export class ConfirmDialog {
  readonly open = input(false);
  readonly title = input('Are you sure?');
  readonly message = input.required<string>();
  readonly confirmLabel = input('Delete');
  readonly cancelLabel = input('Cancel');

  /** True while the confirmed action is running. */
  readonly busy = input(false);

  readonly confirmed = output<void>();
  readonly cancelled = output<void>();
}
