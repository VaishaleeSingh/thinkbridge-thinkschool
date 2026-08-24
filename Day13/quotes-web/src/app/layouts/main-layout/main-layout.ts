import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { AuthStore } from '../../core/services/auth-store';
import { ThemeStore } from '../../core/services/theme-store';
import { Button } from '../../shared/components/button/button';

/**
 * The shell every signed-in route renders inside: header, navigation, theme
 * toggle, sign-out, and the outlet the feature pages appear in.
 *
 * A layout component rather than putting this in `App`, because the sign-in
 * route must NOT have it -- a header with "sign out" and a nav to pages you
 * cannot reach is worse than no header. app.routes.ts nests the authenticated
 * routes under this component and leaves /sign-in outside it, so which chrome a
 * route gets is visible in the route table rather than decided by an @if in a
 * template.
 */
@Component({
  selector: 'app-main-layout',
  templateUrl: './main-layout.html',
  styleUrl: './main-layout.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Button],
})
export class MainLayout {
  private readonly authStore = inject(AuthStore);
  private readonly router = inject(Router);

  protected readonly theme = inject(ThemeStore);
  protected readonly email = this.authStore.email;

  /**
   * The toggle is a single button, so its accessible name has to say what
   * pressing it will DO, not what the current state is -- "Dark theme" as a name
   * on a button that switches to light is actively wrong.
   */
  protected readonly themeActionLabel = computed(() =>
    this.theme.nextPreference() === 'dark' ? 'Switch to dark theme' : 'Switch to light theme',
  );

  constructor() {
    effect(() => {
      if (!this.authStore.isAuthenticated()) {
        void this.router.navigate(['/sign-in']);
      }
    });
  }

  protected async signOut(): Promise<void> {
    await this.authStore.signOut();

    // navigate, not navigateByUrl: the guard on the signed-in routes would send
    // an unauthenticated user here anyway, but doing it explicitly means sign-out
    // does not leave the last page rendered for a frame first.
    await this.router.navigate(['/sign-in']);
  }
}
