/**
 * Story 6.0b AC6 — shared authenticated-fetch helper for the package's
 * Backoffice dashboards.
 *
 * Lifted from `aiv-ai-traffic-dashboard.element.ts:#authedFetch` (Story 5.2)
 * verbatim, retaining its strict-token contract:
 *
 * - Throws {@link AuthContextUnavailableError} when the auth context is
 *   unavailable (auth context not registered yet — boot-time race).
 * - Throws {@link AuthContextUnavailableError} when `config.token()` raises.
 * - Throws {@link AuthContextUnavailableError} when `config.token()` returns
 *   undefined / null / empty string — defends against the silent
 *   `Authorization: Bearer ` (literal empty-bearer) header that Codex review
 *   finding #11 surfaced in the Story 3.2 settings dashboard.
 *
 * Each call site wraps the throw differently — the AI traffic dashboard
 * surfaces `{ kind: "auth-error" }`; the settings dashboard surfaces a
 * `{ ok: false, status: 0, message }` result object. The helper throws so
 * each surface can map to its own UI state without a one-size-fits-all
 * abstraction.
 *
 * Callers pass an `authContextResolver` thunk (typically
 * `() => this.getContext(UMB_AUTH_CONTEXT)`) so the helper stays agnostic
 * of Bellissima's `UmbElementMixin` type system and works against any
 * caller that can produce an auth-context Promise.
 */
export class AuthContextUnavailableError extends Error {
  override readonly name = "AuthContextUnavailableError";
}

export type AuthContextResolver = () => Promise<unknown>;

export interface AuthenticatedFetchOptions {
  method?: string;
  /**
   * If set, the helper JSON-stringifies it and adds `Content-Type:
   * application/json`. Use `undefined` for GET requests; use a serialisable
   * object for PUT/POST. The settings dashboard's PUT path uses this; the
   * AI traffic dashboard's GET-only paths leave it omitted.
   */
  body?: unknown;
  /**
   * Caller-managed AbortSignal so each surface retains abort control over
   * its own in-flight requests (the existing dashboards lazy-create their
   * AbortControllers in the `connectedCallback` / `disconnectedCallback`
   * lifecycle).
   */
  signal: AbortSignal;
  /**
   * Extra request headers merged on top of the helper's defaults
   * (`Accept: application/json`, `Authorization: Bearer ...`,
   * `Content-Type: application/json` when `body` is present). Caller-supplied
   * keys override defaults — **except** `Authorization` (any casing), which
   * is helper-managed and silently dropped from caller headers before the
   * merge. This preserves the empty-bearer guard.
   */
  headers?: Record<string, string>;
}

export async function authenticatedFetch(
  authContextResolver: AuthContextResolver,
  path: string,
  options: AuthenticatedFetchOptions,
): Promise<Response> {
  // Spike 0.B locked decision #11 — bearer-token only against the Management
  // API. Cookie-only fetches return 401 because the Management API enforces
  // OpenIddict bearer-token auth, not cookie auth.
  const authContext = await authContextResolver();
  if (!authContext) {
    throw new AuthContextUnavailableError("Auth context unavailable");
  }
  const config = (
    authContext as {
      getOpenApiConfiguration: () => {
        base: string;
        credentials: RequestCredentials;
        token: () => Promise<string | undefined>;
      };
    }
  ).getOpenApiConfiguration();

  let token: string | undefined;
  try {
    token = await config.token();
  } catch {
    throw new AuthContextUnavailableError("Token acquisition failed");
  }
  // Whitespace-only tokens are also rejected — `!token` only catches falsy
  // values, but `"   "` would otherwise produce `Authorization: Bearer    `,
  // i.e. the same silent-empty-bearer shape that Codex finding #11 surfaced.
  if (!token || token.trim() === "") {
    throw new AuthContextUnavailableError("Token acquisition returned empty");
  }

  const hasBody = options.body !== undefined;
  const baseHeaders: Record<string, string> = {
    Accept: "application/json",
    Authorization: `Bearer ${token}`,
    ...(hasBody ? { "Content-Type": "application/json" } : {}),
  };
  // Caller-supplied keys merge AFTER base, so they can override Accept /
  // Content-Type when needed — but `Authorization` is helper-managed and
  // MUST NOT be overridable, otherwise the empty-bearer guard above is
  // bypassed by a caller passing `{ Authorization: undefined }` (or any
  // other value). Strip both casings before the merge so that lowercase
  // `authorization` keys (which would otherwise duplicate the `Authorization`
  // header rather than override it) cannot slip through either.
  const callerHeaders: Record<string, string> = { ...(options.headers ?? {}) };
  delete callerHeaders.Authorization;
  delete callerHeaders.authorization;
  const mergedHeaders = {
    ...baseHeaders,
    ...callerHeaders,
  };

  return fetch(`${config.base}${path}`, {
    method: options.method ?? "GET",
    credentials: config.credentials,
    signal: options.signal,
    headers: mergedHeaders,
    body: hasBody ? JSON.stringify(options.body) : undefined,
  });
}
