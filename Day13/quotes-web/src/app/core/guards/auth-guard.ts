import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthStore } from '../services/auth-store';

/**
 * Keeps the application's routes for signed-in users.
 *
 * This is a routing convenience, NOT a security control -- the API rejects an
 * unauthenticated request whatever this returns, and anyone can edit their own
 * JavaScript. What it actually buys is that a signed-out user lands on the sign
 * in screen instead of on a page that renders four empty panels and four 401s.
 *
 * `returnUrl` is carried so that following a deep link while signed out ends up
 * where it was pointing after signing in, rather than at the default page.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const authStore = inject(AuthStore);
  const router = inject(Router);

  if (authStore.isAuthenticated()) {
    return true;
  }

  return router.createUrlTree(['/sign-in'], {
    queryParams: { returnUrl: state.url },
  });
};

/**
 * The inverse, for the sign-in page itself: an already-signed-in user who
 * navigates to /sign-in is sent to the app instead of being shown a form for a
 * session they already have.
 */
export const guestGuard: CanActivateFn = () => {
  const authStore = inject(AuthStore);
  const router = inject(Router);

  return authStore.isAuthenticated() ? router.createUrlTree(['/quotes']) : true;
};
