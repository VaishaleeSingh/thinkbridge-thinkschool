/**
 * Development environment (the default). Swapped for
 * `environment.production.ts` by the production build's fileReplacements --
 * see angular.json.
 *
 * WHY THIS FILE EXISTS: so that no component, service or template contains an
 * API host. Exactly one module in the application knows where the API lives,
 * and it is this one; everything else asks for `API_BASE_URL` (see
 * core/services/api-base-url.ts) and cannot tell development from production.
 */
export const environment = {
  production: false,

  /**
   * The Week-1 QuotesApi as `dotnet run` serves it -- see
   * Day7/piece2/QuotesApi/Properties/launchSettings.json, `http` profile.
   *
   * A cross-origin absolute URL rather than a dev-server proxy, deliberately.
   * A proxy would make the browser believe the API is same-origin, which means
   * the CORS policy the API actually enforces would never be exercised until
   * the first deployment -- exactly the environment where finding out it is
   * wrong is most expensive. Pointing straight at :5059 means every local run
   * goes through the same preflight-and-Origin path a deployed browser would.
   *
   * The API's matching half is `Cors:AllowedOrigins` in
   * QuotesApi/appsettings.Development.json.
   */
  apiBaseUrl: 'http://localhost:5059',
} as const;
