import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { Button } from '../button/button';

/**
 * The failed state, with a way out of it.
 *
 * The message is always the API's own normalised failure text (see
 * core/models/api-failure.ts), never a generic "an error occurred": if the API
 * said the collection is full, or that this origin is not allowed by its CORS
 * policy, that is the sentence worth showing.
 *
 * `retry` is an output rather than something this component does itself, because
 * only the caller knows what failed. Every page that uses it re-runs the exact
 * request that failed.
 */
@Component({
  selector: 'app-error-state',
  templateUrl: './error-state.html',
  styleUrl: './error-state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Button],
})
export class ErrorState {
  readonly title = input('Something went wrong');
  readonly message = input.required<string>();

  /** False for failures that retrying cannot fix -- a 403, or a 404. */
  readonly retryable = input(true);

  readonly retryLabel = input('Try again');
  readonly retrying = input(false);

  readonly retry = output<void>();
}
