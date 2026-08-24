import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { CollectionQuote } from '../../../../core/models/collection';
import { Quote } from '../../../../core/models/quote';
import { Badge } from '../../../../shared/components/badge/badge';
import { Button } from '../../../../shared/components/button/button';
import { ConfirmDialog } from '../../../../shared/components/confirm-dialog/confirm-dialog';
import { EmptyState } from '../../../../shared/components/empty-state/empty-state';
import { ErrorState } from '../../../../shared/components/error-state/error-state';
import { Loader } from '../../../../shared/components/loader/loader';
import { PageHeader } from '../../../../shared/components/page-header/page-header';
import { QuotesStore } from '../../../quotes/services/quotes-store';
import { AddQuoteDialog } from '../../components/add-quote-dialog/add-quote-dialog';
import { CollectionQuoteRow } from '../../components/collection-quote-row/collection-quote-row';
import { CollectionDetailStore } from '../../services/collection-detail-store';

/** How many quotes to fetch as add-candidates. Within the API's max page size of 100. */
const PICKER_PAGE_SIZE = 48;

/**
 * One collection, with its quotes.
 *
 * This page holds TWO stores, which is the reason it is the only page that needs
 * a `computed` of its own: the candidate list for the picker is the quotes the
 * API returned (QuotesStore) minus the ones already in this collection
 * (CollectionDetailStore). Neither store can know that on its own, and putting it
 * in either would make that store depend on the other.
 *
 * QuotesStore is REUSED here rather than reimplemented as a picker-specific
 * service: it already fetches, pages and filters quotes, which is exactly what
 * the picker needs.
 */
@Component({
  selector: 'app-collection-detail-page',
  templateUrl: './collection-detail-page.html',
  styleUrl: './collection-detail-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Component-scoped, not route-scoped: see app.routes.ts's own comment on why
  // route-level `providers` looks like per-activation lifetime and is not.
  providers: [CollectionDetailStore, QuotesStore],
  imports: [
    RouterLink,
    PageHeader,
    Button,
    Badge,
    Loader,
    EmptyState,
    ErrorState,
    ConfirmDialog,
    CollectionQuoteRow,
    AddQuoteDialog,
  ],
})
export class CollectionDetailPage implements OnInit {
  private readonly route = inject(ActivatedRoute);

  protected readonly store = inject(CollectionDetailStore);
  protected readonly quotesStore = inject(QuotesStore);

  protected readonly isPickerOpen = signal(false);
  protected readonly pendingRemoval = signal<CollectionQuote | null>(null);

  /**
   * The one derivation this page owns. A Set lookup rather than
   * `quotes.some(...)` per candidate: at 48 candidates against 50 members that is
   * 2400 comparisons per change-detection pass, for no reason.
   */
  protected readonly candidates = computed<readonly Quote[]>(() => {
    const alreadyIn = this.store.memberQuoteIds();
    return this.quotesStore.items().filter((quote) => !alreadyIn.has(quote.id));
  });

  ngOnInit(): void {
    // The id comes off the route as a string; the API takes an int. Parsed once,
    // here, rather than passed around as a string that everything downstream has
    // to convert.
    const id = Number(this.route.snapshot.paramMap.get('id'));

    if (Number.isFinite(id) && id > 0) {
      this.collectionId = id;
      void this.store.load(id);
    } else {
      // Not a positive integer -- "not-a-number", "-1", "1.5". No request to
      // make, so the store needs telling directly: see markInvalidId's own
      // comment for what silently doing nothing here used to render instead.
      this.store.markInvalidId();
    }

    // Loads the candidate pool. setSize also fetches, so this is one request
    // rather than a size change followed by a separate load.
    void this.quotesStore.setSize(PICKER_PAGE_SIZE);
  }

  private collectionId = 0;

  protected reload(): void {
    void this.store.load(this.collectionId);
  }

  protected openPicker(): void {
    this.store.dismissActionError();
    this.isPickerOpen.set(true);
  }

  protected closePicker(): void {
    this.isPickerOpen.set(false);
    this.quotesStore.setSearch('');
  }

  protected async addQuote(quote: Quote): Promise<void> {
    this.addingQuoteId.set(quote.id);

    const added = await this.store.addQuote(quote.id);

    this.addingQuoteId.set(null);

    // The dialog stays open on success so several quotes can be added in a row --
    // and stays open on failure so the reason is visible next to what caused it.
    if (added && this.store.isFull()) {
      this.closePicker();
    }
  }

  protected async addQuotes(quotes: readonly Quote[]): Promise<void> {
    for (const quote of quotes) {
      await this.addQuote(quote);

      if (this.store.isFull()) {
        break;
      }
    }
  }

  protected readonly addingQuoteId = signal<number | null>(null);

  protected confirmRemoval(quote: CollectionQuote): void {
    this.pendingRemoval.set(quote);
  }

  protected cancelRemoval(): void {
    this.pendingRemoval.set(null);
  }

  protected async removalConfirmed(): Promise<void> {
    const quote = this.pendingRemoval();

    if (!quote) {
      return;
    }

    await this.store.removeQuote(quote.quoteId);
    this.pendingRemoval.set(null);
  }
}
