# Day 15 — HttpClient + functional interceptors: agent answer

## Starting state

- `QuotesApi.getPage(page, size)` already sent `GET /api/quotes?page=N&size=N` and returned `PagedResult<Quote>`.
- The existing `Quote` model already matched the real backend fields: `id`, `author`, `text`, `backgroundImageUrl`, and nullable `createdByUserId`.
- `authInterceptor` already attached the bearer token and performed one refresh after a raw 401. I did not redesign it.
- `toApiFailure` already understood `ProblemDetails` and `ValidationProblemDetails`, but it was called only by stores and treated an already-mapped `ApiFailure` as an unknown generic error.
- There was no transient retry interceptor, final error-mapping interceptor, registered three-interceptor pipeline, or focused `QuotesApi` characterization spec.

## Characterization-test proof before production edits

I first added only `src/app/core/services/quotes-api.spec.ts`. Before changing production code, I ran:

```powershell
npm test -- --watch=false --include=src/app/core/services/quotes-api.spec.ts
```

Result: **1 test file passed, 2 tests passed**.

This green test proved:

1. `getPage(2, 6)` makes exactly one `GET` request to `http://week-one-api.test/api/quotes?page=2&size=6`.
2. The returned paging envelope and every current quote field are preserved, including `backgroundImageUrl` and both non-null and null `createdByUserId` examples.
3. An invalid paging response remains an `HttpErrorResponse` with status 400 and preserves the complete `ValidationProblemDetails.errors` dictionary.

Angular 21's unit-test builder in this project does not provide the brief's `--run` option. The working targeted equivalent is `--watch=false --include=...`, which is what the commands in this answer record.

## Files changed

- `src/app/core/services/quotes-api.spec.ts` — pins the Week-1 quotes contract.
- `src/app/core/interceptors/retry-interceptor.ts` — retries transient GET failures at most twice with observable 100/200 ms backoff.
- `src/app/core/interceptors/retry-interceptor.spec.ts` — proves backoff, success on the third attempt, exhaustion, no POST retry, and no 400 retry using fake timers.
- `src/app/core/interceptors/api-error-interceptor.ts` — maps only the final pipeline error to `ApiFailure`.
- `src/app/core/interceptors/api-error-interceptor.spec.ts` — proves validation and plain ProblemDetails mapping.
- `src/app/core/interceptors/interceptor-pipeline.spec.ts` — exercises the providers from the real `appConfig` and proves bearer, 401 refresh, and subsequent transient retry behavior together.
- `src/app/core/models/api-failure.ts` — makes `toApiFailure` idempotent for an already-mapped typed failure.
- `src/app/core/models/api-failure.spec.ts` — proves a store can call `toApiFailure` again without losing the friendly message or field errors.
- `src/app/app.config.ts` — registers and documents the complete functional-interceptor chain.
- `Day15/docs/day15-httpclient-agent-answer.md` — this verification note.

Every edited TypeScript and Markdown file was formatted as a whole file immediately after its edit with `npx prettier --write <file>`.

## Interceptor order and why it works

The registered request order is:

```text
apiErrorInterceptor → authInterceptor → retryInterceptor → backend
```

Angular applies responses in reverse:

```text
backend → retryInterceptor → authInterceptor → apiErrorInterceptor
```

That reverse order is the important part:

- `retryInterceptor` sees raw `HttpErrorResponse` values first. It retries only a `GET` with status `0`, `408`, `429`, `500`, `502`, `503`, or `504`.
- A 401 is not transient, so retry passes it through unchanged. `authInterceptor` can then refresh once and resend with the renewed bearer token.
- `apiErrorInterceptor` is the outermost response handler. It maps only the failure left after retry and auth recovery have finished, so retry/auth never receive a premature `ApiFailure` object.

The integration test imports the actual `appConfig` providers. Its response sequence is 401, then 503 after refresh, then 200 after the 100 ms retry delay. It verifies the original bearer token, renewed bearer token, one refresh call, and successful final value. This would fail if error mapping moved inside auth or retry.

## Diff review notes

- The retry guard exits before applying `retry()` for every non-GET method. POST/PUT/PATCH/DELETE therefore cannot be repeated by this interceptor.
- `retry({ count: 2 })` gives exactly three possible attempts: the original plus two retries.
- `timer(retryCount * 100)` produces observable delays of 100 ms and 200 ms. Tests advance fake time and never sleep.
- A non-transient error is returned from the retry delay function with `throwError(() => error)`, preserving the raw failure for auth or final mapping.
- Exhausting transient retries rethrows the third/final `HttpErrorResponse`; it does not manufacture an empty successful response.
- Validation mapping preserves the whole errors dictionary and promotes its first field message. Otherwise the existing priority remains `detail`, then `title`, then the friendly status fallback.
- `toApiFailure` returns an existing `ApiFailure` object unchanged. Existing stores can keep their defensive boundary call without replacing a specific mapped message with “Something went wrong.”
- The existing `authInterceptor` production file was not changed. Its original focused bearer and one-refresh tests remain green.

## Verification log

From `Day13/quotes-web`:

```powershell
npm test -- --watch=false --include=src/app/core/services/quotes-api.spec.ts
```

Result: **1 file passed, 2 tests passed**. This was run before production edits.

```powershell
npm test -- --watch=false --include=src/app/core/interceptors/*.spec.ts
```

Result: **4 files passed, 11 tests passed**.

```powershell
npm test -- --watch=false
```

Result: **13 files passed, 76 tests passed**.

```powershell
npm run build
```

Result: **production build passed**; output was generated at `dist/quotes-web`.

```powershell
npm run lint
```

Result: **lint passed with exit code 0**.

## What would break this

- Moving `apiErrorInterceptor` after auth or retry in the request array would make it run before them on the response path. They would receive an `ApiFailure` instead of a raw `HttpErrorResponse`, preventing status-based refresh/retry decisions.
- Adding a transient status without updating the allow-list would leave that status non-retriable. Broadening the rule to every failure would incorrectly retry ordinary 4xx responses.
- Removing the GET guard could duplicate writes, updates, or deletes.
- Removing the retry count or using an unbounded retry operator could create a request storm during an outage.
- Removing `toApiFailure` idempotence would make stores replace interceptor-created messages with the generic non-HTTP fallback.
- A deliberate backend contract change to the paging URL, envelope, quote fields, or validation dictionary should fail the characterization spec until the frontend contract is consciously updated.
- “First field error” follows the JSON object's field order. The current endpoint inserts `page` before `size`; if the backend changes that order, a different valid field message may become the summary while the full dictionary remains preserved.

## Deliberately not implemented

- No UI screen was added because the existing stores already surface `ApiFailure.message`, and the brief explicitly excluded a new screen.
- `AuthStore` and the existing 401 refresh algorithm were not redesigned.
- No POST, PUT, PATCH, or DELETE retry was added because the requirement is limited to idempotent GET requests.
- No commit, push, or branch operation was performed. All work remains in the working tree.

No requested runtime behavior was omitted.
