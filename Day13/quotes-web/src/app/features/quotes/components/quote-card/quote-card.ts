import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';

import { Quote, resolveQuoteBackgroundUrl } from '../../../../core/models/quote';
import { API_BASE_URL } from '../../../../core/services/api-base-url';
import { Badge } from '../../../../shared/components/badge/badge';
import { Button } from '../../../../shared/components/button/button';
import { Card } from '../../../../shared/components/card/card';
import { CollectionPicker } from '../../services/collection-picker';

/**
 * One quote. Roughly forty lines of template and nothing else -- which is the
 * point: the page that shows a grid of these reads as a grid of these, not as
 * three hundred lines of markup with a quote somewhere inside it.
 *
 * It takes a Quote and emits an intent. It does not delete anything: the store
 * owns that, so this component works identically on a page that deletes
 * immediately and one that asks first (which is what quotes-page does).
 */
@Component({
  selector: 'app-quote-card',
  templateUrl: './quote-card.html',
  styleUrl: './quote-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Card, Button, Badge, RouterLink],
})
export class QuoteCard {
  private readonly apiBaseUrl = inject(API_BASE_URL);

  /**
   * Provided once on QuotesPage, not here -- see CollectionPicker's own class
   * comment. Every card on the page shares the same instance, which is what
   * makes "only one card's menu open at a time" and "the collections list is
   * fetched once" true without this component doing anything to enforce
   * either.
   */
  protected readonly picker = inject(CollectionPicker);

  /**
   * `app-card`'s host has `overflow: hidden` (rounded corners, clipped
   * background image), so an ordinary `position: absolute` popover as a DOM
   * descendant of it is clipped at the card's edge the moment it extends
   * past it -- confirmed live (screenshot showed the menu text cut off mid
   * word). A native `<dialog>` shown *non-modally* via `.show()` does NOT
   * fix this: only `.showModal()` promotes a dialog to the browser's top
   * layer, and modal brings a backdrop, focus-trapping and Esc-to-close we
   * don't want for a small in-page menu. The element built for exactly this
   * -- top-layer painting (escapes ANY ancestor's overflow/transform/filter
   * clipping) with none of the modal behaviour -- is the Popover API:
   * `popover="auto"` plus `.showPopover()`/`.hidePopover()`. "auto" (not
   * "manual") is deliberate: it gets native light-dismiss (an outside click
   * or Esc closes it) for free, and the browser coordinates that with
   * whichever popover is currently open across cards -- this effect stays
   * the source of truth for OUR state either way, since it re-syncs on the
   * next change-detection pass regardless of who closed it.
   *
   * One consequence of top-layer promotion, verified live and NOT obvious
   * from the spec prose: a promoted element's `position: absolute/fixed`
   * resolves against the viewport (the initial containing block), not
   * against its nearest positioned ancestor -- `.quote__collect`'s
   * `position: relative` stops applying the moment the popover opens. So
   * `bottom: calc(100% + gap); left: 0` in CSS alone placed the menu off
   off-screen (confirmed: a live rect came back at y: -595). The fix is to
   * position it in JS from the toggle button's own `getBoundingClientRect()`
   * each time it opens, in viewport coordinates -- which is exactly what
   * anchoring a top-layer element to its invoker requires either way.
   */
  private readonly collectMenu = viewChild<ElementRef<HTMLElement>>('collectMenu');
  private readonly collectToggle = viewChild<ElementRef<HTMLButtonElement>>('collectToggle');

  /** Matches --space-2 (see _tokens.scss) -- the gap between the toggle button and the menu. */
  private static readonly MENU_GAP_PX = 8;

  constructor() {
    effect(() => {
      const popover = this.collectMenu()?.nativeElement;
      const toggle = this.collectToggle()?.nativeElement;
      if (!popover || !toggle) {
        return;
      }

      const shouldBeOpen = this.picker.isOpenFor(this.quote().id);
      const isOpen = popover.matches(':popover-open');

      if (shouldBeOpen && !isOpen) {
        const buttonRect = toggle.getBoundingClientRect();
        popover.style.left = `${buttonRect.left}px`;
        popover.style.bottom = `${window.innerHeight - buttonRect.top + QuoteCard.MENU_GAP_PX}px`;
        popover.showPopover();
      } else if (!shouldBeOpen && isOpen) {
        popover.hidePopover();
      }
    });
  }

  readonly quote = input.required<Quote>();

  /** Whether this user wrote it -- drives the badge only. See QuoteRow. */
  readonly owned = input(false);

  /** Whether to offer a delete control at all -- see QuotesStore.canDelete. */
  readonly deletable = input(false);

  /** True while THIS quote's delete request is in flight. */
  readonly deleting = input(false);

  readonly openRequested = output<Quote>();
  readonly deleteRequested = output<Quote>();

  /** Thin wrappers over the shared picker, so the template calls a method on
   *  THIS component -- same convention QuotesPage already uses for its own
   *  store -- rather than reaching two levels into an injected service. */
  protected togglePicker(): void {
    void this.picker.toggle(this.quote().id);
  }

  protected addToCollection(collectionId: number): void {
    void this.picker.addTo(this.quote().id, collectionId);
  }

  protected retryLoadCollections(): void {
    void this.picker.retryLoad();
  }

  /** See resolveQuoteBackgroundUrl -- shared with the preview dialog and the detail page. */
  protected backgroundImage(): string {
    return resolveQuoteBackgroundUrl(this.quote().backgroundImageUrl, this.apiBaseUrl);
  }
}
