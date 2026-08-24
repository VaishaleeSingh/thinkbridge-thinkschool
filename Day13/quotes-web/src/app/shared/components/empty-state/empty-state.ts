import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The "there is nothing here" state.
 *
 * A separate, deliberate state -- not an empty list. An empty grid renders as
 * blank space, which is indistinguishable from a page that failed to load and
 * says nothing about what to do next. Every list in this application shows this
 * instead when its response is legitimately empty.
 *
 * `title` should describe the situation and the projected content should offer
 * the way out of it (see the usages in quotes-page and collections-page, which
 * project the same "New quote" / "New collection" button the header has).
 */
@Component({
  selector: 'app-empty-state',
  templateUrl: './empty-state.html',
  styleUrl: './empty-state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EmptyState {
  readonly title = input.required<string>();
  readonly description = input('');
}
