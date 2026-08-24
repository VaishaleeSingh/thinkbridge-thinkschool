/**
 * Production environment. Substituted for `environment.ts` at build time by
 * angular.json's `fileReplacements`, so nothing imports this file directly.
 */
export const environment = {
  production: true,

  /**
   * Empty on purpose: an empty base makes every request same-origin
   * (`/api/quotes` rather than `https://host/api/quotes`), which is correct
   * when the SPA is served from the same host as the API -- behind the same
   * reverse proxy, ingress, or Azure Container Apps ingress rule.
   *
   * It is also the only value that cannot be wrong at build time. A hardcoded
   * production hostname here would have to be re-baked for every environment
   * the app is deployed to, and would be committed to the repository, which is
   * the mistake this file's whole existence is meant to avoid. If the API ever
   * genuinely lives on another origin, set it here AND add that SPA's origin to
   * `Cors:AllowedOrigins` on the API -- one without the other produces a
   * browser-side CORS failure that looks like an outage.
   */
  apiBaseUrl: '',
} as const;
