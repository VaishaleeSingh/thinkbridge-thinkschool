import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type CardVariant = 'default' | 'outlined' | 'highlighted';
export type CardPadding = 'sm' | 'md' | 'lg';

/**
 * The white surface everything in this application sits on.
 *
 * Configured through inputs rather than copied: the quote card, the collection
 * card, the sign-in panel, the filter bar and the collection-detail rows are all
 * this component with different values. That is what makes "cards should have a
 * softer shadow" a one-file change instead of a six-file change.
 *
 * It knows nothing about quotes, collections, or the API -- it takes no data,
 * only appearance. A shared component that imported a feature's model would stop
 * being shared the moment a second feature needed it.
 */
@Component({
  selector: 'app-card',
  templateUrl: './card.html',
  styleUrl: './card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.card--outlined]': "variant() === 'outlined'",
    '[class.card--highlighted]': "variant() === 'highlighted'",
    '[class.card--pad-sm]': "padding() === 'sm'",
    '[class.card--pad-lg]': "padding() === 'lg'",
    '[class.card--interactive]': 'interactive()',
  },
})
export class Card {
  readonly variant = input<CardVariant>('default');
  readonly padding = input<CardPadding>('md');

  /**
   * Adds hover lift and a pointer cursor -- for a card that CONTAINS a link or
   * a button, not for one that is itself clickable.
   *
   * There is deliberately no `clickable` input that would make the card emit a
   * click: that shape leads straight to a clickable <div>, which is invisible to
   * keyboard and screen-reader users. The pattern used instead is a real <a> or
   * <button> inside the card (see collection-card), which gets focus, Enter and
   * a role for free.
   */
  readonly interactive = input(false);
}
