import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';

import { Quote, quoteBackgroundImageCss } from '../../../../core/models/quote';
import { API_BASE_URL } from '../../../../core/services/api-base-url';
import { Button } from '../../../../shared/components/button/button';
import { Modal } from '../../../../shared/components/modal/modal';

/**
 * Read-first quote popup: opens when a quote card is clicked.
 *
 * It intentionally starts as presentation, not editing. The primary interaction
 * is reading the quote against its background image with the author below; edit
 * is an explicit secondary action from the footer.
 */
@Component({
  selector: 'app-quote-preview-dialog',
  templateUrl: './quote-preview-dialog.html',
  styleUrl: './quote-preview-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal, Button],
})
export class QuotePreviewDialog {
  private readonly apiBaseUrl = inject(API_BASE_URL);

  readonly open = input(false);
  readonly quote = input<Quote | null>(null);
  readonly canEdit = input(false);

  readonly closed = output<void>();
  readonly editRequested = output<Quote>();

  /** See `quoteBackgroundImageCss` -- shared with the card and the detail page. */
  protected backgroundImageCss(): string {
    const quote = this.quote();

    return quote ? quoteBackgroundImageCss(quote.backgroundImageUrl, this.apiBaseUrl) : 'none';
  }
}
