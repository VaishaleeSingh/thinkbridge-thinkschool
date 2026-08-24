import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { ThemeStore } from '../../core/services/theme-store';
import { Button } from '../../shared/components/button/button';

/**
 * The shell for routes reached while signed out: centred, no navigation, no
 * account controls -- nothing to click that would only fail.
 *
 * The theme toggle is the one control it keeps. Someone who prefers the dark
 * theme prefers it on the sign-in screen too, and this is the first screen they
 * see.
 */
@Component({
  selector: 'app-auth-layout',
  templateUrl: './auth-layout.html',
  styleUrl: './auth-layout.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, Button],
})
export class AuthLayout {
  protected readonly theme = inject(ThemeStore);

  protected readonly themeActionLabel = computed(() =>
    this.theme.nextPreference() === 'dark' ? 'Switch to dark theme' : 'Switch to light theme',
  );
}
