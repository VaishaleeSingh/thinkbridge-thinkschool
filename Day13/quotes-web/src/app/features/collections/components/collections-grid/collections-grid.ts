import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { CollectionListItem } from '../../../../core/models/collection';
import { CollectionCard } from '../collection-card/collection-card';

/** The responsive grid of collection cards. Same column rules as the quotes grid. */
@Component({
  selector: 'app-collections-grid',
  templateUrl: './collections-grid.html',
  styleUrl: './collections-grid.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CollectionCard],
})
export class CollectionsGrid {
  readonly collections = input.required<readonly CollectionListItem[]>();

  /** The id of the collection currently being deleted, or null if none is. */
  readonly deletingId = input<number | null>(null);

  readonly deleteRequested = output<CollectionListItem>();
}
