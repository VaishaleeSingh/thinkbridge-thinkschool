import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

import { EmptyState } from '../../../../shared/components/empty-state/empty-state';

/**
 * The wildcard route.
 *
 * It renders inside the main layout rather than as a bare page, so a mistyped URL
 * still has the navigation on it -- a dead end with no way out is worse than the
 * wrong address. Reuses EmptyState instead of its own markup: "there is nothing
 * here" is the same component whether the cause is an empty list or a bad URL.
 */
@Component({
  selector: 'app-not-found-page',
  templateUrl: './not-found-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, EmptyState],
})
export class NotFoundPage {}
