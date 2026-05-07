# Getting started with Umbraco.Community.AiVisibility

Umbraco.Community.AiVisibility exposes Umbraco published content to AI crawlers and large-language-model search engines via per-page Markdown rendering, a `/llms.txt` index, a `/llms-full.txt` bulk export, content negotiation, robots.txt audit, and an AI traffic Backoffice dashboard.

This document covers everything shipping in v1 — the per-page Markdown route + per-page caching with publish-driven invalidation; `Accept: text/markdown` content negotiation; `/llms.txt` and `/llms-full.txt` manifests with hot-path protection (`If-None-Match` / 304 / single-flight) and optional hreflang variants; the Settings doctype + `ISettingsResolver` overlay + per-doctype/per-page exclusion; the **Settings → AI Visibility** dashboard with its Management API for editor-driven configuration; the robots audit Health Check (with build-time AI-bot-list sync + SHA pinning); the AI traffic request log (per-instance drainer running on every node) + the **Settings → AI Traffic** dashboard with date-range + classification filtering; HTTP `Link: rel="alternate"; type="text/markdown"` discoverability headers + optional `<llms-link />` and `<llms-hint />` Razor TagHelpers; and the Cloudflare `Content-Signal` response header (off by default; opt-in per-doctype). See [`README.md`](../README.md) for the at-a-glance feature table.

## What you get

After installing the package, every published Umbraco page becomes reachable as Markdown via:

```
GET /{path}.md
```

The package renders the page through Umbraco's normal Razor pipeline (no property walking, no template forking), parses the resulting HTML with AngleSharp, picks the main content region, strips boilerplate, absolutifies relative URLs, and converts to GitHub-flavoured Markdown via ReverseMarkdown — with a YAML frontmatter prefix containing `title`, `url`, and `updated`.

## URL forms

Three URL suffix forms map to the same canonical content:

| URL form | Resolves to | Notes |
|---|---|---|
| `/foo.md` | `/foo` content | The canonical form. Use this in links, sitemaps, and `/llms.txt`. |
| `/foo/index.html.md` | `/foo/` content | Spec-compatible form per [llmstxt.org's convention](https://llmstxt.org). Useful if your site already publishes `/foo/index.html`. |
| `/foo/.md` | `/foo/` content | Typographical-artefact form. Accepted for resilience. |

**All three return identical Markdown — no 301 redirect.** This is a deliberate choice over redirecting the alternate forms to a single canonical URL: it's simpler for crawlers, removes a round-trip, and avoids the brittleness of redirect chains for cases where a tool injects an alternate form by mistake. If your tooling needs a single canonical URL form, prefer `/foo.md`.

The `data-llms-content` attribute on a Razor template element overrides the content-region selection chain — it's the killer feature for adopters with unusual layouts. The strip rules drop `<script>`, `<style>`, `<svg>`, `<iframe>`, `<noscript>`, anything with `[hidden]` or `[aria-hidden="true"]`, and any element marked `data-llms-ignore`.

## Caching

An in-memory cache layer wraps per-page Markdown extraction.

### How it works

| What | Detail |
|---|---|
| **Where** | `IAppPolicyCache` via `AppCaches.RuntimeCache` — Umbraco's standard runtime cache. No bespoke layer, no Redis. |
| **Cache key** | `aiv:page:{nodeKey:N}:{host}:{culture}` — `nodeKey` is `IPublishedContent.Key` (Guid), host is the request host lowercased (or `_` if no ambient `HttpContext`), culture is BCP-47 lowercased (or `_` for invariant content). Host segment ensures multi-domain bindings on the same node never collide on a CDN fronting both hosts. |
| **TTL** | Configured by `AiVisibility:CachePolicySeconds`; default `60` seconds. Set to `0` to disable caching. |
| **Invalidation** | `INotificationAsyncHandler<ContentCacheRefresherNotification>` keyed off Umbraco's distributed cache refresher — fires on every load-balanced instance independently when content publishes, moves, or unpublishes. The handler walks branch descendants via `IDocumentNavigationQueryService` for branch-publish events. |
| **Per-instance** | The cache and its node-to-key index are per-process in-memory. Cross-instance invalidation works via the broadcast notification — no Redis or shared state required. |

### HTTP response headers

Successful `.md` responses carry:

- `Cache-Control: public, max-age={CachePolicySeconds}` — `public` because Markdown is stateless.
- `Vary: Accept` — required so downstream caches don't return Markdown to a caller that sent `Accept: text/html`. Now load-bearing: content negotiation means the same canonical URL can return either Markdown or HTML.
- `ETag: "<hash>"` — strong validator computed from `(host + route + culture + contentVersion)`, where `contentVersion` is the page's `IPublishedContent.UpdateDate`. Every successful publish bumps it; multi-domain bindings on the same node produce distinct ETags.
- `X-Markdown-Tokens: <count>` — Cloudflare-convention character-based token estimate.

A `GET` with a matching `If-None-Match: "<etag>"` header returns `304 Not Modified` with no body. The `ETag`, `Cache-Control`, and `Vary` headers stay set on the 304 per RFC 7232.

### Adopter overrides

The caching decorator wraps `IMarkdownContentExtractor` — adopters who replace the extractor entirely (by registering their own `IMarkdownContentExtractor` before our composer runs) get their implementation invoked directly with **no caching** applied. If you want caching with your own extractor, wrap our `CachingMarkdownExtractorDecorator` yourself in your composer.

`CachePolicySeconds=0` short-circuits the TTL to zero; depending on `IAppPolicyCache` semantics this means "do not cache" or "evict immediately" — either way, every request re-renders.

## Accept-header content negotiation

`Accept: text/markdown` content negotiation runs on canonical (non-`.md`) URLs — for AI crawlers that don't append `.md`.

### How it works

```
GET /home
Accept: text/markdown
```

…returns the same Markdown body, ETag, and headers as `GET /home.md`. The middleware reads Umbraco's resolved `IPublishedRequest` from `HttpContext.Features`, calls the same extractor + cache decorator the `.md` route uses, and writes the response through the same writer — so AC1's "byte-identical bodies + same cache entry consulted" guarantee holds by construction.

### Resolution rules

| `Accept` value | Result |
|---|---|
| `text/markdown` | Markdown response |
| `text/markdown,text/html;q=0.9` | Markdown — higher quality wins |
| `text/html,text/markdown;q=0.5` | HTML — higher quality wins |
| `text/markdown,text/html` (q-tied) | Markdown — first listed wins |
| `text/html` | HTML response |
| `*/*` (browser default) | HTML — `*/*` is treated as "no preference" |
| Missing / empty / malformed | HTML |
| `text/markdown;q=0` (explicit refusal) | HTML |

Method gates: only `GET` and `HEAD` negotiate. `POST` / `PUT` / `DELETE` / `PATCH` pass through unchanged. Paths ending in `.md` pass through too — those are owned by the suffix route.

### `Vary: Accept` is added to every response that touches this middleware

Including responses where we don't divert — required so downstream caches and CDNs don't return a stale Markdown body to an HTML caller (or vice versa). The header is appended (not overwritten), and deduped, so it composes safely with adopters' own `Vary` settings.

### Adopter ordering

The middleware runs in `UmbracoPipelineFilter.PostRouting`, after Umbraco's routing middleware (which populates `UmbracoRouteValues`) and before authentication/authorization. Adopters who register their own `UmbracoPipelineFilter` with a `PostRouting` callback that mutates `Accept` (or short-circuits the response) will see their changes win or lose by composer-registration order — `[ComposeAfter(typeof(Umbraco.Community.AiVisibility.Composing.RoutingComposer))]` puts an adopter's filter after ours.

### Why not User-Agent sniffing?

Cloaking. Google's documentation explicitly calls it out as a penalty risk; the package will never ship UA-based delivery. `Accept` header negotiation is the only supported mechanism.

## Customising extraction

Two layered DI seams are exposed for adopters who need to customise extraction. Pick the **lighter** one when you want to keep the rest of the pipeline intact.

### `IContentRegionSelector` — region-only override

Use this when your templates have a non-standard "main content" boundary that the package's default chain — `[data-llms-content]` → `<main>` → `<article>` → `AiVisibility:MainContentSelectors` (configurable list) — does not catch. The default extractor still runs AngleSharp parse, strip-inside-region, URL absolutification, ReverseMarkdown convert, and YAML frontmatter prepend; only the region selection is yours.

```csharp
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Community.AiVisibility.Composing;
using Umbraco.Community.AiVisibility.Extraction;
using Microsoft.Extensions.DependencyInjection;
using AngleSharp.Dom;

[ComposeAfter(typeof(RoutingComposer))]
public sealed class AcmeRegionSelectorComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) =>
        builder.Services.AddTransient<IContentRegionSelector, AcmeRegionSelector>();
}

internal sealed class AcmeRegionSelector : IContentRegionSelector
{
    public IElement? SelectRegion(IDocument document, IReadOnlyList<string> configuredSelectors)
        => document.QuerySelector("[data-acme-content]")
           ?? document.QuerySelector("main");
}
```

Returning `null` triggers the package's SmartReader fallback. Returning a deliberately empty element (`document.CreateElement("div")`) bypasses the fallback and produces an empty Markdown body — useful for adopters who want explicit "no content" semantics on certain pages.

### `IMarkdownContentExtractor` — full pipeline replacement

Use this when you need a different HTML parser, a different Markdown converter, or radically different extraction logic. The signature is small — one `ExtractAsync(IPublishedContent, string? culture, CancellationToken)` method that returns a `MarkdownExtractionResult`.

```csharp
[ComposeAfter(typeof(RoutingComposer))]
public sealed class AcmeExtractorComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) =>
        builder.Services.AddTransient<IMarkdownContentExtractor, AcmeExtractor>();
}
```

The package's defaults are registered via `services.TryAddTransient`, so your registration overrides ours without a `services.Remove(...)` step. `[ComposeAfter(typeof(RoutingComposer))]` is recommended but not strictly required: when your composer runs before ours, the package's `TryAddTransient` no-ops against your existing registration — you still win.

**Caching interaction:** when you override `IMarkdownContentExtractor`, the package's caching decorator is **not** wrapped around your implementation. The bypass is logged once at startup as an `Information`-level entry (`Adopter has overridden IMarkdownContentExtractor; skipping caching decorator wrap`). If you want caching with your own extractor, wrap our `CachingMarkdownExtractorDecorator` yourself in your composer.

If you want that bypass log to fire reliably, decorate your composer with `[ComposeBefore(typeof(Umbraco.Community.AiVisibility.Composing.CachingComposer))]` in addition to `[ComposeAfter(typeof(RoutingComposer))]`. Without it, the override still wins (DI's last-registration-wins rule applies) but the log line is non-deterministic.

### Lifetime guidance

Both interfaces are registered as `Transient` by default — the default extractor and selector may hold AngleSharp DOM state across an extraction call. You can register `Singleton` if your implementation is stateless and thread-safe; the DI container respects your declaration. Document the reasoning in your composer when going `Singleton`.

### Quality benchmark fixtures

The package ships [`ExtractionQualityBenchmarkTests`](../Umbraco.Community.AiVisibility.Tests/Extraction/ExtractionQualityBenchmarkTests.cs) — a parameterised NUnit test that iterates `Umbraco.Community.AiVisibility.Tests/Fixtures/Extraction/<scenario>/{input.html, expected.md}` pairs and pins the default extractor's output. Drift fails the test with a unified diff. v0.1 ships four scenarios:

- `clean-core-home` — strip selectors, GFM tables, fenced code, blockquote, image alt-drop, URL absolutification
- `clean-core-blog-list` — BlockList rich content with heading-inside-anchor lift
- `clean-core-blockgrid-cards` — BlockGrid card layout with multi-column grid items
- `clean-core-nested-tables-images` — long-form nested headings, GFM tables, figure/figcaption, `data-llms-ignore`

Adopters who fork the package can extend the catalogue with their own fixture pairs — see [`Fixtures/Extraction/README.md`](../Umbraco.Community.AiVisibility.Tests/Fixtures/Extraction/README.md) for the workflow.

## Reverse proxy / load balancer

The cache key and ETag both include the request host (`HttpContext.Request.Host`) so multi-domain bindings on the same Umbraco node produce distinct cache entries and distinct ETags — preventing a CDN or proxy fronting both hosts from serving siteA's body to siteB clients on a 304 revalidation.

Behind a reverse proxy or load balancer (the typical production topology), `HttpContext.Request.Host` reflects the **internal** host (pod IP, internal hostname) by default — not the public-facing host the client sent. Without forwarded-headers middleware in place, every public host collapses onto the same internal host and the per-host ETag separation degrades to a single shared entry.

**Adopter contract:** if you run behind a reverse proxy or load balancer, configure `app.UseForwardedHeaders(...)` with `ForwardedHeaders.XForwardedHost` (and any other forwarded headers your topology requires) **before** `app.UseUmbraco()` in your `Program.cs`:

```csharp
using Microsoft.AspNetCore.HttpOverrides;

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    // KnownProxies / KnownNetworks per your environment.
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseUmbraco()...
```

This is standard ASP.NET Core deployment hygiene; the package does not configure forwarded-headers itself because the trusted-proxy list is environment-specific. If you skip this step, single-domain deployments still work correctly — only the multi-domain per-host ETag/cache separation is affected.

## Cold-start cost

The first Markdown render of a given template JIT-compiles the Razor view — observed at ~6 seconds against Clean.Core 7.0.5 during the in-process-rendering spike. Subsequent renders are 170–600 ms. **The per-page cache absorbs this from the second hit onwards** — pre-warming on app startup is out of scope for v1.

If first-hit latency matters at deploy time, hit `/your-most-important-pages.md` once after a deployment so the JIT cache (and the package's per-page cache) are warm.

## Conditional GET

From v0.3, both `/llms.txt` and `/llms-full.txt` honour `If-None-Match` and respond with `304 Not Modified` for unchanged manifests. The `ETag` is content-derived (SHA-256 of the manifest body, base64-url-encoded, quoted as a strong validator) and travels alongside the cached body, so cache hits reuse the ETag without re-hashing. AI crawlers that revalidate (most do) save round-trip bytes; first-fetch latency is unchanged.

Per RFC 7232 § 6, `If-None-Match` wins over `If-Modified-Since` — the `/llms.txt` and `/llms-full.txt` endpoints ignore `If-Modified-Since` entirely (manifest cache is keyed by `(host, culture)`, not by timestamp; honouring it would invite stale-content false-positives).

## Hreflang variant suffixes

Set `AiVisibility:Hreflang:Enabled: true` in `appsettings.json` to add sibling-culture variant suffixes to `/llms.txt` links:

```jsonc
{
  "AiVisibility": {
    "Hreflang": { "Enabled": true }
  }
}
```

Off by default (FR25). When enabled, each link is followed by zero-or-more variant suffixes in the form `(culture: /culture/path.md)`, in BCP-47-lexicographic order. Single-culture sites see no change. Use this when adopters operate per-culture sub-paths or sub-domains and want cross-culture linkage discoverable to AI crawlers.

Hreflang is **only** applied to `/llms.txt`. `/llms-full.txt` is a single-culture concatenated dump consumed off-site as a self-contained body keyed to the matched `IDomain` binding; cross-culture variant linkage is meaningless there.

## Settings doctype + Backoffice

From v0.4, the package ships a `AI Visibility Settings` Umbraco document type that editors can use to override site-name, site-summary, and per-doctype exclusion without editing `appsettings.json`. The doctype is created automatically on first boot via Umbraco's `PackageMigrationPlan` pipeline (the package registers `AiVisibilityPackageMigrationPlan`, which runs a `CreateAiVisibilitySettingsDoctype` step that calls `IContentTypeService.Save(...)` imperatively). Re-runs are no-ops — `EnsureSettingsDoctype` skip-checks `IContentTypeService.Get(alias)` before creating anything.

### Where to find it

After install, open Backoffice → Content. Create a new node from the root and pick **AI Visibility Settings**. Fill in:

- **Site name** — the H1 emitted at the top of `/llms.txt`. Empty falls back to the matched root content node's name.
- **Site summary** — the blockquote under the `/llms.txt` H1. Soft-capped at 500 chars (truncated with ellipsis on overflow).
- **Excluded doctype aliases** — one alias per line (or comma/semicolon separated). Pages whose `ContentType.Alias` matches any line are omitted from `/llms.txt`, `/llms-full.txt`, and the `.md` route returns `404`.

### Per-page "Exclude from LLM exports" toggle

To let editors exclude individual pages, attach the **AI Visibility Exclusion (composition)** doctype as a composition to any of your own doctypes (Backoffice → Settings → Document Types → your doctype → Add composition). The composition adds a single boolean property `excludeFromLlmExports` (default `false`). Toggling it `true` on a published page causes:

- `GET /that-page.md` → `404 Not Found`
- The page is omitted from `/llms.txt` and `/llms-full.txt`
- HTML responses for that page are unchanged (the toggle only affects the LLM exports — the public site keeps serving normally)

Cache invalidation is automatic: toggling the bool fires a `RefreshNode` notification that clears the per-page Markdown cache, both manifest caches, and the resolver settings cache for every bound hostname in a single invocation.

### Configuration overlay precedence

The package resolves effective settings in this order:

| Layer | Source | Wins over |
|---|---|---|
| Settings doctype | `AI Visibility Settings` content node properties | appsettings + in-code defaults |
| `appsettings.json` | `AiVisibility:` section | in-code defaults |
| In-code defaults | `AiVisibilitySettings` initialiser | — |

**Per-field**, not all-or-nothing — an empty `siteName` in the doctype falls back to `appsettings.json`'s `AiVisibility:SiteName` (and then the in-code default). Same for `siteSummary`.

The `excludedDoctypeAliases` field is the **union** of `appsettings.json`'s `AiVisibility:ExcludedDoctypeAliases` and the doctype's value — adopters' appsettings entries are never discarded by an editor edit.

### Two-list distinction (don't confuse)

| Field | Scope | Default |
|---|---|---|
| `AiVisibility:ExcludedDoctypeAliases` (top-level) | All routes (`/llms.txt`, `/llms-full.txt`, `.md`) | `[]` |
| `AiVisibility:LlmsFullScope:ExcludedDocTypeAliases` | `/llms-full.txt` only — further narrowing | `["errorPage", "redirectPage"]` |

The `/llms-full.txt` route applies BOTH filters cumulatively (logical AND-NOT). The `/llms.txt` and `.md` routes only apply the top-level list.

### uSync coexistence

If your site uses [uSync](https://github.com/KevinJump/uSync) or `uSync.Complete` to own the schema lifecycle, set:

```jsonc
{
  "AiVisibility": {
    "Migrations": {
      "SkipSettingsDoctype": true
    }
  }
}
```

This stops the package from registering `AiVisibilityPackageMigrationPlan` with Umbraco's migration pipeline. uSync then serialises the doctype on first install and owns the lifecycle. The resolver still works — it just falls back fully to appsettings when no `AI Visibility Settings` content node exists.

**Caveat:** flipping this flag from `false` to `true` AFTER the migration has already run does NOT remove the doctype from the host DB (Umbraco's plan-state record persists the executed state). To relocate schema ownership to uSync, delete the doctype manually first.

### Resolver behaviour at request time

The resolver caches its overlay record at `aiv:settings:{culture}` (host-omitted — see [`docs/multi-site.md` § Single-Settings-node design](multi-site.md#single-settings-node-design-host-omitted-resolver-cache)) with TTL `AiVisibility:SettingsResolverCachePolicySeconds` (default `300s`). Cache invalidation is broadcast-driven via Umbraco's distributed cache refresher — every settings cache entry is dropped on any `ContentCacheRefresherNotification`, just like the manifest caches. If the resolver throws (e.g. an adopter override misbehaves), the controllers fail-open: log a `Warning` and fall back to the `appsettings.json` snapshot (no doctype overlay, no exclusion list from the doctype). Same shape as the hreflang resolver-throw graceful degradation.

### Public extension point

`ISettingsResolver` is the fourth public extension point in the package (after `IMarkdownContentExtractor`, `ILlmsTxtBuilder`, `ILlmsFullBuilder`). Override discipline matches the others:

```csharp
// Override BEFORE our composer runs (any composer with no [ComposeAfter])
builder.Services.AddScoped<ISettingsResolver, MyResolver>();
// Or AFTER our composer
[ComposeAfter(typeof(SettingsComposer))]
public sealed class MyComposer : IComposer
{
    public void Compose(IUmbracoBuilder b)
        => b.Services.AddScoped<ISettingsResolver, MyResolver>();
}
```

**Lifetime: Scoped.** The default impl reads request-scoped `IUmbracoContextAccessor`. Adopters re-registering as `Singleton` will hit a captive-dependency exception on first request. The package's `Compose_StartupValidation_LlmsSettingsResolver_NoCaptiveDependency` test pins the contract using `ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }` — adopter overrides should follow the same discipline.

## Zero-config defaults

From v0.6, every route the package ships works zero-config — no `AiVisibility:` section in `appsettings.json`, no Settings doctype edits required. The in-code defaults in [`AiVisibilitySettings`](../Umbraco.Community.AiVisibility/Configuration/AiVisibilitySettings.cs) produce useful output on a typical Umbraco site as soon as `dotnet add package Umbraco.Community.AiVisibility` finishes. The package never ships an `appsettings.json` snippet — adopters who want to override the defaults add a `AiVisibility:` section to their host's own `appsettings.json` (or its environment-specific overlay).

| Route | Zero-config behaviour |
|---|---|
| `/llms.txt` | `# {root content node Name}` H1 (or `# Site` literal when no root resolves), no blockquote when no `siteSummary` is set, all published pages grouped under a single `## Pages` section |
| `/llms-full.txt` | Whole-site scope (no doctype narrowing), tree-order page emission, 5 MB body cap |
| `GET /{path}.md` | Per-page extracted Markdown (with frontmatter) for any published page; `404` for excluded pages |

### Effective defaults table

| Setting | Default | Source |
|---|---|---|
| `AiVisibility:MaxLlmsFullSizeKb` | `5120` | [`AiVisibilitySettings.cs`](../Umbraco.Community.AiVisibility/Configuration/AiVisibilitySettings.cs) |
| `AiVisibility:CachePolicySeconds` (per-page) | `60` | same |
| `AiVisibility:LlmsTxtBuilder:CachePolicySeconds` | `300` | same |
| `AiVisibility:LlmsTxtBuilder:PageSummaryPropertyAlias` | `"metaDescription"` | same |
| `AiVisibility:LlmsFullBuilder:Order` | `TreeOrder` | same |
| `AiVisibility:LlmsFullBuilder:CachePolicySeconds` | `300` | same |
| `AiVisibility:LlmsFullScope:RootContentTypeAlias` | `null` (whole site) | same |
| `AiVisibility:LlmsFullScope:IncludedDocTypeAliases` | `[]` | same |
| `AiVisibility:LlmsFullScope:ExcludedDocTypeAliases` | `["errorPage", "redirectPage"]` | same |
| `AiVisibility:ExcludedDoctypeAliases` (top-level) | `[]` | same |
| `AiVisibility:Hreflang:Enabled` | `false` | same |
| `AiVisibility:SettingsResolverCachePolicySeconds` | `300` | same |
| `AiVisibility:Migrations:SkipSettingsDoctype` | `false` | same |

A drift-detection fixture ([`AiVisibilitySettingsDefaultsTests`](../Umbraco.Community.AiVisibility.Tests/Configuration/AiVisibilitySettingsDefaultsTests.cs)) pins every value above; bumping a default in source without updating the fixture fails CI.

### Site name fallback chain

The H1 emitted on `/llms.txt` resolves through the following layers, returning the first non-whitespace value found:

1. Settings doctype `siteName` field (editor-set, via the standard Umbraco content tree or the Backoffice Settings dashboard below)
2. `appsettings.json` `AiVisibility:SiteName` value (developer-set)
3. The matched root `IPublishedContent.Name` (e.g. `"Home"` on a Clean.Core install)
4. The literal sentinel `"Site"` (used only when no root content node resolves at all — greenfield install before any content is created)

Layer 4 also covers the explicit-empty-string case: an editor clearing the `siteName` field falls through to layer 3 (the resolver and builder both treat empty/whitespace as "fall back").

## Backoffice Settings dashboard

From v0.5, the package ships a Backoffice dashboard under **Settings → AI Visibility** that gives editors a form-based view of the same settings the standard Umbraco content tree exposes on the `AI Visibility Settings` node — `siteName`, `siteSummary`, the doctype-alias exclusion list, and a read-only list of pages with `excludeFromLlmExports` toggled on.

The dashboard surfaces exactly the same overlay rules `ISettingsResolver` enforces (Settings node value > appsettings value > in-code default). On first save, if no `AI Visibility Settings` content node exists yet, the dashboard creates one at the root automatically — adopters using uSync to own the doctype lifecycle don't need to pre-create the node before opening the dashboard.

**Permissions.** The dashboard is conditioned on `Umb.Section.Settings`. Editors without Settings-section access don't see the tile (UX-DR4 — graceful no-render). The Management API behind the dashboard (`/umbraco/management/api/v1/aivisibility/settings/...`) is gated by `[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]`; calls without Settings access return HTTP 403, calls without any auth return HTTP 401.

**Bearer-token auth.** The dashboard's Management API calls use `UMB_AUTH_CONTEXT.getOpenApiConfiguration()` to obtain a bearer token per call (cookie-only fetches against `/umbraco/management/api/...` return HTTP 401 — the Management API enforces OpenIddict bearer auth, not cookies). Adopters scripting the Management API for CI-driven config (e.g. setting site name from a deploy pipeline) must follow the same pattern.

**Save flow.** Save writes through `IContentService.Save` + `IContentService.Publish`. Umbraco's standard `ContentCacheRefresherNotification` fires; the handler clears `aiv:settings:` so the next `/llms.txt` request sees the new values without manual cache-bust. The dashboard round-trips the published values back into the form.

**Validation.** `siteSummary` is capped at 500 characters (counter shown beneath the textarea); `excludedDoctypeAliases` cannot contain whitespace-only entries or case-insensitive duplicates. The dashboard validates client-side; the server re-validates as defence-in-depth and returns `400 ProblemDetails` for direct API callers that bypass the form.

**Management API surface.**

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/umbraco/management/api/v1/aivisibility/settings/` | Returns the resolved overlay + the live Settings node key |
| `PUT` | `/umbraco/management/api/v1/aivisibility/settings/` | Validates + persists + publishes; returns the round-tripped record |
| `GET` | `/umbraco/management/api/v1/aivisibility/settings/doctypes` | Lists publish-eligible doctypes for the multi-select source |
| `GET` | `/umbraco/management/api/v1/aivisibility/settings/excluded-pages?skip=0&take=100` | Read-only page list (clamped to take ≤ 200) |

Operations land in the existing `/umbraco/swagger/umbracocommunityaivisibility/swagger.json` Swagger doc.

### First-run onboarding hint

On first visit to the AI Visibility Settings dashboard, an info-level notice appears at the top reminding adopters that the package is already producing default output ("AI Visibility is now active and producing default output…"). Click `Dismiss` to hide it for your user account; the notice is **per-user**, keyed by your Backoffice user `unique` GUID, and survives browser refreshes and re-logins.

Storage backing: `localStorage` keyed by `aivisibility.onboarding.dismissed.v1.{userUnique}`. The `v1.` segment lets a future onboarding scheme (a future onboarding scheme tying into AI traffic logs) ship a parallel key without ambiguity over which version dismissed a given user. Adopters running storage-disabled browser modes (incognito with site-data blocking) will see the notice each session — the dismiss handler degrades gracefully (no exception, no broken UI) but the flag does not persist across sessions in that mode.

The notice does **not** auto-hide after the first AI-traffic-dashboard request — that auto-hide is deferred to a future release. Until then, the dismiss button is the only signal.

## Discoverability headers + Razor TagHelpers

From v0.7, every opted-in HTML response carries an automatic HTTP `Link: </path.md>; rel="alternate"; type="text/markdown"` header so AI crawlers can find the Markdown alternate without URL guessing. Two optional Razor TagHelpers (`<llms-link />` and `<llms-hint />`) emit the equivalent in document markup for adopters who want body-side discoverability or visually-hidden hint text. See [`docs/data-attributes.md`](data-attributes.md) for the copy-paste setup.

Two additional response headers ride the existing Markdown writer:

- **`X-Markdown-Tokens: <integer>`** — character-based estimate of the body's token count, emitted on every 200 Markdown response (omitted on 304 — body-derived).
- **`Content-Signal: <directives>`** — Cloudflare's content-use policy header. Off by default; configurable per-site and per-doctype (see [`docs/data-attributes.md`](data-attributes.md) for `appsettings` shape).

The MCP Server Card / Agent Skills / WebMCP / OAuth / commerce-protocol surfaces that the [`isitagentready.com`](https://isitagentready.com) scanner checks for are intentionally NOT shipped — they're concerns of the application layer (what services the site offers, who the agent is, how transactions clear), not the content-rendering middleware. See [`docs/data-attributes.md` § Out of scope (intentionally)](data-attributes.md#out-of-scope-intentionally) for the full list and rationale.

## Robots audit Health Check

From v0.8, the Backoffice **Settings → Health Check → LLMs** group surfaces a `LLMs robots.txt audit` check that fetches your site's `/robots.txt` and cross-references it against the [`ai-robots-txt/ai.robots.txt`](https://github.com/ai-robots-txt/ai.robots.txt) AI-crawler list. Findings appear grouped by category (training / search-retrieval / user-triggered / opt-out) with copy-pasteable suggested removals.

**The package never modifies your `/robots.txt`** — it audits and surfaces, you decide. See [`docs/robots-audit.md`](robots-audit.md) for the full contract.

| Setting | Default | Effect |
|---|---|---|
| `AiVisibility:RobotsAuditOnStartup` | `true` | Run a one-shot audit at host startup (per scheduling-publisher / single-instance role only — multi-front-end installs don't all hammer their own origin). |
| `AiVisibility:RobotsAuditor:RefreshIntervalHours` | `24` | Recurring refresh cadence via Umbraco's `IDistributedBackgroundJob` (exactly-once across a load-balanced deployment). Set to `0` to disable the recurring refresh; the on-demand Health Check view still works. |
| `AiVisibility:RobotsAuditor:FetchTimeoutSeconds` | `5` | Per-host `/robots.txt` fetch timeout (seconds). Clamped to `[1, 60]` at consumption. |
| `AiVisibility:RobotsAuditor:DevFetchPort` | `null` | **Dev-only escape hatch.** Overrides the scheme default port for the audit fetch (e.g. `44314` for a TestSite on Kestrel). **DO NOT set in production** — production deploys serve `/robots.txt` on 443/80. Live in `appsettings.Development.json` only. |
| `AiVisibility:RobotsAuditor:RefreshIntervalSecondsOverride` | `null` | **Dev-only escape hatch.** Forces seconds-precision refresh cycles instead of `RefreshIntervalHours`. **DO NOT set in production** — would hammer adopter origins. Live in `appsettings.Development.json` only. |

The AI-bot list is **synced from upstream at build time** with SHA pinning — the build hard-fails on a SHA mismatch (deliberate; protects against silent feed tampering). Offline / disconnected builds fall back to the committed snapshot at `Umbraco.Community.AiVisibility/Robots/AiBotList.fallback.txt` with a warning. See [`docs/maintenance.md`](maintenance.md) for the SHA-refresh process.

Adopters can replace the auditor entirely by registering a Singleton `IRobotsAuditor` of their own (Singleton lifetime is required — see [`docs/robots-audit.md` § Custom auditors](robots-audit.md#custom-auditors)).

## Upgrading from v0.7 to v0.8

The robots audit ships as net-new surface — existing routes, headers, and DI shapes are unchanged. Notable new artefacts:

- New `Umbraco.Community.AiVisibility/Robots/` and `Umbraco.Community.AiVisibility/Telemetry/` namespaces.
- New build-time MSBuild target `SyncAiBotList` — embeds an AI-bot list resource into the assembly. Online + offline build paths both work; see `docs/maintenance.md`.
- New `IRobotsAuditor` extension point. Adopters wanting custom audit semantics override via `services.AddSingleton<IRobotsAuditor, MyImpl>()`.
- New `AiVisibility:RobotsAuditOnStartup` + `AiVisibility:RobotsAuditor:*` configuration keys.

## Upgrading from v0.6 to v0.7

v0.7 introduced breaking changes for adopters who customise the Markdown response pipeline. The full set of changes is in [`CHANGELOG.md`](../CHANGELOG.md); the load-bearing items:

- **`MarkdownController` constructor signature changed.** Two new dependencies (`IExclusionEvaluator`, `IOptionsMonitor<AiVisibilitySettings>`) and the private `IsExcludedAsync`/`TryReadExcludeBool` helpers were removed in favour of the shared `IExclusionEvaluator` seam. Adopters who subclass or service-locate `MarkdownController` directly will fail to compile until they update; the controller is intended as the package's own surface, not an extension seam, so the breaking change is loud-by-design.
- **`IMarkdownResponseWriter.WriteAsync` gained a 4th parameter `string? contentSignal`.** A 3-arg overload remains available as `[Obsolete]` (it forwards to the 4-arg version with `contentSignal: null`) so adopters who *call* the interface keep working with a deprecation warning. Adopters who *implement* the interface must add the 4-arg overload and will lose Content-Signal emission until they wire it through.
- **New `Link: rel="alternate"` HTTP header on every opted-in HTML response.** Add `AiVisibility:DiscoverabilityHeader:Enabled: false` to suppress globally. See [`data-attributes.md`](data-attributes.md#http-link-discoverability-header) for the full gating contract.
- **New `<llms-link />` and `<llms-hint />` Razor TagHelpers.** Opt-in via `_ViewImports.cshtml` — see [`data-attributes.md`](data-attributes.md#razor-taghelpers--llms-link--and-llms-hint-).
- **New `Content-Signal` configurable Markdown response header.** Off by default; configure under `AiVisibility:ContentSignal:*`.
- **New `IExclusionEvaluator` public seam.** The default `DefaultExclusionEvaluator` is `public sealed` — adopters can wrap-and-delegate via the DI Decorator pattern. See [`data-attributes.md` § Customising the exclusion contract](data-attributes.md#customising-the-exclusion-contract).
- **New RCL static asset at `/umbraco-community-aivisibility.css`.** Optional; needed only if you use `<llms-hint />` and don't already ship a `.visually-hidden` (or equivalent) class.

Single-route adopters (only consuming `/llms.txt`, `/llms-full.txt`, or `.md` URLs without subclassing or implementing the package interfaces) need no code changes.

## Notifications + request log + UA classification

From v0.9, every successful Markdown / `/llms.txt` / `/llms-full.txt` response publishes a sealed Umbraco notification (fire-and-forget) and the package's default writer persists a non-PII row to `aiVisibilityRequestLog` in your host DB. **Subscribe in your composer to consume the events; override `IRequestLog` to redirect to App Insights / Serilog / a custom sink.** See [`docs/extension-points.md`](extension-points.md) for the full contract.

### Configuration keys reference

| Key | Default | Effect |
|---|---|---|
| `AiVisibility:RequestLog:Enabled` | `true` | Kill switch for the package's default writer. When `false`, notifications STILL fire (they're public events decoupled from the writer) — only the default DB write short-circuits. |
| `AiVisibility:RequestLog:QueueCapacity` | `1024` | Bounded channel capacity. Clamped at runtime to `[64, 65536]`. `DropOldest` semantics shed oldest entries on overflow. |
| `AiVisibility:RequestLog:BatchSize` | `50` | Drainer batch size. Clamped to `[1, 1000]`. |
| `AiVisibility:RequestLog:MaxBatchIntervalSeconds` | `1` | Maximum interval between batch flushes when the queue isn't yet full. Clamped to `[1, 60]`. |
| `AiVisibility:RequestLog:OverflowLogIntervalSeconds` | `60` | How often the writer logs an overflow Warning under sustained crawl. Clamped to `[5, 3600]`. |
| `AiVisibility:LogRetention:DurationDays` | `90` | Days to retain rows in `aiVisibilityRequestLog`. Set to `0` (or any value `≤ 0`) to disable retention — rows persist indefinitely. Otherwise clamped to `[1, 3650]` (10 years). The disable check happens BEFORE clamping. |
| `AiVisibility:LogRetention:RunIntervalHours` | `24` | Retention job cadence. Set to `0` (or any value `≤ 0`) to disable the recurring DELETE — `Period` returns `Timeout.InfiniteTimeSpan`. Otherwise clamped to `[1, 8760]`. The disable check happens BEFORE clamping. |
| `AiVisibility:LogRetention:RunIntervalSecondsOverride` | `null` | Dev-only escape hatch — seconds-precision cycles for an internal two-instance test gate. **Do not set in production.** Clamped to `[1, 86400]`. |

### PII discipline

The notification payloads + the `aiVisibilityRequestLog` columns capture **path, content key, culture, UA classification, referrer host** ONLY. Never query strings, cookies, tokens, session IDs, or full referrer paths. Adopter handlers MUST honour the same discipline if they forward data to external sinks.

## Upgrading from v0.8 to v0.9

v0.9 introduced breaking changes for adopters who construct `MarkdownController`, `LlmsTxtController`, `LlmsFullTxtController`, or `AcceptHeaderNegotiationMiddleware` directly. Each gained one new constructor parameter (`ILlmsNotificationPublisher`); the controllers are intended as the package's own surfaces, so the breaking change is loud-by-design. Adopters who consume the routes via HTTP only — or who subscribe via `INotificationAsyncHandler<T>` — need no code changes.

## AI Traffic Backoffice dashboard

From v0.10, the Backoffice **Settings** section adds a second tile: **AI Traffic**. The dashboard is a read-only mini-analytics surface backed by the `aiVisibilityRequestLog` table populated by the request log writer.

- **Default range** is the last 7 UTC days. Use the date-range pickers (top of the dashboard) to widen up to 365 days; wider ranges clamp server-side and surface the `X-AiVisibility-Range-Clamped: true` response header (the dashboard updates the `From` field to the effective post-clamp value).
- **Classification chips** — one per UA class with at least one row in the current range, sorted by descending count. Click to filter (multi-select); click again to deselect; deselect all to see every class. Chip colours: AI classes (training / search / user-triggered) render primary; deprecated AI tokens render warning; human browsers render positive; non-AI crawlers + unknown render neutral.
- **Pagination** — `<uui-pagination>` appears when results span more than one page (default page size 50; clamped at 200 per request via `AiVisibility:Analytics:MaxPageSize`). Ordering is `createdUtc DESC, id DESC` for stable round-trips across page changes.
- **Soft cap** — when total in-range rows exceed `AiVisibility:Analytics:MaxResultRows` (default 10000), the dashboard surfaces a "Showing first N results" footer prompting you to narrow the range or filter by class.
- **Empty state** — when zero rows match the current filter, the dashboard surfaces "No AI traffic recorded yet for this filter. The package is logging — check back later." plus an additional retention-aware hint when the range is older than `AiVisibility:LogRetention:DurationDays`.
- **Permissions** — the dashboard mounts under `Umb.Section.Settings`. Editors without Settings-section access do not see the tile; direct API calls return HTTP 403.

The dashboard reads via four `Management API` operations under `/umbraco/management/api/v1/aivisibility/analytics/`: `requests` (paginated rows), `classifications` (chip source), `summary` (header total + first/last seen), `retention` (one-shot `DurationDays` lookup). All require bearer-token auth via the standard `UMB_AUTH_CONTEXT.getOpenApiConfiguration()` pattern.

### Configuration keys reference

| Key | Default | Notes |
|---|---|---|
| `AiVisibility:Analytics:DefaultPageSize` | `50` | Default page size when the request omits `?pageSize=`. Clamped at runtime to `[1, MaxPageSize]`. |
| `AiVisibility:Analytics:MaxPageSize` | `200` | Maximum allowed page size; requests above this clamp DOWN. Defends against unbounded JSON response sizes. |
| `AiVisibility:Analytics:DefaultRangeDays` | `7` | Default range span when the request omits `?from=`. |
| `AiVisibility:Analytics:MaxRangeDays` | `365` | Maximum allowed range span. Wider requests clamp `from = to - MaxRangeDays.Days` AND surface the `X-AiVisibility-Range-Clamped: true` response header. |
| `AiVisibility:Analytics:MaxResultRows` | `10000` | Soft cap on total in-range matching rows. When exceeded, the response body carries `totalCappedAt`; the dashboard shows the "Showing first N results — narrow your date range" footer. Set to `0` (or any value `≤ 0`) to disable the cap surface entirely. |

### Read-side caveat (adopter `IRequestLog` overrides)

The dashboard reads DIRECTLY from the host DB's `aiVisibilityRequestLog` table via NPoco. Adopters who replace the default `IRequestLog` with a non-DB sink (App Insights, Serilog, custom queue) will see an **empty dashboard** — the table is the empty-by-design state when no writes land in it. v1 does not ship a pluggable read seam; ship your own dashboard against your own analytics sink. See [`docs/extension-points.md`](extension-points.md) for the full discussion.

## Upgrading from v0.9 to v0.10

v0.10 is non-breaking. The new dashboard manifest is additive — the existing "AI Visibility" Settings tile is unaffected. Adopters upgrading from v0.9 see the new "AI Traffic" tile appear under Settings as soon as they upgrade.

## What's not in v0.10

Coming in later epics:

- v1.0 NuGet release readiness (Epic 6)

## Anti-patterns the package will NOT ship

These are common asks that the package explicitly refuses:

- User-Agent sniffing for Markdown delivery (cloaking; Google penalty)
- `<meta name="llms">` injection (rejected by WHATWG)
- `/.well-known/ai.txt` (no spec, no consensus)
- AI/human toggle UI button (decorative; AI doesn't click)
- `rel="canonical"` from `.md` back to HTML (Cloudflare and Evil Martians both say no)
- Property-walking content registry (architectural principle: the Umbraco template is the canonical visual form of content)
