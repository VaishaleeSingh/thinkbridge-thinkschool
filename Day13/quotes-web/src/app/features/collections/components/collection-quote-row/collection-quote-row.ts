import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { CollectionQuote } from '../../../../core/models/collection';
import { Button } from '../../../../shared/components/button/button';
import { RelativeTimePipe } from '../../../../shared/pipes/relative-time-pipe';

/**
 * One quote inside an open collection: the quote, who said it, when it was added
 * to THIS collection, and a control to take it out again.
 *
 * `addedAt` is the field the API's Day-12 read model was reshaped to expose --
 * it belongs to the membership rather than to the quote, and this row is the only
 * place in the application that can show it.
 */
@Component({
  selector: 'app-collection-quote-row',
  templateUrl: './collection-quote-row.html',
  styleUrl: './collection-quote-row.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Button, RelativeTimePipe],
})
export class CollectionQuoteRow {
  readonly quote = input.required<CollectionQuote>();
  readonly removing = input(false);

  readonly removeRequested = output<CollectionQuote>();
}
