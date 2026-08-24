import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { PagedResult, Quote } from '../../../core/models/quote';
import { AuthStore } from '../../../core/services/auth-store';
import { QuotesApi } from '../../../core/services/quotes-api';
import { QuotesStore } from './quotes-store';

/**
 * The signal state transitions the Day-13 brief asks to have verified, as tests
 * rather than as a claim:
 *
 *   initial -> loading -> success -> items and computeds update
 *   initial -> loading -> failure -> error state
 *   initial -> loading -> empty response -> empty state
 *
 * Written against the store rather than through the DOM on purpose. These are
 * assertions about state, and a test that drove a browser to check them would be
 * slower and would also be testing the template.
 */
function makeQuote(id: number, overrides: Partial<Quote> = {}): Quote {
  return {
    id,
    author: `Author ${id}`,
    text: `Text ${id}`,
    createdByUserId: '1',
    ...overrides,
  };
}

function makePage(items: readonly Quote[], total = items.length): PagedResult<Quote> {
  return { page: 1, size: 12, total, items };
}

describe('QuotesStore', () => {
  let api: { getPage: ReturnType<typeof vi.fn>; create: ReturnType<typeof vi.fn>; delete: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = { getPage: vi.fn(), create: vi.fn(), delete: vi.fn() };

    TestBed.configureTestingModule({
      providers: [
        QuotesStore,
        { provide: QuotesApi, useValue: api },

        // A stub rather than the real AuthStore: this store only asks it for the
        // signed-in user id, and the real one would pull in HttpClient and
        // sessionStorage for no benefit.
        { provide: AuthStore, useValue: { userId: signal('1') } },
      ],
    });
  });

  it('starts empty, not loading, and with no error', () => {
    const store = TestBed.inject(QuotesStore);

    expect(store.items()).toEqual([]);
    expect(store.isLoading()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.page()).toBe(1);

    // Nothing has been fetched yet, so "empty" is true -- which is why the page
    // renders the loader off showLoading() and not off showEmpty().
    expect(store.showEmpty()).toBe(true);
  });

  it('goes loading -> success and updates every derived value', async () => {
    let resolvePage: (value: PagedResult<Quote>) => void = () => undefined;
    api.getPage.mockReturnValue(
      new Promise<PagedResult<Quote>>((resolve) => {
        resolvePage = resolve;
      }),
    );

    const store = TestBed.inject(QuotesStore);
    const loading = store.load();

    // Mid-flight: the loading state is on and the error state is cleared.
    expect(store.isLoading()).toBe(true);
    expect(store.showLoading()).toBe(true);
    expect(store.showError()).toBe(false);

    resolvePage(makePage([makeQuote(1), makeQuote(2)], 40));
    await loading;

    expect(store.isLoading()).toBe(false);
    expect(store.items()).toHaveLength(2);
    expect(store.total()).toBe(40);

    // The computeds followed without anything setting them.
    expect(store.showLoading()).toBe(false);
    expect(store.showEmpty()).toBe(false);
    expect(store.rows()).toHaveLength(2);
    expect(store.rows()[0].deletable).toBe(true);
    expect(store.rows()[0].owned).toBe(true);
  });

  it('goes loading -> failure and surfaces the API message', async () => {
    api.getPage.mockRejectedValue(
      new HttpErrorResponse({ status: 500, error: { detail: 'The database is on fire.' } }),
    );

    const store = TestBed.inject(QuotesStore);
    await store.load();

    expect(store.showError()).toBe(true);
    expect(store.error()?.message).toBe('The database is on fire.');

    // Rows are cleared: leaving stale rows under an error claims data that may no
    // longer be true.
    expect(store.items()).toEqual([]);
  });

  it('goes loading -> empty response -> empty state', async () => {
    api.getPage.mockResolvedValue(makePage([], 0));

    const store = TestBed.inject(QuotesStore);
    await store.load();

    expect(store.showEmpty()).toBe(true);
    expect(store.showError()).toBe(false);
    expect(store.showLoading()).toBe(false);
  });

  it('filters the fetched page client-side without another request', async () => {
    api.getPage.mockResolvedValue(
      makePage([makeQuote(1, { author: 'Seneca' }), makeQuote(2, { author: 'Marcus' })]),
    );

    const store = TestBed.inject(QuotesStore);
    await store.load();

    store.setSearch('seneca');

    expect(store.matchCount()).toBe(1);
    expect(store.items()[0].author).toBe('Seneca');
    expect(store.isFiltering()).toBe(true);

    // The point of the assertion: one call, from load(). Filtering does not page.
    expect(api.getPage).toHaveBeenCalledTimes(1);
  });

  it('reports no-matches separately from empty, so the two read differently', async () => {
    api.getPage.mockResolvedValue(makePage([makeQuote(1, { author: 'Seneca' })]));

    const store = TestBed.inject(QuotesStore);
    await store.load();
    store.setSearch('nothing matches this');

    expect(store.showNoMatches()).toBe(true);
    expect(store.showEmpty()).toBe(false);
  });

  it('resets to page 1 when the page size changes', async () => {
    api.getPage.mockResolvedValue(makePage([makeQuote(1)], 100));

    const store = TestBed.inject(QuotesStore);
    await store.goToPage(3);
    expect(store.page()).toBe(3);

    await store.setSize(48);

    // Page 3 of 12-per-page does not exist at 48 per page; asking for it would
    // return an empty list that looks like a bug.
    expect(store.page()).toBe(1);
    expect(api.getPage).toHaveBeenLastCalledWith(1, 48);
  });

  it('returns field errors from a rejected create instead of throwing', async () => {
    api.getPage.mockResolvedValue(makePage([]));
    api.create.mockRejectedValue(
      new HttpErrorResponse({
        status: 400,
        error: { errors: { author: ['Author is required.'] } },
      }),
    );

    const store = TestBed.inject(QuotesStore);
    const fieldErrors = await store.create({ author: '', text: 'x' });

    expect(fieldErrors['author']).toEqual(['Author is required.']);

    // A validation failure is not a page failure: the list must not show an
    // error state because a form was wrong.
    expect(store.showError()).toBe(false);
  });

  it('only offers delete for quotes the API would allow', () => {
    const store = TestBed.inject(QuotesStore);

    expect(store.canDelete(makeQuote(1, { createdByUserId: '1' }))).toBe(true);
    expect(store.canDelete(makeQuote(2, { createdByUserId: '99' }))).toBe(false);

    // Null owner: the API's MustOwnQuoteHandler treats it as "no ownership rule
    // applies" rather than as "nobody may touch this".
    expect(store.canDelete(makeQuote(3, { createdByUserId: null }))).toBe(true);
  });

  it('separates "may delete" from "wrote it"', () => {
    const store = TestBed.inject(QuotesStore);
    const unowned = makeQuote(1, { createdByUserId: null });

    // Deletable, but not authored by this user -- so it gets the control and not
    // the "yours" badge. Conflating the two labels every legacy quote as mine.
    expect(store.canDelete(unowned)).toBe(true);
    expect(store.isOwnedByCaller(unowned)).toBe(false);

    expect(store.isOwnedByCaller(makeQuote(2, { createdByUserId: '1' }))).toBe(true);
  });
});
