import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The top of every page: what this screen is, optionally a line about it, and
 * the actions that belong to it.
 *
 * One component so the heading level, the spacing below it, and the way actions
 * wrap on a phone are decided once. It renders an <h1>, which is correct because
 * each route has exactly one of these -- a page with two <h1>s is a page whose
 * outline no longer describes it.
 */
@Component({
  selector: 'app-page-header',
  templateUrl: './page-header.html',
  styleUrl: './page-header.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PageHeader {
  readonly title = input.required<string>();
  readonly subtitle = input('');
}
