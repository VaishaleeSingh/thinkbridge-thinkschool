import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';

import { Quote } from '../../../../core/models/quote';
import { API_BASE_URL } from '../../../../core/services/api-base-url';
import { Badge } from '../../../../shared/components/badge/badge';
import { Button } from '../../../../shared/components/button/button';
import { Card } from '../../../../shared/components/card/card';

/**
 * One quote. Roughly forty lines of template and nothing else -- which is the
 * point: the page that shows a grid of these reads as a grid of these, not as
 * three hundred lines of markup with a quote somewhere inside it.
 *
 * It takes a Quote and emits an intent. It does not delete anything: the store
 * owns that, so this component works identically on a page that deletes
 * immediately and one that asks first (which is what quotes-page does).
 */
@Component({
  selector: 'app-quote-card',
  templateUrl: './quote-card.html',
  styleUrl: './quote-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Card, Button, Badge],
})
export class QuoteCard {
  private readonly apiBaseUrl = inject(API_BASE_URL);

  readonly quote = input.required<Quote>();

  /** Whether this user wrote it -- drives the badge only. See QuoteRow. */
  readonly owned = input(false);

  /** Whether to offer a delete control at all -- see QuotesStore.canDelete. */
  readonly deletable = input(false);

  /** True while THIS quote's delete request is in flight. */
  readonly deleting = input(false);

  readonly openRequested = output<Quote>();
  readonly deleteRequested = output<Quote>();

  protected backgroundImage(): string {
    const url = this.quote().backgroundImageUrl;

    if (url.startsWith('http://') || url.startsWith('https://')) {
      return url;
    }

    if (url.startsWith('/')) {
      return `${this.apiBaseUrl}${url}`;
    }

    return url;
  }
}
