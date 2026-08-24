import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';

import { COLLECTION_LIMITS, CollectionListItem } from '../../../../core/models/collection';
import { Badge } from '../../../../shared/components/badge/badge';
import { Button } from '../../../../shared/components/button/button';
import { Card } from '../../../../shared/components/card/card';
import { RelativeTimePipe } from '../../../../shared/pipes/relative-time-pipe';

/**
 * One row of the collections list: name, size, when it last changed, and a
 * delete control.
 *
 * The whole card is not a click target -- the NAME is a router link. That is the
 * deliberate choice described in Card's `interactive` input: a link is focusable,
 * announces itself as a link, opens in a new tab on middle-click, and needs no
 * keydown handler. The card only reacts visually to that link being hovered or
 * focused. Delete is a second, separate control in the footer, same as
 * QuoteCard -- it does not delete anything itself, it emits an intent and lets
 * the page decide whether to confirm first (it does).
 */
@Component({
  selector: 'app-collection-card',
  templateUrl: './collection-card.html',
  styleUrl: './collection-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, Card, Badge, Button, RelativeTimePipe],
})
export class CollectionCard {
  readonly collection = input.required<CollectionListItem>();

  /** True while THIS collection's delete request is in flight. */
  readonly deleting = input(false);

  readonly deleteRequested = output<CollectionListItem>();

  protected readonly maxItems = COLLECTION_LIMITS.maxItems;
}
