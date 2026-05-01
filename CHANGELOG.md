# Changelog

All notable changes to **LlmsTxt.Umbraco** are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html) (with a pre-1.0 caveat: v0.x minor versions may include breaking changes — call-outs below).

## [v0.7] — Story 4.1: HTTP `Link` discoverability header + Razor TagHelpers + Cloudflare addendum headers

### Added

- **HTTP `Link: rel="alternate"; type="text/markdown"` discoverability header** on every opted-in HTML response. Auto-emitted by a new `DiscoverabilityHeaderMiddleware` registered via `UmbracoPipelineFilter.PostRouting`. Includes idempotent `Vary: Accept`. Headers are flushed via `Response.OnStarting` with a `StatusCode < 300` guard so downstream filters that rewrite to 4xx/5xx don't carry the header onto error responses.
- **`<llms-link />` Razor TagHelper** — emits `<link rel="alternate" type="text/markdown" href="/path.md" />` inside `<head>`.
- **`<llms-hint />` Razor TagHelper** — emits a visually-hidden `<div role="note">` with a body anchor pointing at the Markdown alternate. Visually hidden via the new `.llms-hint` CSS class shipped at `/llms-txt-umbraco.css` (RCL static asset).
- **`X-Markdown-Tokens: <integer>` response header** on every successful 200 Markdown response (omitted on 304 — body-derived). Cloudflare-convention character-based estimate (`Math.Max(1, length / 4)`).
- **`Content-Signal: <directives>` response header** on Markdown responses when configured. Off by default; configurable site-wide and per-doctype under `LlmsTxt:ContentSignal:Default` and `LlmsTxt:ContentSignal:PerDocTypeAlias:<alias>`. Rides 304 responses (RFC 7232 § 4.1 representation-metadata).
- **`ILlmsExclusionEvaluator` public extension seam.** Default implementation `DefaultLlmsExclusionEvaluator` is `public sealed` so adopters can wrap-and-delegate via the DI Decorator pattern. Replaces the previously-duplicated `IsExcludedAsync` private helpers in `MarkdownController` and `AcceptHeaderNegotiationMiddleware`.
- **Configuration keys.** `LlmsTxt:DiscoverabilityHeader:Enabled` (default `true`), `LlmsTxt:ContentSignal:Default` (default `null`), `LlmsTxt:ContentSignal:PerDocTypeAlias:<alias>` (default empty map). All read live via `IOptionsMonitor` — flipping at runtime takes effect on the next request without restart.
- **Documentation.** New `docs/data-attributes.md` covers the full Story 4.1 surface — discoverability header, TagHelpers, optional CSS asset, Cloudflare alignment, exclusion-decorator pattern, `curl` verification.

### Changed (BREAKING — pre-1.0)

- **`MarkdownController` constructor signature changed.** Now takes `ILlmsExclusionEvaluator` and `IOptionsMonitor<LlmsTxtSettings>`. The previously-private `IsExcludedAsync` and `TryReadExcludeBool` helpers were removed (logic lifted into the shared evaluator). Adopters who subclass or service-locate the controller directly will fail to compile until they update. The controller is the package's own HTTP surface, not an adopter extension seam — the loud break is by design.
- **`IMarkdownResponseWriter.WriteAsync` gained a 4th positional parameter `string? contentSignal`.** A 3-arg overload remains available as `[Obsolete("Pass null for contentSignal explicitly via the 4-arg overload. This overload is removed in v1.0.")]` and forwards to the 4-arg version with `contentSignal: null`. **Adopters who *call* the interface** keep working with a deprecation warning. **Adopters who *implement* the interface** must add the 4-arg overload — their existing 3-arg implementation is no longer the abstract method on the interface, and they will lose Content-Signal emission until they wire it through.

### Fixed (review patches landed under v0.7)

- `MarkdownAlternateUrl.Append("")`, `Append(null)`, and `Append("/")` now collapse to the same root alternate (`/index.html.md`) — previously `Append("")` returned `/.md` (inconsistent with the trailing-slash rule).
- `MarkdownAlternateUrl.Append` no longer hard-codes the `"index.html.md"` literal — uses `Constants.Routes.IndexHtmlMdSuffix`.
- `DiscoverabilityHeaderMiddleware` now uses `IsNullOrWhiteSpace` (not `IsNullOrEmpty`) for the canonical-URL guard so adopter `IPublishedUrlProvider` overrides returning whitespace-only values don't produce mangled `Link` values.
- `DiscoverabilityHeaderMiddleware` now sanitises the alternate URL for CR/LF and `<>` characters to defend against header injection from hostile/buggy `IPublishedUrlProvider` overrides.
- `MarkdownResponseWriter` now sanitises `Content-Signal` for CR/LF before writing to defend against header injection from malformed adopter config.
- `ContentSignalResolver.Resolve` is now defensively null-coalesced against `settings.ContentSignal` and `settings.ContentSignal.PerDocTypeAlias` being explicitly null (init-set property edge case).
- `<llms-link />` / `<llms-hint />` TagHelpers now catch `UriFormatException` around the request-URI build (previously bubbled to the Razor view) and `OperationCanceledException` (suppress output cleanly when the request is aborted mid-render).
- New CSS rules in `/llms-txt-umbraco.css`: `:focus-within`/`:focus-visible` reveal pattern (WCAG 2.4.7 Focus Visible), `@media (forced-colors: active)` hide (some High Contrast modes strip the `clip` rule), `@media print` hide.
- `_ViewImports.cshtml` adopter snippet uses namespace-scoped `@addTagHelper LlmsTxt.Umbraco.TagHelpers.*, LlmsTxt.Umbraco` (not wildcard `*, LlmsTxt.Umbraco`) to prevent future internal types in other namespaces auto-registering as TagHelpers.

### Migration

See [`docs/getting-started.md` § Upgrading from v0.6 to v0.7](docs/getting-started.md#upgrading-from-v06-to-v07) for adopter actions. Single-route adopters (only consuming `/llms.txt`, `/llms-full.txt`, or `.md` URLs without subclassing or implementing the package interfaces) need no code changes.

## [v0.6] — Story 3.3: Zero-config defaults + onboarding hint

(Earlier history: see `git log` and per-story commits — Stories 3.1, 3.2, 3.3, 2.x, 1.x, 0.x.)
