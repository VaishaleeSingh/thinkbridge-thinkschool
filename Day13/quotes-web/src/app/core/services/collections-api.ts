import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import {
  AddCollectionItemRequest,
  CollectionDetail,
  CollectionListItem,
  CreateCollectionRequest,
} from '../models/collection';
import { API_BASE_URL } from './api-base-url';

/**
 * The five /api/collections endpoints.
 *
 * Note that list and detail return different types. That is the API's Day-12
 * CQRS-lite split showing through the wire: the list endpoint answers "what
 * collections do I have and how big are they", the detail endpoint answers
 * "what is in this one". This client does not paper over the difference -- see
 * core/models/collection.ts for why flattening them into one type would be
 * worse.
 *
 * Six endpoints, not five: `remove` (DELETE /api/collections/{id}) deletes the
 * whole collection, distinct from `removeItem` below (DELETE
 * /api/collections/{id}/items/{quoteId}), which only removes one quote from it.
 */
@Injectable({ providedIn: 'root' })
export class CollectionsApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  /**
   * GET /api/collections -- the caller's own collections only. Ownership comes
   * from the token's `sub` claim server-side, so there is no owner parameter
   * here and no way for this client to ask for someone else's.
   */
  list(): Promise<readonly CollectionListItem[]> {
    return firstValueFrom(
      this.http.get<readonly CollectionListItem[]>(`${this.baseUrl}/api/collections`),
    );
  }

  getById(id: number): Promise<CollectionDetail> {
    return firstValueFrom(
      this.http.get<CollectionDetail>(`${this.baseUrl}/api/collections/${id}`),
    );
  }

  /** POST /api/collections -- 201. Name is required and capped at 80 chars. */
  create(request: CreateCollectionRequest): Promise<CollectionDetail> {
    return firstValueFrom(
      this.http.post<CollectionDetail>(`${this.baseUrl}/api/collections`, request),
    );
  }

  /**
   * POST /api/collections/{id}/items
   *
   * The API returns the Collection aggregate here rather than the detail read
   * model, so the response is deliberately not typed and not used: the caller
   * re-reads the detail endpoint instead. Typing a response the UI has no shape
   * for would be a lie in the signature, and mapping the aggregate would couple
   * this client to the API's write model.
   *
   * Fails with 400 if the collection already holds 50 quotes, or if the quote
   * is already in it -- both invariants of the aggregate.
   */
  addItem(collectionId: number, request: AddCollectionItemRequest): Promise<unknown> {
    return firstValueFrom(
      this.http.post<unknown>(`${this.baseUrl}/api/collections/${collectionId}/items`, request),
    );
  }

  /** DELETE /api/collections/{id}/items/{quoteId} -- 204, or 404 if not a member. */
  removeItem(collectionId: number, quoteId: number): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.baseUrl}/api/collections/${collectionId}/items/${quoteId}`),
    );
  }

  /**
   * DELETE /api/collections/{id} -- 204 on success, 403 if this caller does not
   * own it, 404 if it never existed. Removes the collection itself, along with
   * whatever quotes it held -- the quotes themselves are untouched, since a
   * collection is a grouping of them, not a container that owns them.
   */
  remove(id: number): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.baseUrl}/api/collections/${id}`));
  }
}
