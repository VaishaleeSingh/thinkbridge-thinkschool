import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * The root component, and intentionally almost empty: every route renders inside
 * a layout (see app.routes.ts), so there is nothing that belongs to ALL routes.
 *
 * Putting a header here instead would put it on the sign-in screen too, which is
 * the one page that must not have one.
 */
@Component({
  selector: 'app-root',
  template: '<router-outlet />',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet],
})
export class App {}
