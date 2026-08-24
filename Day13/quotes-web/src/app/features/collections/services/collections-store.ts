import { Injectable, computed, inject, signal } from '@angular/core';

import { ApiFailure, toApiFailure } from '../../../core/models/api-failure';
import { CollectionListItem } from '../../../core/models/collection';
import { CollectionsApi } from '../../../core/services/collections-api';

/**
 * The "my collections" list.
 *
 * Smaller than QuotesStore for a reason that is the API's, not this client's: GET
 * /api/collections is not paged and returns only the caller's own collections, so
 * there is no page, size or total to track. The store's shape follows the
 * endpoint rather than a house style applied to every screen.
 *
 * Provided by the page component, not root -- same reasoning as QuotesStore, and
 * see the note there about why the route was the wrong place for it.
 */
@Injectable()
export class CollectionsStore {
  private readonly api = inject(CollectionsApi);

  private readonly collections = signal<readonly CollectionListItem[]>([]);
  private readonly loading = signal(false);
  private readonly failure = signal<ApiFailure | null>(null);
  private readonly creating = signal(false);
  private readonly deletingCollectionId = signal<number | null>(null);

  /**
   * A failed create or delete, kept apart from `failure` for the same reason
   * QuotesStore splits them: a failed LOAD means the list on screen cannot be
   * trusted, so it is cleared and replaced by an error. A failed delete means
   * the list is exactly as correct as it was a moment ago -- blanking it and
   * showing "could not load collections" would hide good data over one action
   * that did not happen.
   */
  private readonly mutationFailure = signal<ApiFailure | null>(null);

  readonly items = this.collections.asReadonly();
  readonly isLoading = this.loading.asReadonly();
  readonly error = this.failure.asReadonly();
  readonly isCreating = this.creating.asReadonly();
  readonly deletingId = this.deletingCollectionId.asReadonly();
  readonly actionError = this.mutationFailure.asReadonly();

  /** Sums the counts the API already flattened onto each row -- see its Day-12 read model. */
  readonly totalQuotes = computed(() =>
    this.collections().reduce((running, collection) => running + collection.quoteCount, 0),
  );

  readonly showLoading = computed(() => this.loading() && this.collections().length === 0);
  readonly showError = computed(() => !this.loading() && this.failure() !== null);
  readonly showEmpty = computed(
    () => !this.loading() && this.failure() === null && this.collections().length === 0,
  );

  async load(): Promise<void> {
    this.loading.set(true);
    this.failure.set(null);
    this.mutationFailure.set(null);

    try {
      this.collections.set(await this.api.list());
    } catch (error) {
      this.failure.set(toApiFailure(error));
      this.collections.set([]);
    } finally {
      this.loading.set(false);
    }
  }

  /**
   * Creates a collection, returning the API's field errors (empty on success) so
   * the dialog can show them against the name field.
   */
  async create(name: string): Promise<Readonly<Record<string, readonly string[]>>> {
    this.creating.set(true);

    try {
      await this.api.create({ name });

      // Re-read rather than appending locally: the API assigns the id and the
      // ordering, and a locally appended row would have neither.
      await this.load();
      return {};
    } catch (error) {
      const failure = toApiFailure(error);

      if (Object.keys(failure.fieldErrors).length > 0) {
        return failure.fieldErrors;
      }

      // The API validates the name on the aggregate's constructor, which arrives
      // as a 400 ProblemDetails with a message but no `errors` dictionary -- so a
      // name that is too long lands here rather than in the branch above. It is
      // put on the name field regardless, because that is the field it is about.
      if (failure.status === 400) {
        return { name: [failure.message] };
      }

      // Not `failure`: that signal drives showError, which replaces the WHOLE
      // list with a full-page error state. The list is still correct here --
      // one create attempt failed with something that is not about the name
      // (a 401, a 403, a 500) -- so this is the mutation failure instead. This
      // was the actual bug reported against "New collection": a token that had
      // just expired, or any non-validation failure, closed the dialog as if it
      // had worked (create() still returns {}, the empty-fieldErrors case) and
      // then blanked the entire page behind a generic error, which looked like
      // the button did nothing.
      this.mutationFailure.set(failure);
      return {};
    } finally {
      this.creating.set(false);
    }
  }

  /** Clears a create/delete failure once it has been seen. Mirrors QuotesStore. */
  dismissActionError(): void {
    this.mutationFailure.set(null);
  }

  /** Deletes a collection, then re-reads the list. */
  async remove(id: number): Promise<void> {
    this.deletingCollectionId.set(id);

    try {
      await this.api.remove(id);
      await this.load();
    } catch (error) {
      // Can legitimately 403 if this somehow isn't the caller's own collection
      // -- see the API's ownership check. That must not clear the list.
      this.mutationFailure.set(toApiFailure(error));
    } finally {
      this.deletingCollectionId.set(null);
    }
  }
}
