import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CollectionListItem } from '../../../core/models/collection';
import { CollectionsApi } from '../../../core/services/collections-api';
import { CollectionsStore } from './collections-store';

/**
 * These tests exist because of one real bug: a collection create that failed
 * with anything other than a field-validation error (a 401, a 403, a 500 -- the
 * kind of failure a user actually hit against the real API) was landing on
 * `failure`, the LOAD-error signal, instead of `mutationFailure`. The visible
 * effect was the "New collection" dialog closing as if it had worked (create()
 * still returned {}, its empty-fieldErrors case) while the whole list behind it
 * was replaced by a full-page error state -- which reads exactly like "the
 * button does nothing." The `create()` tests below pin the fix: showError must
 * stay false and actionError must carry the failure instead.
 */
function makeItem(id: number, overrides: Partial<CollectionListItem> = {}): CollectionListItem {
  return {
    id,
    name: `Collection ${id}`,
    quoteCount: 0,
    lastAddedAt: null,
    ...overrides,
  };
}

describe('CollectionsStore', () => {
  let api: { list: ReturnType<typeof vi.fn>; create: ReturnType<typeof vi.fn>; remove: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = { list: vi.fn(), create: vi.fn(), remove: vi.fn() };

    TestBed.configureTestingModule({
      providers: [CollectionsStore, { provide: CollectionsApi, useValue: api }],
    });
  });

  it('starts empty, not loading, and with no error', () => {
    const store = TestBed.inject(CollectionsStore);

    expect(store.items()).toEqual([]);
    expect(store.isLoading()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.showEmpty()).toBe(true);
  });

  it('goes loading -> success and updates totalQuotes', async () => {
    api.list.mockResolvedValue([makeItem(1, { quoteCount: 3 }), makeItem(2, { quoteCount: 5 })]);

    const store = TestBed.inject(CollectionsStore);
    await store.load();

    expect(store.items()).toHaveLength(2);
    expect(store.totalQuotes()).toBe(8);
    expect(store.showEmpty()).toBe(false);
  });

  it('goes loading -> failure and surfaces the API message', async () => {
    api.list.mockRejectedValue(
      new HttpErrorResponse({ status: 500, error: { detail: 'The database is on fire.' } }),
    );

    const store = TestBed.inject(CollectionsStore);
    await store.load();

    expect(store.showError()).toBe(true);
    expect(store.error()?.message).toBe('The database is on fire.');
    expect(store.items()).toEqual([]);
  });

  it('returns field errors from a rejected create instead of throwing', async () => {
    api.list.mockResolvedValue([]);
    api.create.mockRejectedValue(
      new HttpErrorResponse({ status: 400, error: { errors: { name: ['Name is required.'] } } }),
    );

    const store = TestBed.inject(CollectionsStore);
    const fieldErrors = await store.create('');

    expect(fieldErrors['name']).toEqual(['Name is required.']);
    expect(store.showError()).toBe(false);
  });

  it('puts a bare 400 (the aggregate constructor\'s ArgumentException) on the name field', async () => {
    api.list.mockResolvedValue([]);
    api.create.mockRejectedValue(
      new HttpErrorResponse({
        status: 400,
        error: { title: 'One or more invariants were violated.', detail: 'Collection name must be between 3 and 80 characters.' },
      }),
    );

    const store = TestBed.inject(CollectionsStore);
    const fieldErrors = await store.create('ab');

    expect(fieldErrors['name']).toEqual(['Collection name must be between 3 and 80 characters.']);
    expect(store.showError()).toBe(false);
  });

  it('THE BUG: a non-validation create failure must not blank the list behind showError', async () => {
    api.list.mockResolvedValueOnce([makeItem(1)]);

    const store = TestBed.inject(CollectionsStore);
    await store.load();
    expect(store.items()).toHaveLength(1);

    api.create.mockRejectedValue(new HttpErrorResponse({ status: 401, error: null }));

    const fieldErrors = await store.create('Stoics');

    // The dialog reads an empty fieldErrors object as success and closes --
    // that part of the contract is unchanged and correct for the 400/validation
    // cases. What must NOT happen is the list disappearing behind a full-page
    // error for a failure that has nothing to do with the list being wrong.
    expect(fieldErrors).toEqual({});
    expect(store.showError()).toBe(false);
    expect(store.items()).toHaveLength(1);

    // The failure is not silently dropped either -- it surfaces as an action
    // error, same channel QuotesStore uses for a failed create/delete.
    expect(store.actionError()?.status).toBe(401);
  });

  it('dismissActionError clears a create/delete failure', async () => {
    api.list.mockResolvedValue([]);
    api.create.mockRejectedValue(new HttpErrorResponse({ status: 500, error: null }));

    const store = TestBed.inject(CollectionsStore);
    await store.create('Stoics');
    expect(store.actionError()).not.toBeNull();

    store.dismissActionError();
    expect(store.actionError()).toBeNull();
  });

  it('deletes a collection, then re-reads the list', async () => {
    api.list.mockResolvedValueOnce([makeItem(1), makeItem(2)]).mockResolvedValueOnce([makeItem(2)]);
    api.remove.mockResolvedValue(undefined);

    const store = TestBed.inject(CollectionsStore);
    await store.load();
    expect(store.items()).toHaveLength(2);

    await store.remove(1);

    expect(api.remove).toHaveBeenCalledWith(1);
    expect(store.items()).toHaveLength(1);
    expect(store.deletingId()).toBeNull();
  });

  it('a failed delete (403 -- not the owner) leaves the list exactly as it was', async () => {
    api.list.mockResolvedValue([makeItem(1), makeItem(2)]);
    api.remove.mockRejectedValue(new HttpErrorResponse({ status: 403, error: null }));

    const store = TestBed.inject(CollectionsStore);
    await store.load();
    await store.remove(1);

    expect(store.items()).toHaveLength(2);
    expect(store.showError()).toBe(false);
    expect(store.actionError()?.status).toBe(403);
    expect(store.deletingId()).toBeNull();
  });
});
