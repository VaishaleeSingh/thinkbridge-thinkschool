import { Injectable, computed, inject, signal } from '@angular/core';

import { ApiFailure, toApiFailure } from '../../../core/models/api-failure';
import { COLLECTION_LIMITS, CollectionDetail } from '../../../core/models/collection';
import { CollectionsApi } from '../../../core/services/collections-api';

/**
 * One collection, open.
 *
 * The two mutations here both re-read the detail afterwards instead of patching
 * the local copy. That is not laziness: POST /items returns the API's Collection
 * AGGREGATE, not the detail read model this screen renders -- it has no quote text
 * and no AddedAt -- so there is nothing in the response to patch with. Re-reading
 * gets the shape the screen actually needs, and it is also what makes `quoteCount`
 * and the aggregate's invariants agree with the server rather than with an
 * optimistic guess.
 */
@Injectable()
export class CollectionDetailStore {
  private readonly api = inject(CollectionsApi);

  private readonly detail = signal<CollectionDetail | null>(null);
  private readonly loading = signal(false);
  private readonly failure = signal<ApiFailure | null>(null);
  private readonly adding = signal(false);
  private readonly removingQuoteId = signal<number | null>(null);
  private readonly mutationFailure = signal<ApiFailure | null>(null);

  readonly collection = this.detail.asReadonly();
  readonly isLoading = this.loading.asReadonly();
  readonly error = this.failure.asReadonly();
  readonly isAdding = this.adding.asReadonly();
  readonly removingId = this.removingQuoteId.asReadonly();

  /**
   * Failures from adding or removing, kept apart from `error`. A failed "add"
   * must not blank the collection that is on screen and loaded fine -- it is a
   * message about an action, not about the page.
   */
  readonly actionError = this.mutationFailure.asReadonly();

  readonly quotes = computed(() => this.detail()?.quotes ?? []);
  readonly name = computed(() => this.detail()?.name ?? '');
  readonly quoteCount = computed(() => this.detail()?.quoteCount ?? 0);

  /** Ids already in the collection, so the picker can leave them out. */
  readonly memberQuoteIds = computed(() => new Set(this.quotes().map((quote) => quote.quoteId)));

  /**
   * The aggregate refuses a 51st quote. Knowing that here means the UI can say
   * so and disable the control, instead of offering an action that returns 400.
   */
  readonly isFull = computed(() => this.quoteCount() >= COLLECTION_LIMITS.maxItems);
  readonly remainingSlots = computed(() =>
    Math.max(0, COLLECTION_LIMITS.maxItems - this.quoteCount()),
  );

  readonly showLoading = computed(() => this.loading() && this.detail() === null);
  readonly showError = computed(() => !this.loading() && this.failure() !== null);
  readonly showEmpty = computed(
    () => !this.loading() && this.failure() === null && this.detail() !== null && this.quoteCount() === 0,
  );

  async load(collectionId: number): Promise<void> {
    this.loading.set(true);
    this.failure.set(null);
    this.mutationFailure.set(null);

    try {
      this.detail.set(await this.api.getById(collectionId));
    } catch (error) {
      this.failure.set(toApiFailure(error));
      this.detail.set(null);
    } finally {
      this.loading.set(false);
    }
  }

  async addQuote(quoteId: number): Promise<boolean> {
    const collection = this.detail();

    if (!collection) {
      return false;
    }

    this.adding.set(true);
    this.mutationFailure.set(null);

    try {
      await this.api.addItem(collection.id, { quoteId });
      await this.load(collection.id);
      return true;
    } catch (error) {
      this.mutationFailure.set(toApiFailure(error));
      return false;
    } finally {
      this.adding.set(false);
    }
  }

  async removeQuote(quoteId: number): Promise<void> {
    const collection = this.detail();

    if (!collection) {
      return;
    }

    this.removingQuoteId.set(quoteId);
    this.mutationFailure.set(null);

    try {
      await this.api.removeItem(collection.id, quoteId);
      await this.load(collection.id);
    } catch (error) {
      this.mutationFailure.set(toApiFailure(error));
    } finally {
      this.removingQuoteId.set(null);
    }
  }

  dismissActionError(): void {
    this.mutationFailure.set(null);
  }
}
