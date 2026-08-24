import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { Quote } from '../../../../core/models/quote';
import { QuoteRow } from '../../models/quote-row';
import { QuoteCard } from '../quote-card/quote-card';

/**
 * The responsive grid the quote cards sit in.
 *
 * Its own component rather than a div in the page, because the grid's column
 * behaviour is a decision worth having one home: one column on a phone, two from
 * 640px, three from 1024px, each column allowed to shrink (`minmax(0, 1fr)`) so a
 * long unbroken word inside a card cannot widen the page.
 */
@Component({
  selector: 'app-quotes-grid',
  templateUrl: './quotes-grid.html',
  styleUrl: './quotes-grid.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [QuoteCard],
})
export class QuotesGrid {
  readonly rows = input.required<readonly QuoteRow[]>();

  /** The quote currently being deleted, if any -- so only that card shows a spinner. */
  readonly deletingId = input<number | null>(null);

  readonly openRequested = output<Quote>();
  readonly deleteRequested = output<Quote>();
}
