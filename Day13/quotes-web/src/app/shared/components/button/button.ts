import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md';

/**
 * The application's only button.
 *
 * Every visual and behavioural difference between buttons in this app is a
 * value of one of the inputs below -- so a change to how buttons look, or to how
 * they behave while a request is in flight, is a change to this one component
 * rather than to every page that has a button on it.
 *
 * IT RENDERS A REAL <button>. Not a styled div, not an anchor: keyboard
 * activation, focus order, form submission and the "button" role all come free
 * and correct, and none of them can be forgotten at a call site.
 *
 * `loading` disables the button as well as showing a spinner, which is what
 * makes double submission impossible rather than merely discouraged -- see
 * isDisabled() below.
 */
@Component({
  selector: 'app-button',
  templateUrl: './button.html',
  styleUrl: './button.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.app-button--block]': 'fullWidth()',
  },
})
export class Button {
  readonly variant = input<ButtonVariant>('primary');
  readonly size = input<ButtonSize>('md');

  /**
   * 'submit' inside a form, 'button' everywhere else. It matters: a button with
   * no explicit type submits its form, so an "add filter" button next to a text
   * input would reload the page.
   */
  readonly type = input<'button' | 'submit'>('button');

  readonly disabled = input(false);

  /** Shows a spinner AND blocks activation. See isDisabled(). */
  readonly loading = input(false);

  /**
   * Replaces the label while loading, so a button can say "Saving…" rather than
   * still saying "Save" with a spinner next to it. Null keeps the projected
   * label as-is.
   */
  readonly loadingLabel = input<string | null>(null);

  readonly fullWidth = input(false);

  /**
   * For icon-only buttons, which have no text to name them. Anything with a
   * visible label should leave this null and let the label be the name -- an
   * aria-label that repeats visible text is noise, and one that contradicts it
   * is a bug only a screen-reader user will ever hit.
   */
  readonly ariaLabel = input<string | null>(null);

  readonly pressed = output<void>();

  /**
   * A computed(), not two checks in the template: `disabled` and `loading` mean
   * the same thing to the DOM, and expressing that once here is what guarantees
   * a loading button cannot be clicked a second time. The template reads this
   * for the disabled attribute AND the class, so the two can never disagree.
   */
  protected readonly isDisabled = computed(() => this.disabled() || this.loading());

  protected onClick(): void {
    // Belt and braces: the attribute already prevents it, but a disabled
    // button that emits anyway is the kind of bug that only shows up as a
    // duplicate row in the database.
    if (this.isDisabled()) {
      return;
    }

    this.pressed.emit();
  }
}
