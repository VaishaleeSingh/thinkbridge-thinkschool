import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CollectionListItem } from '../../../core/models/collection';
import { CollectionsApi } from '../../../core/services/collections-api';
import { CollectionPicker } from './collection-picker';

const COLLECTIONS: readonly CollectionListItem[] = [
  { id: 1, name: 'Morning reads', quoteCount: 3, lastAddedAt: null },
  { id: 2, name: 'Favourites', quoteCount: 49, lastAddedAt: '2026-01-01T00:00:00Z' },
];

describe('CollectionPicker', () => {
  let picker: CollectionPicker;
  let api: { list: ReturnType<typeof vi.fn>; addItem: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = {
      list: vi.fn().mockResolvedValue(COLLECTIONS),
      addItem: vi.fn().mockResolvedValue(undefined),
    };

    TestBed.configureTestingModule({
      providers: [CollectionPicker, { provide: CollectionsApi, useValue: api }],
    });

    picker = TestBed.inject(CollectionPicker);
  });

  it('opens a quote menu and lazily loads the collections list exactly once', async () => {
    expect(picker.collections()).toBeNull();

    await picker.toggle(100001);
    expect(picker.isOpenFor(100001)).toBe(true);
    expect(picker.collections()).toEqual(COLLECTIONS);
    expect(api.list).toHaveBeenCalledTimes(1);

    // Opening a second quote's menu must not re-fetch: one instance, one list.
    await picker.toggle(100002);
    expect(picker.isOpenFor(100001)).toBe(false);
    expect(picker.isOpenFor(100002)).toBe(true);
    expect(api.list).toHaveBeenCalledTimes(1);
  });

  it('closes on a second toggle of the same quote', async () => {
    await picker.toggle(100001);
    expect(picker.isOpenFor(100001)).toBe(true);

    await picker.toggle(100001);
    expect(picker.isOpenFor(100001)).toBe(false);
  });

  it('only one quote menu is open at a time', async () => {
    await picker.toggle(100001);
    await picker.toggle(100002);

    expect(picker.isOpenFor(100001)).toBe(false);
    expect(picker.isOpenFor(100002)).toBe(true);
  });

  it('a successful add patches the local quoteCount instead of re-fetching', async () => {
    await picker.toggle(100001);

    const ok = await picker.addTo(100001, 1);

    expect(ok).toBe(true);
    expect(api.addItem).toHaveBeenCalledWith(1, { quoteId: 100001 });
    expect(api.list).toHaveBeenCalledTimes(1); // still just the one load, no re-fetch
    expect(picker.collections()?.find((c) => c.id === 1)?.quoteCount).toBe(4);
  });

  it('isAdding is true only for the specific quote/collection pair in flight', async () => {
    let resolveAdd!: () => void;
    api.addItem.mockReturnValueOnce(new Promise<void>((resolve) => (resolveAdd = resolve)));

    await picker.toggle(100001);
    const pending = picker.addTo(100001, 1);

    expect(picker.isAdding(100001, 1)).toBe(true);
    expect(picker.isAdding(100001, 2)).toBe(false);

    resolveAdd();
    await pending;

    expect(picker.isAdding(100001, 1)).toBe(false);
  });

  it('a real 400 (duplicate membership / full collection) surfaces the actual API message, not a generic one', async () => {
    // apiErrorInterceptor (Day 15) has already normalised the raw HTTP error to
    // an ApiFailure by the time CollectionsApi's promise rejects -- this is
    // exactly the shape toApiFailure's own idempotence guard passes through
    // unchanged, so the mock reflects that boundary rather than a raw
    // HttpErrorResponse this service never actually sees.
    api.addItem.mockRejectedValueOnce({
      status: 400,
      message: 'This quote is already in the collection.',
      fieldErrors: { quoteId: ['This quote is already in the collection.'] },
    });

    await picker.toggle(100001);
    const ok = await picker.addTo(100001, 1);

    expect(ok).toBe(false);
    expect(picker.addError()).toEqual({
      status: 400,
      message: 'This quote is already in the collection.',
      fieldErrors: { quoteId: ['This quote is already in the collection.'] },
    });
  });

  it('a failed add does not blank an already-loaded collections list', async () => {
    api.addItem.mockRejectedValueOnce({ status: 500, message: 'The API had a problem handling that request. Please try again.', fieldErrors: {} });

    await picker.toggle(100001);
    await picker.addTo(100001, 1);

    expect(picker.collections()).not.toBeNull();
    expect(picker.addError()).not.toBeNull();
  });
});
