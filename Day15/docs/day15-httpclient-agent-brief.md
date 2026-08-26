# Day 15 — HttpClient + functional interceptors: agent brief

## Role and working agreement

You are implementing this as a junior engineer. I own the brief, review and final verification.

- Work only in `Day13/quotes-web` and `Day15/docs`.
- Do not commit, push, create a branch or modify generated/cache files.
- Read the backend contract before changing frontend code.
- Write the characterization tests first and run them green before implementing interceptors.
- After every edited file, run `npx prettier --write <entire-file-path>`.
- Keep the existing 401 refresh behavior. Do not redesign `AuthStore`.
- Do not add a UI screen. Existing stores already surface `ApiFailure.message`.

## Source of truth

Read these files before writing code:

- `Day7/piece2/QuotesApi/Extensions/QuoteEndpointExtensions.cs`
- `Day7/piece2/QuotesApi/Models/Quote.cs`
- `Day7/piece2/QuotesApi/Middleware/ExceptionHandlingMiddleware.cs`
- `Day13/quotes-web/src/app/core/services/quotes-api.ts`
- `Day13/quotes-web/src/app/core/interceptors/auth-interceptor.ts`
- `Day13/quotes-web/src/app/core/models/api-failure.ts`
- `Day13/quotes-web/src/app/app.config.ts`

The real list contract is:

```http
GET /api/quotes?page=N&size=N
Authorization: Bearer <token>
```

Successful response:

```json
{
  "page": 2,
  "size": 6,
  "total": 13,
  "items": [
    {
      "id": 7,
      "author": "Rumi",
      "text": "The wound is the place where the light enters you.",
      "backgroundImageUrl": "/quote-backgrounds/mountain-1.jpg",
      "createdByUserId": "42"
    }
  ]
}
```

Do not reduce `Quote` to the exercise shorthand `{ id, author, text }`. The current backend also returns `backgroundImageUrl` and nullable `createdByUserId`; the characterization test must pin the real current shape.

Invalid paging returns ASP.NET Core `ValidationProblemDetails` with HTTP 400:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "page": ["Page must be at least 1."],
    "size": ["Size must be between 1 and 100."]
  }
}
```

The exception middleware can also return RFC 7807 `ProblemDetails` with `status`, `title` and `detail`.

## Phase 1 — characterization tests first

Add a focused `quotes-api.spec.ts` beside `quotes-api.ts` using Angular's `HttpTestingController`.

It must prove, before interceptor changes:

1. `getPage(2, 6)` sends exactly one `GET` to `/api/quotes?page=2&size=6`.
2. The resolved value preserves `{ page, size, total, items }` and every real quote field.
3. A real invalid-paging 400 is received as an `HttpErrorResponse` whose body preserves the `ValidationProblemDetails.errors` dictionary.

Run only this spec and record the green command/result in the Day 15 answer. Do not change production code to make this phase pass; it characterizes the contract already implemented by `QuotesApi`.

## Phase 2 — functional interceptor pipeline

Keep `authInterceptor` for the bearer token and existing one-time 401 refresh. Add two focused functional interceptors rather than one catch-all function.

### Retry interceptor

Requirements:

- Retry only `GET` requests. Do not retry `POST`, `PUT`, `PATCH` or `DELETE`.
- Retry only transient failures: status `0`, `408`, `429`, `500`, `502`, `503`, `504`.
- Never retry ordinary 4xx responses such as `400`, `401`, `403` or `404`.
- Make at most two retries after the original attempt.
- Use observable backoff: 100 ms before retry 1, 200 ms before retry 2.
- Tests must use fake time; no test may actually sleep.
- When retries are exhausted, rethrow the original/final failure. Do not turn failure into a successful empty response.

### API error interceptor

Requirements:

- Map the final `HttpErrorResponse` into the existing typed `ApiFailure` contract.
- Preserve `status` and the complete field-error dictionary.
- Prefer the first field error for `ValidationProblemDetails`.
- Otherwise prefer RFC 7807 `detail`, then `title`, then the existing friendly status fallback.
- Ensure existing stores calling `toApiFailure(error)` can accept an already-mapped `ApiFailure` without replacing it with a generic message.
- A final 400 must surface a friendly actionable message, not `Http failure response ...` or `[object Object]`.

### Interceptor ordering

Register the pipeline so:

1. `authInterceptor` sees a raw 401 and keeps its existing refresh behavior.
2. Retry logic sees raw transient HTTP failures.
3. Error mapping happens only once, after auth recovery and retries finish.

Document why the array order in `app.config.ts` produces that response order. Do not guess—cover the combined behavior with a test.

## Required tests

Add focused tests proving:

- bearer header remains attached;
- one 401 refresh and one retry still work;
- GET + 503 + 503 + 200 makes exactly three attempts with 100/200 ms backoff;
- GET stops after two retries;
- POST 503 is not retried;
- GET 400 is not retried;
- ValidationProblemDetails maps to typed `ApiFailure` with first field message and all field errors;
- plain ProblemDetails maps `detail` to the friendly message;
- the registered full interceptor chain does not map a 401 before `authInterceptor` can refresh it.

Prefer small per-interceptor specs plus one pipeline-order integration spec. Avoid duplicating every branch through the full chain.

## Verification commands

From `Day13/quotes-web`:

```powershell
npm test -- --run src/app/core/services/quotes-api.spec.ts
npm test -- --run src/app/core/interceptors
npm test -- --run
npm run build
```

If the installed Angular/Vitest CLI requires a slightly different targeted-test syntax, use the working equivalent and record it honestly.

## Deliverables

1. Characterization test committed only as a working-tree change, green before production edits.
2. Functional retry and error interceptors with focused tests.
3. Updated interceptor registration and any minimal typed-error changes.
4. `Day15/docs/day15-httpclient-agent-answer.md` containing:
   - starting state;
   - characterization-test proof;
   - files changed;
   - interceptor order and why;
   - verification log with exact commands/results;
   - what would break this;
   - any requirement you deliberately did not implement and why.

## Done means

- The real Week-1 contract is pinned by a green test.
- Only transient idempotent GETs retry, with bounded backoff.
- A 4xx becomes a typed, friendly application error.
- Existing 401 refresh behavior remains green.
- All tests and production build pass.
- No commit or push was performed.
