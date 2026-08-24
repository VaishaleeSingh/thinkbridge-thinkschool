import { DOCUMENT, Injectable, computed, effect, inject, signal } from '@angular/core';

const STORAGE_KEY = 'quotes-web.theme';

/**
 * "system" means: follow the OS, and keep following it if the user changes it
 * later. It is the default because it is the only setting that is right without
 * the user doing anything.
 */
export type ThemePreference = 'system' | 'light' | 'dark';

/**
 * The theme preference, and the one place the `data-theme` attribute on <html>
 * is written.
 *
 * HOW THIS PAIRS WITH THE CSS (styles/_tokens.scss):
 *   - preference 'system' removes the attribute entirely, which lets the
 *     `@media (prefers-color-scheme: dark)` block decide -- including reacting
 *     to the OS changing while the app is open, with no listener here.
 *   - 'light' / 'dark' set the attribute, and the `:root[data-theme=...]`
 *     rules win over the media query in both directions.
 *
 * That split is why this service does not need to know which colours exist, and
 * the stylesheet does not need to know this service exists.
 */
@Injectable({ providedIn: 'root' })
export class ThemeStore {
  private readonly document = inject(DOCUMENT);

  private readonly preferenceSignal = signal<ThemePreference>(readStoredPreference());

  readonly preference = this.preferenceSignal.asReadonly();

  /**
   * What the next click should switch to, and the label for it. A computed()
   * rather than a method, so the button's text and aria-label update with the
   * preference instead of being recomputed by every template that asks.
   */
  readonly nextPreference = computed<ThemePreference>(() =>
    this.preferenceSignal() === 'dark' ? 'light' : 'dark',
  );

  constructor() {
    // Two real side effects, both outside Angular's own rendering: an attribute
    // on the document element (which no template owns) and browser storage.
    // This is what effect() is for; note that nothing derived lives in here.
    effect(() => {
      const preference = this.preferenceSignal();
      const root = this.document.documentElement;

      if (preference === 'system') {
        root.removeAttribute('data-theme');
      } else {
        root.setAttribute('data-theme', preference);
      }

      writeStoredPreference(preference);
    });
  }

  set(preference: ThemePreference): void {
    this.preferenceSignal.set(preference);
  }

  toggle(): void {
    this.preferenceSignal.set(this.nextPreference());
  }
}

function readStoredPreference(): ThemePreference {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
  } catch {
    return 'system';
  }
}

/**
 * localStorage here, unlike the session (see AuthStore): a colour preference is
 * not a credential, and being asked to re-pick dark mode in every new tab is the
 * kind of small annoyance that persistence exists to prevent.
 */
function writeStoredPreference(preference: ThemePreference): void {
  try {
    localStorage.setItem(STORAGE_KEY, preference);
  } catch {
    // Storage blocked -- the theme still applies for this page load.
  }
}
