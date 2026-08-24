import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type LoaderVariant = 'spinner' | 'skeleton';

/**
 * The loading state, in both the forms this application needs.
 *
 * 'skeleton' is the default for a list or grid that is about to appear, because
 * it reserves the space the content will occupy -- so the page does not jump when
 * the response lands. 'spinner' is for the cases where the eventual size is
 * unknown (a detail panel, an inline action).
 *
 * ACCESSIBILITY: a purely visual loading state tells a screen-reader user
 * nothing. The label is announced via role="status", which is polite -- it waits
 * for a pause rather than interrupting -- and is the reason `label` has no
 * generic default worth suppressing.
 */
@Component({
  selector: 'app-loader',
  templateUrl: './loader.html',
  styleUrl: './loader.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Loader {
  readonly variant = input<LoaderVariant>('skeleton');

  /** Announced to assistive technology, e.g. "Loading quotes". */
  readonly label = input('Loading');

  /** Skeleton rows to draw. Ignored by the spinner variant. */
  readonly rows = input(3);

  /**
   * A computed range rather than a *ngFor over a number: @for needs something
   * iterable, and building the array in the template would allocate a new one on
   * every change-detection pass, which also breaks track identity.
   */
  protected readonly rowIndexes = computed(() =>
    Array.from({ length: Math.max(1, this.rows()) }, (_unused, index) => index),
  );
}
