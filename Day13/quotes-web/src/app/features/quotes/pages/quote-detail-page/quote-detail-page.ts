import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { Quote, resolveQuoteBackgroundUrl } from '../../../../core/models/quote';
import { API_BASE_URL } from '../../../../core/services/api-base-url';
import { EmptyState } from '../../../../shared/components/empty-state/empty-state';
import { ErrorState } from '../../../../shared/components/error-state/error-state';
import { Loader } from '../../../../shared/components/loader/loader';
import { PageHeader } from '../../../../shared/components/page-header/page-header';
import { QuoteDetailStore } from '../../services/quote-detail-store';
import { QuotesStore } from '../../services/quotes-store';

/**
 * The `:id` segment as a quote id, or null when it is not one.
 *
 * Digits only, no leading zero, and inside the range JavaScript holds exactly.
 * `Number(raw)` on its own is far too generous for a URL: it accepts '0x10' as
 * 16, '1e3' as 1000 and ' 5 ' as 5, so /quotes/0x10 would fetch and render quote
 * 16 under an address that does not name it -- which contradicts the premise this
 * page is built on, that the address bar is the question being asked. Rejecting
 * '007' as well is deliberate: one quote, one URL.
 */
function parseQuoteId(raw: string): number | null {
  if (!/^[1-9][0-9]*$/.test(raw)) {
    return null;
  }

  const id = Number(raw);

  return Number.isSafeInteger(id) ? id : null;
}

/**
 * One quote, open.
 *
 * WHY THE STORES ARE PROVIDED HERE AND NOT ON THE ROUTE. Everything this screen
 * knows is about one specific quote, so the state has to be scoped to a viewing
 * of that quote and nothing wider. `providers` on this component ties the two
 * stores to the component instance: Angular creates them when it creates the
 * component and destroys them with it, which is a guarantee of the component
 * lifecycle rather than of how the Router happens to manage the environment
 * injectors it builds from `Route.providers`. Put them on the route and the
 * store's lifetime becomes the router's business -- a cached route injector
 * hands the next quote you open the previous quote's loaded data, its error, or
 * its in-flight flag, and the bug shows up as a flash of the wrong quote.
 *
 * The one case where this component IS reused is a parameter change --
 * /quotes/1 to /quotes/2 is the same route config, so Angular keeps the instance
 * and its providers. That is deliberate and handled: the subscription below
 * re-loads on every parameter, and QuoteDetailStore's token guard is what makes
 * an overlapping pair of requests land in the right order (see load() there).
 *
 * QuotesStore is reused here, not reimplemented, for the "more quotes" list --
 * the same reuse the collection detail page makes for its picker. It is also the
 * reason the list and the detail can be in flight at the same time on this
 * screen, and the reason a user can click straight from one quote to the next
 * before the first has answered.
 */
@Component({
  selector: 'app-quote-detail-page',
  templateUrl: './quote-detail-page.html',
  styleUrl: './quote-detail-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [QuoteDetailStore, QuotesStore],
  imports: [RouterLink, PageHeader, Loader, ErrorState, EmptyState],
})
export class QuoteDetailPage {
  private readonly route = inject(ActivatedRoute);
  private readonly apiBaseUrl = inject(API_BASE_URL);

  protected readonly store = inject(QuoteDetailStore);
  protected readonly quotesStore = inject(QuotesStore);

  /**
   * The `:id` segment exactly as it appeared in the URL, kept so the "no such
   * quote" message can quote it back.
   */
  protected readonly routeId = signal('');

  /**
   * True when that segment is not a quote id at all (/quotes/abc, /quotes/0x10).
   * No request is made for it: the API would answer 400 or 404 to a question
   * whose answer is already known, and a spinner meanwhile would suggest
   * otherwise. See parseQuoteId above for what counts.
   */
  protected readonly malformedId = signal(false);

  /**
   * A malformed id and a 404 are one outcome for the reader -- there is no such
   * quote -- so they share a branch in the template and differ only in wording.
   */
  protected readonly showNoSuchQuote = computed(
    () => this.malformedId() || this.store.showMissing(),
  );

  protected readonly noSuchQuoteMessage = computed(() =>
    this.malformedId()
      ? `“${this.routeId()}” is not a quote id, so there is nothing to open here.`
      : `Quote ${this.store.selectedId()} is not in the library any more. It may have been deleted since you last saw it.`,
  );

  /** The rest of the fetched page, minus the quote already on this screen. */
  protected readonly otherQuotes = computed<readonly Quote[]>(() =>
    this.quotesStore.items().filter((quote) => quote.id !== this.store.selectedId()),
  );

  protected readonly backgroundImage = computed(() => {
    const quote = this.store.quote();

    return quote ? resolveQuoteBackgroundUrl(quote.backgroundImageUrl, this.apiBaseUrl) : '';
  });

  constructor() {
    // paramMap and not the snapshot: Angular reuses this component when only the
    // id changes, so a snapshot read once in ngOnInit would leave the page
    // showing the quote it was first opened with while the URL said another.
    // takeUntilDestroyed unsubscribes with the component.
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const raw = params.get('id') ?? '';
      const id = parseQuoteId(raw);

      this.routeId.set(raw);
      this.malformedId.set(id === null);

      if (id !== null) {
        void this.store.load(id);
        return;
      }

      // The address no longer names a quote, so neither does this page. Told to
      // the store rather than merely hidden by the template: `selectedId` is read
      // by otherQuotes() below, and a store still naming the quote we came from
      // would leave that quote missing from "More quotes" on the page saying
      // there is no such quote.
      this.store.clear();
    });

    // Fetched once, in the constructor rather than per parameter: the "more
    // quotes" list is context for this screen as a whole, and a change of :id
    // does not make it stale.
    void this.quotesStore.load();
  }

  protected reload(): void {
    void this.store.reload();
  }
}
