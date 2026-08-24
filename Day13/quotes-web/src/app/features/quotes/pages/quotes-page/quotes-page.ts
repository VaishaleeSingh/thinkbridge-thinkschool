import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';

import { CreateQuoteRequest, Quote, UpdateQuoteRequest } from '../../../../core/models/quote';
import { Button } from '../../../../shared/components/button/button';
import { ConfirmDialog } from '../../../../shared/components/confirm-dialog/confirm-dialog';
import { EmptyState } from '../../../../shared/components/empty-state/empty-state';
import { ErrorState } from '../../../../shared/components/error-state/error-state';
import { Loader } from '../../../../shared/components/loader/loader';
import { Pagination } from '../../../../shared/components/pagination/pagination';
import { QuoteFormDialog } from '../../components/quote-form-dialog/quote-form-dialog';
import { QuotePreviewDialog } from '../../components/quote-preview-dialog/quote-preview-dialog';
import { QuotesFilterBar } from '../../components/quotes-filter-bar/quotes-filter-bar';
import { QuotesGrid } from '../../components/quotes-grid/quotes-grid';
import { QUOTE_PAGE_SIZES, QuotesStore } from '../../services/quotes-store';

/**
 * The quotes screen.
 *
 * Everything below is composition and intent -- the page holds no list, no
 * counts and no loading flags. All of that is QuotesStore, provided by this
 * route (see app.routes.ts) so it is created on arrival and destroyed on leaving.
 *
 * The only state that lives here is which dialog is open, because that is not a
 * fact about quotes: it belongs to this screen and nothing else needs it.
 */
@Component({
  selector: 'app-quotes-page',
  templateUrl: './quotes-page.html',
  styleUrl: './quotes-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    Button,
    Loader,
    EmptyState,
    ErrorState,
    Pagination,
    ConfirmDialog,
    QuotesFilterBar,
    QuotesGrid,
    QuoteFormDialog,
    QuotePreviewDialog,
  ],
})
export class QuotesPage implements OnInit {
  protected readonly store = inject(QuotesStore);
  protected readonly pageSizes = QUOTE_PAGE_SIZES;

  protected readonly isCreateOpen = signal(false);
  protected readonly createFieldErrors = signal<Readonly<Record<string, readonly string[]>>>({});
  protected readonly editFieldErrors = signal<Readonly<Record<string, readonly string[]>>>({});
  protected readonly editingQuote = signal<Quote | null>(null);
  protected readonly previewQuote = signal<Quote | null>(null);
  protected readonly previewCanEdit = signal(false);

  /** The quote awaiting delete confirmation. Null means the dialog is closed. */
  protected readonly pendingDelete = signal<Quote | null>(null);

  /**
   * ngOnInit rather than the constructor, and rather than an effect: the first
   * load is a one-off, and there is nothing reactive about it. An effect here
   * would be the mistake this exercise warns about -- it would re-run for reasons
   * unrelated to the user asking for data.
   */
  ngOnInit(): void {
    void this.store.load();
  }

  protected openCreate(): void {
    this.editingQuote.set(null);
    this.createFieldErrors.set({});
    this.isCreateOpen.set(true);
  }

  protected closeCreate(): void {
    this.isCreateOpen.set(false);
  }

  protected openPreview(quote: Quote): void {
    this.previewQuote.set(quote);
    this.previewCanEdit.set(this.store.isOwnedByCaller(quote));
  }

  protected closePreview(): void {
    this.previewQuote.set(null);
    this.previewCanEdit.set(false);
  }

  protected openEdit(quote: Quote): void {
    if (!this.store.isOwnedByCaller(quote)) {
      return;
    }

    this.isCreateOpen.set(false);
    this.previewQuote.set(null);
    this.previewCanEdit.set(false);
    this.editFieldErrors.set({});
    this.editingQuote.set(quote);
  }

  protected closeEdit(): void {
    this.editingQuote.set(null);
  }

  protected async createQuote(request: CreateQuoteRequest): Promise<void> {
    const fieldErrors = await this.store.create(request);

    this.createFieldErrors.set(fieldErrors);

    // The dialog stays open when the API rejected the values, so the messages
    // land next to the fields that caused them.
    if (Object.keys(fieldErrors).length === 0) {
      this.isCreateOpen.set(false);
    }
  }

  protected async updateQuote(request: UpdateQuoteRequest): Promise<void> {
    const quote = this.editingQuote();

    if (!quote) {
      return;
    }

    const fieldErrors = await this.store.update(quote.id, request);
    this.editFieldErrors.set(fieldErrors);

    if (Object.keys(fieldErrors).length === 0) {
      this.editingQuote.set(null);
    }
  }

  protected confirmDelete(quote: Quote): void {
    this.pendingDelete.set(quote);
  }

  protected cancelDelete(): void {
    this.pendingDelete.set(null);
  }

  protected async deleteConfirmed(): Promise<void> {
    const quote = this.pendingDelete();

    if (!quote) {
      return;
    }

    await this.store.remove(quote.id);
    this.pendingDelete.set(null);
  }
}
