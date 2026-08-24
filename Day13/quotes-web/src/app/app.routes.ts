import { Routes } from '@angular/router';

import { authGuard, guestGuard } from './core/guards/auth-guard';

/**
 * The route table, and deliberately nothing else -- every page is loaded with
 * loadComponent, so this file imports no page components and stays readable as a
 * map of the application.
 *
 * TWO THINGS ARE EXPRESSED HERE RATHER THAN IN COMPONENTS:
 *
 * 1. Which chrome a route gets. The authenticated routes are children of
 *    MainLayout; /sign-in is a child of AuthLayout. Neither layout needs an @if
 *    over "am I signed in", and no route can accidentally get the wrong one.
 *
 * 2. Who may enter. authGuard on the parent covers every child, so a page added
 *    later is protected by default rather than by someone remembering.
 *
 * WHAT IS DELIBERATELY *NOT* HERE: the feature stores. This file has regressed
 * to `providers` on these routes before -- with a rewritten version of THIS
 * comment claiming that entering the route created the store and leaving it
 * destroyed it. That is not what route providers do: Angular creates that
 * environment injector once and caches it on the route config, so the store
 * outlives every activation. The regression was caught again by
 * verify-ui.mjs's "returning to a page starts it at page 1, not where it was
 * left" -- paging QuotesStore to page 2, leaving for /collections, and coming
 * back showed page 2 again, with the previous page's rows still in memory.
 *
 * Each page provides its own store instead (`providers` on the @Component),
 * which IS created and destroyed with the component -- the lifecycle every
 * comment in this codebase, including the ones that got rewritten, actually
 * wants. See QuotesPage, CollectionsPage and CollectionDetailPage.
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
        loadComponent: () =>
          import('./features/quotes/pages/quotes-page/quotes-page').then((m) => m.QuotesPage),
      },
      {
        // One quote, by id. QuoteDetailStore's lifetime is a viewing of that
        // quote, provided on the page component -- see QuoteDetailPage.
        path: 'quotes/:id',
        title: 'Quote · Quotes',
        loadComponent: () =>
          import('./features/quotes/pages/quote-detail-page/quote-detail-page').then(
            (m) => m.QuoteDetailPage,
          ),
      },
      {
        path: 'collections',
        title: 'Collections · Quotes',
        loadComponent: () =>
          import('./features/collections/pages/collections-page/collections-page').then(
            (m) => m.CollectionsPage,
          ),
      },
      {
        path: 'collections/:id',
        title: 'Collection · Quotes',
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
