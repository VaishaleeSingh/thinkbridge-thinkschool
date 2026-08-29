/**
 * The quote shapes as the Week-1 API actually returns them -- not as the UI
 * would prefer them.
 *
 * Every field here was read off the API rather than guessed:
 *   Quote                 -> QuotesApi/Models/Quote.cs
 *   PagedQuotes           -> the anonymous object returned by GET /api/quotes
 *                            in QuoteEndpointExtensions.cs
 *   CreateQuoteRequest    -> the record of the same name in the same file
 */

/** A single quote. `createdByUserId` is nullable in the API and so is it here. */
export interface Quote {
  readonly id: number;
  readonly author: string;
  readonly text: string;
  readonly backgroundImageUrl: string;

  /**
   * Null for quotes created before the API's Day-3 ownership rules existed, or
   * by a caller with no identifiable user id. It matters to the UI because the
   * API only permits a delete when this matches the caller -- so the delete
   * control is shown accordingly rather than offered and then rejected with a
   * 403.
   */
  readonly createdByUserId: string | null;
}

/**
 * GET /api/quotes?page=&size= returns `{ page, size, total, items }`.
 *
 * Generic because the shape is the API's paging envelope rather than anything
 * quote-specific, even though quotes are currently its only user.
 */
export interface PagedResult<T> {
  readonly page: number;
  readonly size: number;
  readonly total: number;
  readonly items: readonly T[];
}

/** Body of POST /api/quotes. The API caps author at 200 and text at 1000. */
export interface CreateQuoteRequest {
  readonly author: string;
  readonly text: string;
  readonly backgroundImageUrl: string;
}

/** Body of PUT /api/quotes/{id}. */
export interface UpdateQuoteRequest {
  readonly author: string;
  readonly text: string;
  readonly backgroundImageUrl: string;
}

export interface QuoteBackgroundOption {
  readonly label: string;
  readonly url: string;
}

export const QUOTE_BACKGROUND_OPTIONS: readonly QuoteBackgroundOption[] = [
  {
    label: 'Mountain Dawn',
    url: '/quote-backgrounds/mountain-1.webp',
  },
  {
    label: 'Alpine Valley',
    url: '/quote-backgrounds/mountain-2.webp',
  },
  {
    label: 'Snow Peaks',
    url: '/quote-backgrounds/mountain-3.webp',
  },
  {
    label: 'Forest Ridge',
    url: '/quote-backgrounds/mountain-4.webp',
  },
  {
    label: 'Lake Reflection',
    url: '/quote-backgrounds/mountain-5.webp',
  },
  {
    label: 'Highland Sunset',
    url: '/quote-backgrounds/mountain-6.webp',
  },
] as const;

export const DEFAULT_QUOTE_BACKGROUND_URL = QUOTE_BACKGROUND_OPTIONS[0].url;

/**
 * The API's own limits, mirrored here so the form can enforce them before a
 * request is sent AND so the numbers exist in exactly one place on this side of
 * the wire.
 *
 * Duplicating a server rule in a client is a real cost -- the two can drift.
 * The alternative is worse: without them the only way to learn that 1001
 * characters is too long is to type them, submit, and read a validation problem
 * response. The server still enforces its own rules; these only decide when the
 * button is disabled.
 */
export const QUOTE_LIMITS = {
  authorMaxLength: 200,
  textMaxLength: 1000,
} as const;

/**
 * The absolute URL to render a quote's background from.
 *
 * `backgroundImageUrl` comes back from the API as it was stored, which for the
 * bundled backgrounds is a root-relative path like `/quote-backgrounds/x.jpg`.
 * Root-relative against the API, not against this app: in development the two
 * are different origins (see src/environments), so the path has to be resolved
 * against API_BASE_URL or the browser asks the dev server for an image it does
 * not have. An absolute URL is passed through untouched, because a quote may
 * legitimately point at an image hosted anywhere.
 *
 * It lives beside the model rather than in each component because three places
 * render a quote's background -- the card, the preview dialog and the detail
 * page -- and three copies of this rule would be three chances for one of them
 * to be fixed and the others not.
 */
export function resolveQuoteBackgroundUrl(url: string, apiBaseUrl: string): string {
  if (url.startsWith('http://') || url.startsWith('https://')) {
    return url;
  }

  if (url.startsWith('/')) {
    return `${apiBaseUrl}${toWebp(url)}`;
  }

  return toWebp(url);
}

/**
 * The bundled backgrounds are WebP only as of Day 17 -- the JPEGs they were
 * converted from are gone from `public/`.
 *
 * This exists because rows already in the database still hold the old
 * `/quote-backgrounds/x.jpg` values: the migration that seeded them
 * (20260824070320_AddQuoteBackgroundImage) has already run, and editing an
 * applied migration would not re-run it anyway. Rewriting the extension on the
 * way out means those rows keep rendering without a data migration, and it is a
 * no-op for anything written since. Only the bundled path is touched -- a remote
 * URL is someone else's host and is left exactly as stored.
 */
function toWebp(url: string): string {
  return url.startsWith('/quote-backgrounds/') ? url.replace(/\.jpg$/i, '.webp') : url;
}



