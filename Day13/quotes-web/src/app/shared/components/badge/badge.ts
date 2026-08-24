import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type BadgeTone = 'neutral' | 'primary' | 'success' | 'danger' | 'warning';

/**
 * A small pill of metadata: a quote count, "yours", a collection's fullness.
 *
 * `tone` carries meaning, but never on its own -- every use in this app pairs it
 * with text, because a colour is not readable to someone who cannot distinguish
 * it, and is invisible in a screen reader entirely.
 */
@Component({
  selector: 'app-badge',
  templateUrl: './badge.html',
  styleUrl: './badge.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.badge--primary]': "tone() === 'primary'",
    '[class.badge--success]': "tone() === 'success'",
    '[class.badge--danger]': "tone() === 'danger'",
    '[class.badge--warning]': "tone() === 'warning'",
  },
})
export class Badge {
  readonly tone = input<BadgeTone>('neutral');
}
