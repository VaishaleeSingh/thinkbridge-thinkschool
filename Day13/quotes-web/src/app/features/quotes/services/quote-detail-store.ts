import { Injectable, computed, inject, signal } from '@angular/core';

import { ApiFailure, toApiFailure } from '../../../core/models/api-failure';
import { Quote } from '../../../core/models/quote';
import { QuotesApi } from '../../../core/services/quotes-api';

/**
 * One quote, opened by id from GET /api/quotes/{id}.
 *
 * WHY A SECOND STORE RATHER THAN A FIELD ON QuotesStore: the list and the detail
 * are two requests with two independent outcomes. A 404 here means this quote is
 * gone; it says nothing about the page of quotes that may still be on screen
 * beside it. Folded into one store, every consumer would have to work out which
 * of two `loading` flags and which of two `error` signals applied to it.
 *
 * SIGNAL DISCIPLINE, as in QuotesStore: `signal()` only for the four facts that
 * are pushed in (the fetched quote, the id being viewed, in-flight, failed),
 * `computed()` for every view state derived from them, and no `effect()`
 * anywhere -- loading happens when something the user did asks for it.
 *
 * THE ONE UNOBVIOUS THING is the interleave guard in `load()`. Read that comment
 * before changing anything in there.
 */
@Injectable()
export class QuoteDetailStore {
  private readonly api = inject(QuotesApi);

  // --- Raw state -------------------------------------------------------------
  private readonly detail = signal<Quote | null>(null);
  private readonly requestedId = signal<number | null>(null);
  private readonly loading = signal(false);
  private readonly failure = signal<ApiFailure | null>(null);

  /**
   * Identifies the newest load(). Incremented on entry to every call, and every
   * write of the four signals above is gated on the token still being the newest
   * one when the response lands -- see load().
   *
   * A plain field and not a signal on purpose: nothing renders it, and making it
   * reactive would invite a computed to depend on a value that changes once per
   * request for reasons that are not about what is on screen.
   */
  private requestToken = 0;

  // --- Readonly projections --------------------------------------------------
  readonly quote = this.detail.asReadonly();
  readonly selectedId = this.requestedId.asReadonly();
  readonly isLoading = this.loading.asReadonly();
  readonly error = this.failure.asReadonly();

  /**
   * A 404 is not a failure of the same kind as the rest.
   *
   * Everything else `toApiFailure` produces -- a 0 because the API is not
   * running, a 401 because the session expired, a 500 -- means "we could not
   * find out". A 404 means we did find out: there is no quote with that id. The
   * two need different words and different ways forward (retry versus go back to
   * the list), which is only possible if the store tells them apart rather than
   * handing the page one undifferentiated `error`.
   */
  readonly isMissing = computed(() => this.failure()?.status === 404);

  /** True when the request never reached the API at all. See toApiFailure. */
  readonly isUnreachable = computed(() => this.failure()?.status === 0);

  // --- View states, derived rather than tracked ------------------------------
  readonly showLoading = computed(() => this.loading() && this.detail() === null);
  readonly showMissing = computed(() => !this.loading() && this.isMissing());
  readonly showError = computed(
    () => !this.loading() && this.failure() !== null && !this.isMissing(),
  );
  readonly showQuote = computed(() => this.failure() === null && this.detail() !== null);

  /**
   * Nothing asked for yet -- the state the store is in before the page has read
   * an id off the route. Its own state rather than a variant of "loading",
   * because a page that renders a spinner for it is claiming a request that was
   * never made.
   */
  readonly showEmpty = computed(
    () => !this.loading() && this.failure() === null && this.detail() === null,
  );

  // --- Commands --------------------------------------------------------------

  /**
   * Fetches one quote and, if it is still the one being viewed by the time the
   * response arrives, renders it.
   *
   * THE INTERLEAVE. Two requests can be in flight at once: opening quote A and
   * then quote B before A has answered reuses this store, because /quotes/1 and
   * /quotes/2 are the same route and Angular reuses the component (and therefore
   * its providers) across a parameter change. Promises resolve in whatever order
   * the network hands them back, so without a guard A's late response overwrites
   * B -- the page then shows quote A while the address bar says quote B, and a
   * late 404 for A would report B as deleted.
   *
   * The guard is a monotonic token, NOT a comparison of the returned quote's id
   * against the selected one, and that is the whole point:
   *
   *   - `++this.requestToken` makes every CALL, not every id, uniquely
   *     identifiable. Only the call that still holds the newest token may write.
   *   - So requesting the same id twice in a row -- a retry, a double click on
   *     the same link, or navigating 5 -> 6 -> 5 -- is handled correctly, where
   *     an id comparison is not: both responses carry id 5, both would pass an
   *     id check, and the first one to be issued could land last and overwrite
   *     the newer one with staler data. A token cannot tie, because it is taken
   *     from a counter that only ever moves forward.
   *   - `loading` is cleared under the same guard, so a loser settling cannot
   *     report "finished" while the request whose result will actually render is
   *     still in flight.
   *
   * No cancellation is attempted. Aborting the loser's HTTP request would be a
   * bandwidth optimisation, not a correctness one, and it would need the API
   * layer to hand back a subscription rather than a promise; the guard is what
   * makes the outcome correct either way.
   */
  async load(id: number): Promise<void> {
    const token = ++this.requestToken;

    this.requestedId.set(id);
    this.loading.set(true);
    this.failure.set(null);

    // A different quote was asked for, so the one on screen is no longer an
    // answer to anything: keeping it would leave quote A's text under a spinner
    // that is fetching quote B. A re-read of the SAME quote keeps it, so a
    // refresh does not blank content that is still true.
    if (this.detail()?.id !== id) {
      this.detail.set(null);
    }

    try {
      const quote = await this.api.getById(id);

      if (token !== this.requestToken) {
        return;
      }

      this.detail.set(quote);
    } catch (error) {
      if (token !== this.requestToken) {
        return;
      }

      this.failure.set(toApiFailure(error));

      // Cleared for the same reason QuotesStore clears its rows: a quote left
      // under an error message claims something the API just refused to confirm.
      this.detail.set(null);
    } finally {
      if (token === this.requestToken) {
        this.loading.set(false);
      }
    }
  }

  /**
   * Re-runs the request that failed. Retry belongs here rather than in the page
   * because the page would have to remember the id, which this store already
   * knows.
   */
  async reload(): Promise<void> {
    const id = this.requestedId();

    if (id === null) {
      return;
    }

    await this.load(id);
  }

  /**
   * Nothing is being viewed any more.
   *
   * Called when the address stops naming a quote at all -- /quotes/abc -- where
   * there is no request to make and so nothing that would otherwise reset the
   * state. Without it the store keeps reporting the quote it was last shown:
   * `selectedId` still names it, which is enough to make it disappear from a
   * caller that (correctly) filters a list by "the one already on screen", on the
   * very page saying no such quote exists.
   *
   * The token bump is the important half. A reset that only cleared the four
   * signals would be undone by a request that was already on the wire: it would
   * land afterwards and repopulate the store behind a "No such quote" message.
   * Incrementing the counter retires every in-flight call for the same reason a
   * newer load() does -- they no longer hold the newest token, so their writes
   * are discarded.
   */
  clear(): void {
    this.requestToken++;

    this.detail.set(null);
    this.requestedId.set(null);
    this.failure.set(null);
    this.loading.set(false);
  }
}
