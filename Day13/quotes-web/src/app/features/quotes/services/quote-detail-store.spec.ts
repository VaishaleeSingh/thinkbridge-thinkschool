import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { DEFAULT_QUOTE_BACKGROUND_URL, Quote } from '../../../core/models/quote';
import { QuotesApi } from '../../../core/services/quotes-api';
import { QuoteDetailStore } from './quote-detail-store';

/**
 * The detail store's state transitions, including the one this feature exists to
 * get right: two overlapping requests.
 *
 *   initial               -> empty state, nothing requested
 *   loading -> success    -> the quote renders
 *   loading -> 500 / 0    -> a failure that can be retried
 *   loading -> 404        -> a MISSING quote, which is a different state
 *   two overlapping loads -> only the newest one may write
 *   clear()               -> nothing viewed, and any request on the wire retired
 *
 * The interleave tests below hold both promises open at once and settle them in
 * the wrong order deliberately. A test that awaited the first load before
 * starting the second would pass against a store with no guard at all, and so
 * would not be a test for this.
 */
function makeQuote(id: number, overrides: Partial<Quote> = {}): Quote {
  return {
    id,
    author: `Author ${id}`,
    text: `Text ${id}`,
    backgroundImageUrl: DEFAULT_QUOTE_BACKGROUND_URL,
    createdByUserId: '1',
    ...overrides,
  };
}

interface Pending {
  readonly promise: Promise<Quote>;
  resolveWith(quote: Quote): void;
  rejectWith(error: unknown): void;
}

/** A request the test decides the outcome of, later. */
function pending(): Pending {
  let resolveWith: (quote: Quote) => void = () => undefined;
  let rejectWith: (error: unknown) => void = () => undefined;

  const promise = new Promise<Quote>((resolve, reject) => {
    resolveWith = resolve;
    rejectWith = reject;
  });

  return { promise, resolveWith, rejectWith };
}

describe('QuoteDetailStore', () => {
  let api: { getById: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    api = { getById: vi.fn() };

    TestBed.configureTestingModule({
      providers: [QuoteDetailStore, { provide: QuotesApi, useValue: api }],
    });
  });

  it('starts with nothing requested, not loading, and no error', () => {
    const store = TestBed.inject(QuoteDetailStore);

    expect(store.quote()).toBeNull();
    expect(store.selectedId()).toBeNull();
    expect(store.isLoading()).toBe(false);
    expect(store.error()).toBeNull();

    // The empty state, and not the loading one: no request has been made, and a
    // spinner would claim otherwise.
    expect(store.showEmpty()).toBe(true);
    expect(store.showLoading()).toBe(false);
    expect(store.showQuote()).toBe(false);
  });

  it('goes loading -> loaded and updates every derived value', async () => {
    const request = pending();
    api.getById.mockReturnValue(request.promise);

    const store = TestBed.inject(QuoteDetailStore);
    const loading = store.load(7);

    expect(store.isLoading()).toBe(true);
    expect(store.showLoading()).toBe(true);
    expect(store.selectedId()).toBe(7);
    expect(store.showEmpty()).toBe(false);
    expect(store.showError()).toBe(false);

    request.resolveWith(makeQuote(7, { author: 'Seneca' }));
    await loading;

    expect(store.isLoading()).toBe(false);
    expect(store.quote()?.author).toBe('Seneca');
    expect(store.showQuote()).toBe(true);
    expect(store.showLoading()).toBe(false);
    expect(store.showEmpty()).toBe(false);
    expect(api.getById).toHaveBeenCalledWith(7);
  });

  it('surfaces a server failure as retryable, with the API’s own message', async () => {
    api.getById.mockRejectedValue(
      new HttpErrorResponse({ status: 500, error: { detail: 'The database is on fire.' } }),
    );

    const store = TestBed.inject(QuoteDetailStore);
    await store.load(7);

    expect(store.showError()).toBe(true);
    expect(store.error()?.message).toBe('The database is on fire.');

    // Not the missing state: nobody said the quote is gone, only that the API
    // could not answer.
    expect(store.isMissing()).toBe(false);
    expect(store.showMissing()).toBe(false);
    expect(store.quote()).toBeNull();
  });

  it('reports an unreachable API separately from a missing quote', async () => {
    api.getById.mockRejectedValue(new HttpErrorResponse({ status: 0 }));

    const store = TestBed.inject(QuoteDetailStore);
    await store.load(7);

    expect(store.isUnreachable()).toBe(true);
    expect(store.showError()).toBe(true);

    // The distinction the brief asks for: "could not be reached" is not "no
    // longer exists", and the page words them differently.
    expect(store.showMissing()).toBe(false);
  });

  it('treats a 404 as a missing quote rather than as a failure to retry', async () => {
    api.getById.mockRejectedValue(new HttpErrorResponse({ status: 404 }));

    const store = TestBed.inject(QuoteDetailStore);
    await store.load(404);

    expect(store.isMissing()).toBe(true);
    expect(store.showMissing()).toBe(true);

    // showError stays false so the page cannot offer a retry that cannot work.
    expect(store.showError()).toBe(false);
    expect(store.showQuote()).toBe(false);
    expect(store.selectedId()).toBe(404);
  });

  it('retries the id it was last asked for, and does nothing before there is one', async () => {
    const store = TestBed.inject(QuoteDetailStore);

    await store.reload();
    expect(api.getById).not.toHaveBeenCalled();

    api.getById.mockResolvedValue(makeQuote(3));
    await store.load(3);
    await store.reload();

    expect(api.getById).toHaveBeenCalledTimes(2);
    expect(api.getById).toHaveBeenLastCalledWith(3);
  });

  it('ignores a late response for a quote that is no longer selected', async () => {
    const first = pending();
    const second = pending();
    api.getById.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

    const store = TestBed.inject(QuoteDetailStore);

    // Both in flight at once -- quote 1 opened, then quote 2 before 1 answered.
    const loadingFirst = store.load(1);
    const loadingSecond = store.load(2);

    expect(store.selectedId()).toBe(2);

    // Quote 1 answers LAST, which is the case the guard exists for.
    second.resolveWith(makeQuote(2, { author: 'Newest' }));
    await loadingSecond;

    expect(store.quote()?.id).toBe(2);

    first.resolveWith(makeQuote(1, { author: 'Stale' }));
    await loadingFirst;

    expect(store.quote()?.id).toBe(2);
    expect(store.quote()?.author).toBe('Newest');
    expect(store.isLoading()).toBe(false);
  });

  it('does not let a late failure report the quote that did load as broken', async () => {
    const first = pending();
    const second = pending();
    api.getById.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

    const store = TestBed.inject(QuoteDetailStore);
    const loadingFirst = store.load(1);
    const loadingSecond = store.load(2);

    second.resolveWith(makeQuote(2));
    await loadingSecond;

    // Quote 1 was deleted while the user was already looking at quote 2. Its 404
    // is true about quote 1 and irrelevant here: rendering it would tell the user
    // that the quote in front of them no longer exists.
    first.rejectWith(new HttpErrorResponse({ status: 404 }));
    await loadingFirst;

    expect(store.error()).toBeNull();
    expect(store.showMissing()).toBe(false);
    expect(store.showQuote()).toBe(true);
    expect(store.quote()?.id).toBe(2);
  });

  it('keeps loading true when a superseded request settles first', async () => {
    const first = pending();
    const second = pending();
    api.getById.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

    const store = TestBed.inject(QuoteDetailStore);
    const loadingFirst = store.load(1);
    const loadingSecond = store.load(2);

    first.resolveWith(makeQuote(1));
    await loadingFirst;

    // The loser's `finally` must not clear the flag: quote 2 is still in flight,
    // and reporting "done" here is what makes a page flash its empty state
    // between two navigations.
    expect(store.isLoading()).toBe(true);
    expect(store.showLoading()).toBe(true);
    expect(store.quote()).toBeNull();

    second.resolveWith(makeQuote(2));
    await loadingSecond;

    expect(store.isLoading()).toBe(false);
    expect(store.quote()?.id).toBe(2);
  });

  it('keeps the newest response when the SAME id is requested twice', async () => {
    const first = pending();
    const second = pending();
    api.getById.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

    const store = TestBed.inject(QuoteDetailStore);

    // The case an "is this the id I asked for?" check cannot handle: both
    // responses are for quote 5, so both would pass an id comparison, and the
    // older one lands last.
    const loadingFirst = store.load(5);
    const loadingSecond = store.load(5);

    second.resolveWith(makeQuote(5, { text: 'Edited a moment ago' }));
    await loadingSecond;

    first.resolveWith(makeQuote(5, { text: 'What it said before the edit' }));
    await loadingFirst;

    // The token is taken per CALL, so the second call still owns the state and
    // the first is discarded even though its id matches.
    expect(store.quote()?.text).toBe('Edited a moment ago');
    expect(store.isLoading()).toBe(false);
  });

  it('clear() puts the store back to nothing being viewed', async () => {
    api.getById.mockResolvedValue(makeQuote(5));

    const store = TestBed.inject(QuoteDetailStore);
    await store.load(5);
    expect(store.selectedId()).toBe(5);

    store.clear();

    // selectedId matters as much as the quote itself: a page that filters a list
    // by "the one already on screen" reads it, so a store still naming quote 5
    // would hide quote 5 from that list while claiming nothing is open.
    expect(store.selectedId()).toBeNull();
    expect(store.quote()).toBeNull();
    expect(store.error()).toBeNull();
    expect(store.isLoading()).toBe(false);
    expect(store.showEmpty()).toBe(true);
    expect(store.showQuote()).toBe(false);
  });

  it('discards a request that was already in flight when clear() happened', async () => {
    const request = pending();
    api.getById.mockReturnValue(request.promise);

    const store = TestBed.inject(QuoteDetailStore);
    const loading = store.load(5);

    store.clear();
    expect(store.isLoading()).toBe(false);

    // Already on the wire, and answering a question the store is no longer
    // asking. Clearing without retiring the token would let this repopulate the
    // store behind a "No such quote" message.
    request.resolveWith(makeQuote(5));
    await loading;

    expect(store.quote()).toBeNull();
    expect(store.selectedId()).toBeNull();
    expect(store.showEmpty()).toBe(true);

    // The same for a failure landing late: it must not report an error about a
    // quote nobody is looking at.
    const failing = pending();
    api.getById.mockReturnValue(failing.promise);
    const loadingAgain = store.load(6);

    store.clear();
    failing.rejectWith(new HttpErrorResponse({ status: 404 }));
    await loadingAgain;

    expect(store.error()).toBeNull();
    expect(store.showMissing()).toBe(false);

    // And the store still works afterwards: the token only ever moves forward.
    api.getById.mockResolvedValue(makeQuote(7));
    await store.load(7);

    expect(store.quote()?.id).toBe(7);
    expect(store.showQuote()).toBe(true);
  });

  it('drops the previous quote when a different one is opened, but not on a re-read', async () => {
    api.getById.mockResolvedValue(makeQuote(1));

    const store = TestBed.inject(QuoteDetailStore);
    await store.load(1);

    const next = pending();
    api.getById.mockReturnValue(next.promise);

    const loadingNext = store.load(2);

    // Quote 1 is gone from the state immediately: leaving it on screen would put
    // its text under a spinner that is fetching quote 2.
    expect(store.quote()).toBeNull();
    expect(store.showLoading()).toBe(true);

    next.resolveWith(makeQuote(2));
    await loadingNext;

    const refresh = pending();
    api.getById.mockReturnValue(refresh.promise);
    const loadingRefresh = store.load(2);

    // A re-read of the same quote keeps it visible instead of blanking content
    // that is still true.
    expect(store.quote()?.id).toBe(2);
    expect(store.showLoading()).toBe(false);
    expect(store.showQuote()).toBe(true);

    refresh.resolveWith(makeQuote(2));
    await loadingRefresh;
  });
});
