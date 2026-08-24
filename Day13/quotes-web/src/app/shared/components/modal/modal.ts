import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  ElementRef,
  effect,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';

import { nextId } from '../../utils/unique-id';
import { Button } from '../button/button';

/**
 * The application's dialog, built on the native <dialog> element opened with
 * showModal().
 *
 * WHY NATIVE, AND NOT A DIV WITH role="dialog": the browser then owns the three
 * things a hand-rolled dialog almost always gets wrong.
 *
 *   1. Focus is TRAPPED. Tab cycles inside the dialog and cannot reach the page
 *      behind it. Reimplementing that means querying focusable descendants on
 *      every keystroke and getting the edge cases (disabled controls, hidden
 *      elements, iframes) right; the platform already has.
 *   2. Focus moves in on open and RETURNS to the element that opened it on close
 *      -- including when that element was removed by whatever the dialog did.
 *   3. Escape closes it, and everything outside it is inert to clicks and to
 *      assistive technology, without aria-hidden being toggled across the page.
 *
 * What is still done here: opening and closing it from a signal input (a <dialog>
 * is opened imperatively, not by an attribute -- binding [open] would produce a
 * NON-modal dialog with none of the above), locking the page behind it from
 * scrolling, refusing to close while a request is in flight, and closing on a
 * backdrop click.
 */
@Component({
  selector: 'app-modal',
  templateUrl: './modal.html',
  styleUrl: './modal.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Button],
})
export class Modal {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);

  readonly open = input(false);
  readonly title = input.required<string>();
  readonly size = input<'sm' | 'md'>('md');

  /**
   * While a request is in flight, Escape and the backdrop stop closing the
   * dialog -- so a half-finished create is not dismissed by a stray click while
   * the POST is still running. The close button disables itself for the same
   * reason.
   */
  readonly busy = input(false);

  readonly closed = output<void>();

  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('dialog');

  protected readonly titleId = nextId('modal-title');

  constructor() {
    // The one bridge between a signal input and an imperative DOM API. It reads
    // both `open()` and the viewChild signal, so it runs again once the element
    // exists -- which is the pass after `open` first becomes true.
    effect(() => {
      const element = this.dialog()?.nativeElement;
      const shouldBeOpen = this.open();

      if (!element) {
        return;
      }

      // Guarded both ways: showModal() on an already-open dialog throws, and
      // close() on a closed one fires a spurious `close` event that would be
      // emitted as a user-initiated dismissal.
      if (shouldBeOpen && !element.open) {
        element.showModal();
      } else if (!shouldBeOpen && element.open) {
        element.close();
      }
    });

    // Scroll lock. showModal() makes everything behind the dialog inert to
    // pointer and assistive technology, but it does NOT stop the page scrolling
    // -- so scrolling the wheel over an open dialog still moves the list behind
    // it. <body> belongs to no component, which is why this is a side effect
    // here rather than a style somewhere.
    effect(() => {
      this.document.body.classList.toggle('has-modal', this.open());
    });

    // THE BUG THIS GUARDS AGAINST: every confirm dialog in this app is rendered
    // inside an @if block bound to [open]="true" -- cancelling sets the
    // controlling signal back to null, which DESTROYS this component rather
    // than closing it (onClose()/the `closed` output are never reached at all).
    // An effect's cleanup only runs on its NEXT execution or on destroy, and
    // destroy does not re-run the effect body one more time with `open()` false
    // -- it just stops it. So the class this effect had set stayed on <body>
    // forever, and the page behind could not be scrolled again until a reload,
    // after every single cancel in the application. Caught by "the scroll lock
    // is released when a dialog is destroyed while open" in verify-ui.mjs,
    // which is the one case a still-open, closed-not-destroyed dialog cannot
    // exercise.
    //
    // A real dialog closed properly (Escape, the backdrop, the close button)
    // still runs the effect above one more time with `open()` false first, so
    // this is a deliberate backstop for the destroy path specifically, not a
    // replacement for it.
    this.destroyRef.onDestroy(() => {
      this.document.body.classList.remove('has-modal');
    });
  }

  /**
   * The native `cancel` event -- Escape, and the platform's own dismiss gesture.
   * Prevented while busy; otherwise allowed to proceed to `close`.
   */
  protected onCancel(event: Event): void {
    if (this.busy()) {
      event.preventDefault();
    }
  }

  /**
   * Fired by the browser after any close, whatever caused it. This is the single
   * place `closed` is emitted, so Escape, the backdrop and the close button all
   * take the same path out.
   */
  protected onClose(): void {
    if (this.open()) {
      this.closed.emit();
    }
  }

  protected onBackdropClick(event: MouseEvent): void {
    // A <dialog> IS its backdrop: clicks on the padding around the panel have
    // the dialog itself as their target, while clicks on anything inside the
    // panel do not. Comparing targets is what tells the two apart without a
    // separate overlay element.
    if (event.target === this.dialog()?.nativeElement) {
      this.requestClose();
    }
  }

  protected requestClose(): void {
    if (!this.busy()) {
      this.dialog()?.nativeElement.close();
    }
  }
}
