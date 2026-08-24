import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl } from '@angular/forms';

import { Quote } from '../../../../core/models/quote';
import { Button } from '../../../../shared/components/button/button';
import { EmptyState } from '../../../../shared/components/empty-state/empty-state';
import { Loader } from '../../../../shared/components/loader/loader';
import { Modal } from '../../../../shared/components/modal/modal';
import { TextField } from '../../../../shared/components/text-field/text-field';

/**
 * Picks a quote to add to the open collection.
 *
 * The candidate list is passed IN rather than fetched here: the page composes it
 * from the quotes the API returned minus the ones already in this collection,
 * which is a fact only the page (holding both stores) knows. That also keeps this
 * component testable with an array and no HTTP.
 *
 * `addingQuoteId` rather than a plain `adding` flag, so the spinner appears on
 * the row that was clicked instead of on every row at once.
 */
@Component({
  selector: 'app-add-quote-dialog',
  templateUrl: './add-quote-dialog.html',
  styleUrl: './add-quote-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Modal, Button, TextField, Loader, EmptyState],
})
export class AddQuoteDialog {
  private readonly destroyRef = inject(DestroyRef);

  readonly open = input(false);
  readonly quotes = input.required<readonly Quote[]>();
  readonly loading = input(false);
  readonly adding = input(false);

  /** How many more this collection can hold, from the aggregate's max of 50. */
  readonly remainingSlots = input(0);

  /** A failed add, shown inside the dialog because that is where the action was. */
  readonly errorMessage = input<string | null>(null);

  readonly searchChanged = output<string>();
  readonly quotesSelected = output<readonly Quote[]>();
  readonly cancelled = output<void>();

  protected readonly searchControl = new FormControl('', { nonNullable: true });
  protected readonly selectedQuoteIds = signal<readonly number[]>([]);

  protected readonly selectedQuotes = computed(() => {
    const ids = new Set(this.selectedQuoteIds());
    return this.quotes().filter((quote) => ids.has(quote.id));
  });

  protected readonly selectedCount = computed(() => this.selectedQuotes().length);

  constructor() {
    this.searchControl.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((term) => this.searchChanged.emit(term));

    // Clearing the filter when the dialog opens also clears it in the store it
    // is bound to, so the picker never opens showing yesterday's search.
    effect(() => {
      if (this.open()) {
        this.searchControl.setValue('');
        this.selectedQuoteIds.set([]);
      }
    });

    effect(() => {
      const validIds = new Set(this.quotes().map((quote) => quote.id));
      this.selectedQuoteIds.update((ids) => ids.filter((id) => validIds.has(id)));
    });
  }

  protected isSelected(quoteId: number): boolean {
    return this.selectedQuoteIds().includes(quoteId);
  }

  protected toggleSelection(quoteId: number, checked: boolean): void {
    this.selectedQuoteIds.update((ids) => {
      if (checked) {
        return ids.includes(quoteId) ? ids : [...ids, quoteId];
      }

      return ids.filter((id) => id !== quoteId);
    });
  }

  protected toggleSelectionFromRow(quoteId: number): void {
    this.toggleSelection(quoteId, !this.isSelected(quoteId));
  }

  protected onCheckboxClick(event: Event): void {
    event.stopPropagation();
  }

  protected addSelectedQuotes(): void {
    const quotes = this.selectedQuotes();

    if (quotes.length > 0) {
      this.quotesSelected.emit(quotes);
      this.selectedQuoteIds.set([]);
    }
  }
}
