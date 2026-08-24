import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { Button } from '../button/button';

/**
 * Previous / next paging over an API that returns `{ page, size, total }`.
 *
 * It derives everything it shows -- the page count, whether each button is
 * usable, and the "showing x to y of n" line -- with computed(), from the three
 * numbers it is given. It holds no state of its own: the current page belongs to
 * the feature store that fetched it, and a component that kept its own copy could
 * disagree with the data on screen.
 *
 * Prev/next rather than numbered pages, deliberately: numbered links are worth
 * their complexity when a user needs to jump deep into a large set, which is not
 * this screen (the quotes list is also searchable). This is not a limitation to
 * work around later -- it is a smaller component that does the job.
 */
@Component({
  selector: 'app-pagination',
  templateUrl: './pagination.html',
  styleUrl: './pagination.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Button],
})
export class Pagination {
  readonly page = input.required<number>();
  readonly size = input.required<number>();
  readonly total = input.required<number>();

  /** True while a page is loading, so neither button can be pressed twice. */
  readonly busy = input(false);

  readonly pageChange = output<number>();

  protected readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(this.total() / Math.max(1, this.size()))),
  );

  protected readonly firstOnPage = computed(() =>
    this.total() === 0 ? 0 : (this.page() - 1) * this.size() + 1,
  );

  protected readonly lastOnPage = computed(() => Math.min(this.page() * this.size(), this.total()));

  protected readonly hasPrevious = computed(() => this.page() > 1);
  protected readonly hasNext = computed(() => this.page() < this.totalPages());

  protected goToPrevious(): void {
    if (this.hasPrevious()) {
      this.pageChange.emit(this.page() - 1);
    }
  }

  protected goToNext(): void {
    if (this.hasNext()) {
      this.pageChange.emit(this.page() + 1);
    }
  }
}
