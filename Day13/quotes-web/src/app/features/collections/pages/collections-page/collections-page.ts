import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';

import { Button } from '../../../../shared/components/button/button';
import { ConfirmDialog } from '../../../../shared/components/confirm-dialog/confirm-dialog';
import { EmptyState } from '../../../../shared/components/empty-state/empty-state';
import { ErrorState } from '../../../../shared/components/error-state/error-state';
import { Loader } from '../../../../shared/components/loader/loader';
import { PageHeader } from '../../../../shared/components/page-header/page-header';
import { CollectionFormDialog } from '../../components/collection-form-dialog/collection-form-dialog';
import { CollectionsGrid } from '../../components/collections-grid/collections-grid';
import { COLLECTION_LIMITS, CollectionListItem } from '../../../../core/models/collection';
import { CollectionsStore } from '../../services/collections-store';

/**
 * The collections list.
 *
 * Same shape as QuotesPage on purpose: header, states, grid, dialog. Two screens
 * that behave the same way should read the same way -- and both get their
 * loading, empty and error rendering from the same three shared components rather
 * than from two hand-written versions that drift.
 */
@Component({
  selector: 'app-collections-page',
  templateUrl: './collections-page.html',
  styleUrl: './collections-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,

  // Created and destroyed with this page -- see the note in quotes-page.ts for
  // why this is not on the route and not in root.
  providers: [CollectionsStore],
  imports: [
    PageHeader,
    Button,
    Loader,
    EmptyState,
    ErrorState,
    ConfirmDialog,
    CollectionsGrid,
    CollectionFormDialog,
  ],
})
export class CollectionsPage implements OnInit {
  protected readonly store = inject(CollectionsStore);

  /**
   * Built here rather than as an expression in the template, because it has to
   * count and pluralise. Inline, it read "1 collections" -- and the browser check
   * asserted that exact string, so the wrong wording was locked in by a test.
   */
  protected readonly subtitle = computed(() => {
    if (this.store.showEmpty()) {
      return `Collections group quotes. Each one holds up to ${COLLECTION_LIMITS.maxItems}.`;
    }

    const collections = this.store.items().length;
    const quotes = this.store.totalQuotes();

    return `${count(collections, 'collection')}, ${count(quotes, 'quote')} in total.`;
  });

  protected readonly isCreateOpen = signal(false);
  protected readonly createFieldErrors = signal<Readonly<Record<string, readonly string[]>>>({});

  /** The collection awaiting delete confirmation. Null means the dialog is closed. */
  protected readonly pendingDelete = signal<CollectionListItem | null>(null);

  ngOnInit(): void {
    void this.store.load();
  }

  protected openCreate(): void {
    this.createFieldErrors.set({});

    // A failure from the last attempt should not still be on screen while the
    // user makes the next one -- see quotes-page.ts for the same call.
    this.store.dismissActionError();
    this.isCreateOpen.set(true);
  }

  protected closeCreate(): void {
    this.isCreateOpen.set(false);
  }

  protected async createCollection(name: string): Promise<void> {
    const fieldErrors = await this.store.create(name);

    this.createFieldErrors.set(fieldErrors);

    if (Object.keys(fieldErrors).length === 0) {
      this.isCreateOpen.set(false);
    }
  }

  protected confirmDelete(collection: CollectionListItem): void {
    this.store.dismissActionError();
    this.pendingDelete.set(collection);
  }

  protected cancelDelete(): void {
    this.pendingDelete.set(null);
  }

  protected async deleteConfirmed(): Promise<void> {
    const collection = this.pendingDelete();

    if (!collection) {
      return;
    }

    await this.store.remove(collection.id);
    this.pendingDelete.set(null);
  }
}

/** "1 collection", "3 collections" -- the difference a template cannot express. */
function count(value: number, noun: string): string {
  return `${value} ${noun}${value === 1 ? '' : 's'}`;
}
