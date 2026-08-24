import { Routes } from '@angular/router';

import { authGuard, guestGuard } from './core/guards/auth-guard';
import { CollectionDetailStore } from './features/collections/services/collection-detail-store';
import { CollectionsStore } from './features/collections/services/collections-store';
import { QuotesStore } from './features/quotes/services/quotes-store';

/**
 * The route table, and deliberately nothing else -- every page is loaded with
 * loadComponent, so this file imports no page components and stays readable as a
 * map of the application.
 *
 * THREE THINGS ARE EXPRESSED HERE RATHER THAN IN COMPONENTS:
 *
 * 1. Which chrome a route gets. The authenticated routes are children of
 *    MainLayout; /sign-in is a child of AuthLayout. Neither layout needs an @if
 *    over "am I signed in", and no route can accidentally get the wrong one.
 *
 * 2. Who may enter. authGuard on the parent covers every child, so a page added
 *    later is protected by default rather than by someone remembering.
 *
 * 3. How long feature state lives. `providers` on a route creates the store when
 *    the route is entered and destroys it when it is left, which is why the stores
 *    are not `providedIn: 'root'`: signing out and back in cannot show the
 *    previous session's data, because the store holding it no longer exists.
 *
 * Lazy loading is per page, not per feature: these are small screens, and a
 * feature-level bundle would mean loading the collection detail page in order to
 * see the collections list.
 */
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'quotes',
  },

  {
    path: 'sign-in',
    canActivate: [guestGuard],
    loadComponent: () => import('./layouts/auth-layout/auth-layout').then((m) => m.AuthLayout),
    children: [
      {
        path: '',
        title: 'Sign in · Quotes',
        loadComponent: () =>
          import('./features/auth/pages/sign-in-page/sign-in-page').then((m) => m.SignInPage),
      },
    ],
  },

  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layouts/main-layout/main-layout').then((m) => m.MainLayout),
    children: [
      {
        path: 'quotes',
        title: 'Quotes',
        providers: [QuotesStore],
        loadComponent: () =>
          import('./features/quotes/pages/quotes-page/quotes-page').then((m) => m.QuotesPage),
      },
      {
        path: 'collections',
        title: 'Collections · Quotes',
        providers: [CollectionsStore],
        loadComponent: () =>
          import('./features/collections/pages/collections-page/collections-page').then(
            (m) => m.CollectionsPage,
          ),
      },
      {
        path: 'collections/:id',
        title: 'Collection · Quotes',

        // QuotesStore again, because the "add a quote" picker needs a list of
        // quotes and that store already fetches, pages and filters them. A
        // second, picker-specific service would be the same code with a
        // different name.
        providers: [CollectionDetailStore, QuotesStore],
        loadComponent: () =>
          import('./features/collections/pages/collection-detail-page/collection-detail-page').then(
            (m) => m.CollectionDetailPage,
          ),
      },
      {
        path: '**',
        title: 'Not found · Quotes',
        loadComponent: () =>
          import('./features/not-found/pages/not-found-page/not-found-page').then(
            (m) => m.NotFoundPage,
          ),
      },
    ],
  },
];
