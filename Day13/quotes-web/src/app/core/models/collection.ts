/**
 * Collection shapes, taken from the API's Day-12 read models
 * (QuotesApi/Models/CollectionReadModels.cs) rather than from its Collection
 * aggregate.
 *
 * That distinction is the whole reason there are two list/detail types here
 * instead of one `Collection`: the API deliberately returns a different shape
 * per screen -- the list carries a count and no quotes, the detail carries the
 * quotes. Modelling them as one type on this side would mean either pretending
 * the list has quotes (it does not; `quotes` would be permanently undefined) or
 * making every field optional, which pushes the uncertainty into every template
 * that reads it.
 */

/** One row of GET /api/collections. No quotes -- by design; see above. */
export interface CollectionListItem {
  readonly id: number;
  readonly name: string;
  readonly quoteCount: number;

  /** ISO-8601 string, or null for a collection nothing has been added to yet. */
  readonly lastAddedAt: string | null;
}

/** GET /api/collections/{id} -- the detail screen. */
export interface CollectionDetail {
  readonly id: number;
  readonly name: string;
  readonly quoteCount: number;
  readonly quotes: readonly CollectionQuote[];
}

/**
 * A quote as it appears inside a collection: the quote's own fields, plus when
 * it was added to THIS collection. `addedAt` belongs to the membership, not to
 * the quote, which is why it appears here and not on `Quote`.
 */
export interface CollectionQuote {
  readonly quoteId: number;
  readonly author: string;
  readonly text: string;

  /** ISO-8601 string. */
  readonly addedAt: string;
}

/** Body of POST /api/collections. */
export interface CreateCollectionRequest {
  readonly name: string;
}

/** Body of POST /api/collections/{id}/items. */
export interface AddCollectionItemRequest {
  readonly quoteId: number;
}

/**
 * Limits the API enforces on the Collection aggregate
 * (QuotesApi/Models/Collection.cs): name is required and capped at 80
 * characters, and a collection holds at most 50 quotes.
 *
 * `maxItems` is here so the UI can say "this collection is full" instead of
 * letting someone pick a quote and then surfacing the aggregate's exception as
 * a 400.
 */
export const COLLECTION_LIMITS = {
  nameMaxLength: 80,
  maxItems: 50,
} as const;
