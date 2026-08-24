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
    url: '/quote-backgrounds/mountain-1.jpg',
  },
  {
    label: 'Alpine Valley',
    url: '/quote-backgrounds/mountain-2.jpg',
  },
  {
    label: 'Snow Peaks',
    url: '/quote-backgrounds/mountain-3.jpg',
  },
  {
    label: 'Forest Ridge',
    url: '/quote-backgrounds/mountain-4.jpg',
  },
  {
    label: 'Lake Reflection',
    url: '/quote-backgrounds/mountain-5.jpg',
  },
  {
    label: 'Highland Sunset',
    url: '/quote-backgrounds/mountain-6.jpg',
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
