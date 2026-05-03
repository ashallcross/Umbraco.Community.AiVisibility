# Getting started with LlmsTxt.Umbraco

LlmsTxt.Umbraco exposes Umbraco published content to AI crawlers and large-language-model search engines via per-page Markdown rendering, a `/llms.txt` index, and a `/llms-full.txt` bulk export.

This document covers what ships up to **v0.5 (Story 3.2)** — the per-page Markdown route, per-page caching with publish-driven invalidation, `Accept: text/markdown` content negotiation, the `/llms.txt` and `/llms-full.txt` manifests with hot-path protection (`If-None-Match` / 304 / single-flight) and optional hreflang variants, the Settings doctype + `ILlmsSettingsResolver` overlay + per-doctype/per-page exclusion, **and the Backoffice Settings dashboard with its Management API for editor-driven configuration**. The robots audit and AI traffic dashboard land in later stories.

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

Story 1.2 adds an in-memory cache layer over the per-page Markdown extraction.

### How it works

| What | Detail |
|---|---|
| **Where** | `IAppPolicyCache` via `AppCaches.RuntimeCache` — Umbraco's standard runtime cache. No bespoke layer, no Redis. |
| **Cache key** | `llms:page:{nodeKey:N}:{host}:{culture}` — `nodeKey` is `IPublishedContent.Key` (Guid), host is the request host lowercased (or `_` if no ambient `HttpContext`), culture is BCP-47 lowercased (or `_` for invariant content). Host segment ensures multi-domain bindings on the same node never collide on a CDN fronting both hosts. |
| **TTL** | Configured by `LlmsTxt:CachePolicySeconds`; default `60` seconds. Set to `0` to disable caching. |
| **Invalidation** | `INotificationAsyncHandler<ContentCacheRefresherNotification>` keyed off Umbraco's distributed cache refresher — fires on every load-balanced instance independently when content publishes, moves, or unpublishes. The handler walks branch descendants via `IDocumentNavigationQueryService` for branch-publish events. |
| **Per-instance** | The cache and its node-to-key index are per-process in-memory. Cross-instance invalidation works via the broadcast notification — no Redis or shared state required. |

### HTTP response headers

Successful `.md` responses carry:

- `Cache-Control: public, max-age={CachePolicySeconds}` — `public` because Markdown is stateless.
- `Vary: Accept` — required so downstream caches don't return Markdown to a caller that sent `Accept: text/html`. Now load-bearing: Story 1.3's content negotiation means the same canonical URL can return either Markdown or HTML.
- `ETag: "<hash>"` — strong validator computed from `(host + route + culture + contentVersion)`, where `contentVersion` is the page's `IPublishedContent.UpdateDate`. Every successful publish bumps it; multi-domain bindings on the same node produce distinct ETags.
- `X-Markdown-Tokens: <count>` — Cloudflare-convention character-based token estimate.

A `GET` with a matching `If-None-Match: "<etag>"` header returns `304 Not Modified` with no body. The `ETag`, `Cache-Control`, and `Vary` headers stay set on the 304 per RFC 7232.

### Adopter overrides

The caching decorator wraps `IMarkdownContentExtractor` — adopters who replace the extractor entirely (by registering their own `IMarkdownContentExtractor` before our composer runs) get their implementation invoked directly with **no caching** applied. If you want caching with your own extractor, wrap our `CachingMarkdownExtractorDecorator` yourself in your composer.

`CachePolicySeconds=0` short-circuits the TTL to zero; depending on `IAppPolicyCache` semantics this means "do not cache" or "evict immediately" — either way, every request re-renders.

## Accept-header content negotiation

Story 1.3 adds `Accept: text/markdown` content negotiation on canonical (non-`.md`) URLs — for AI crawlers that don't append `.md`.

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

The middleware runs in `UmbracoPipelineFilter.PostRouting`, after Umbraco's routing middleware (which populates `UmbracoRouteValues`) and before authentication/authorization. Adopters who register their own `UmbracoPipelineFilter` with a `PostRouting` callback that mutates `Accept` (or short-circuits the response) will see their changes win or lose by composer-registration order — `[ComposeAfter(typeof(LlmsTxt.Umbraco.Composers.RoutingComposer))]` puts an adopter's filter after ours.

### Why not User-Agent sniffing?

Cloaking. Google's documentation explicitly calls it out as a penalty risk; the package will never ship UA-based delivery. `Accept` header negotiation is the only supported mechanism.

## Customising extraction

Story 1.4 documents the two layered DI seams adopters can override. Pick the **lighter** one when you want to keep the rest of the pipeline intact.

### `IContentRegionSelector` — region-only override

Use this when your templates have a non-standard "main content" boundary that the package's default chain — `[data-llms-content]` → `<main>` → `<article>` → `LlmsTxt:MainContentSelectors` (configurable list) — does not catch. The default extractor still runs AngleSharp parse, strip-inside-region, URL absolutification, ReverseMarkdown convert, and YAML frontmatter prepend; only the region selection is yours.

```csharp
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using LlmsTxt.Umbraco.Composers;
using LlmsTxt.Umbraco.Extraction;
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

**Caching interaction:** when you override `IMarkdownContentExtractor`, the package's caching decorator (Story 1.2) is **not** wrapped around your implementation. The bypass is logged once at startup as an `Information`-level entry (`Adopter has overridden IMarkdownContentExtractor; skipping caching decorator wrap`). If you want caching with your own extractor, wrap our `CachingMarkdownExtractorDecorator` yourself in your composer.

If you want that bypass log to fire reliably, decorate your composer with `[ComposeBefore(typeof(LlmsTxt.Umbraco.Composers.CachingComposer))]` in addition to `[ComposeAfter(typeof(RoutingComposer))]`. Without it, the override still wins (DI's last-registration-wins rule applies) but the log line is non-deterministic.

### Lifetime guidance

Both interfaces are registered as `Transient` by default — the default extractor and selector may hold AngleSharp DOM state across an extraction call. You can register `Singleton` if your implementation is stateless and thread-safe; the DI container respects your declaration. Document the reasoning in your composer when going `Singleton`.

### Quality benchmark fixtures

The package ships [`ExtractionQualityBenchmarkTests`](../LlmsTxt.Umbraco.Tests/Extraction/ExtractionQualityBenchmarkTests.cs) — a parameterised NUnit test that iterates `LlmsTxt.Umbraco.Tests/Fixtures/Extraction/<scenario>/{input.html, expected.md}` pairs and pins the default extractor's output. Drift fails the test with a unified diff. v0.1 ships four scenarios:

- `clean-core-home` — strip selectors, GFM tables, fenced code, blockquote, image alt-drop, URL absolutification
- `clean-core-blog-list` — BlockList rich content with heading-inside-anchor lift
- `clean-core-blockgrid-cards` — BlockGrid card layout with multi-column grid items
- `clean-core-nested-tables-images` — long-form nested headings, GFM tables, figure/figcaption, `data-llms-ignore`

Adopters who fork the package can extend the catalogue with their own fixture pairs — see [`Fixtures/Extraction/README.md`](../LlmsTxt.Umbraco.Tests/Fixtures/Extraction/README.md) for the workflow.

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

This is standard ASP.NET Core deployment hygiene; LlmsTxt does not configure forwarded-headers itself because the trusted-proxy list is environment-specific. If you skip this step, single-domain deployments still work correctly — only the multi-domain per-host ETag/cache separation is affected.

## Cold-start cost

The first Markdown render of a given template JIT-compiles the Razor view — observed at ~6 seconds against Clean.Core 7.0.5 in the Story 0.A spike. Subsequent renders are 170–600 ms. **Story 1.2's cache absorbs this from the second hit onwards** — pre-warming on app startup is out of scope for v1.

If first-hit latency matters at deploy time, hit `/your-most-important-pages.md` once after a deployment so the JIT cache (and the LlmsTxt cache) are warm.

## Conditional GET (Story 2.3)

From v0.3, both `/llms.txt` and `/llms-full.txt` honour `If-None-Match` and respond with `304 Not Modified` for unchanged manifests. The `ETag` is content-derived (SHA-256 of the manifest body, base64-url-encoded, quoted as a strong validator) and travels alongside the cached body, so cache hits reuse the ETag without re-hashing. AI crawlers that revalidate (most do) save round-trip bytes; first-fetch latency is unchanged.

Per RFC 7232 § 6, `If-None-Match` wins over `If-Modified-Since` — the `/llms.txt` and `/llms-full.txt` endpoints ignore `If-Modified-Since` entirely (manifest cache is keyed by `(host, culture)`, not by timestamp; honouring it would invite stale-content false-positives).

## Hreflang variant suffixes (Story 2.3)

Set `LlmsTxt:Hreflang:Enabled: true` in `appsettings.json` to add sibling-culture variant suffixes to `/llms.txt` links:

```jsonc
{
  "LlmsTxt": {
    "Hreflang": { "Enabled": true }
  }
}
```

Off by default (FR25). When enabled, each link is followed by zero-or-more variant suffixes in the form `(culture: /culture/path.md)`, in BCP-47-lexicographic order. Single-culture sites see no change. Use this when adopters operate per-culture sub-paths or sub-domains and want cross-culture linkage discoverable to AI crawlers.

Hreflang is **only** applied to `/llms.txt`. `/llms-full.txt` is a single-culture concatenated dump consumed off-site as a self-contained body keyed to the matched `IDomain` binding; cross-culture variant linkage is meaningless there.

## Settings doctype + Backoffice (Story 3.1)

From v0.4, the package ships a `LlmsTxt Settings` Umbraco document type that editors can use to override site-name, site-summary, and per-doctype exclusion without editing `appsettings.json`. The doctype is created automatically on first boot via Umbraco's `PackageMigrationPlan` pipeline (the package registers `LlmsTxtSettingsMigrationPlan`, which runs a `CreateLlmsSettingsDoctype` step that calls `IContentTypeService.Save(...)` imperatively). Re-runs are no-ops — `EnsureSettingsDoctype` skip-checks `IContentTypeService.Get(alias)` before creating anything.

### Where to find it

After install, open Backoffice → Content. Create a new node from the root and pick **LlmsTxt Settings**. Fill in:

- **Site name** — the H1 emitted at the top of `/llms.txt`. Empty falls back to the matched root content node's name.
- **Site summary** — the blockquote under the `/llms.txt` H1. Soft-capped at 500 chars (truncated with ellipsis on overflow).
- **Excluded doctype aliases** — one alias per line (or comma/semicolon separated). Pages whose `ContentType.Alias` matches any line are omitted from `/llms.txt`, `/llms-full.txt`, and the `.md` route returns `404`.

### Per-page "Exclude from LLM exports" toggle

To let editors exclude individual pages, attach the **LlmsTxt Exclusion (composition)** doctype as a composition to any of your own doctypes (Backoffice → Settings → Document Types → your doctype → Add composition). The composition adds a single boolean property `excludeFromLlmExports` (default `false`). Toggling it `true` on a published page causes:

- `GET /that-page.md` → `404 Not Found`
- The page is omitted from `/llms.txt` and `/llms-full.txt`
- HTML responses for that page are unchanged (the toggle only affects the LLM exports — the public site keeps serving normally)

Cache invalidation is automatic: toggling the bool fires a `RefreshNode` notification that clears the per-page Markdown cache, both manifest caches, and the resolver settings cache for every bound hostname in a single invocation.

### Configuration overlay precedence

The package resolves effective settings in this order:

| Layer | Source | Wins over |
|---|---|---|
| Settings doctype | `LlmsTxt Settings` content node properties | appsettings + in-code defaults |
| `appsettings.json` | `LlmsTxt:` section | in-code defaults |
| In-code defaults | `LlmsTxtSettings` initialiser | — |

**Per-field**, not all-or-nothing — an empty `siteName` in the doctype falls back to `appsettings.json`'s `LlmsTxt:SiteName` (and then the in-code default). Same for `siteSummary`.

The `excludedDoctypeAliases` field is the **union** of `appsettings.json`'s `LlmsTxt:ExcludedDoctypeAliases` and the doctype's value — adopters' appsettings entries are never discarded by an editor edit.

### Two-list distinction (don't confuse)

| Field | Scope | Default |
|---|---|---|
| `LlmsTxt:ExcludedDoctypeAliases` (top-level) | All routes (`/llms.txt`, `/llms-full.txt`, `.md`) | `[]` |
| `LlmsTxt:LlmsFullScope:ExcludedDocTypeAliases` (Story 2.2) | `/llms-full.txt` only — further narrowing | `["errorPage", "redirectPage"]` |

The `/llms-full.txt` route applies BOTH filters cumulatively (logical AND-NOT). The `/llms.txt` and `.md` routes only apply the top-level list.

### uSync coexistence

If your site uses [uSync](https://github.com/KevinJump/uSync) or `uSync.Complete` to own the schema lifecycle, set:

```jsonc
{
  "LlmsTxt": {
    "Migrations": {
      "SkipSettingsDoctype": true
    }
  }
}
```

This stops the package from registering `LlmsTxtSettingsMigrationPlan` with Umbraco's migration pipeline. uSync then serialises the doctype on first install and owns the lifecycle. The resolver still works — it just falls back fully to appsettings when no `LlmsTxt Settings` content node exists.

**Caveat:** flipping this flag from `false` to `true` AFTER the migration has already run does NOT remove the doctype from the host DB (Umbraco's plan-state record persists the executed state). To relocate schema ownership to uSync, delete the doctype manually first.

### Resolver behaviour at request time

The resolver caches its overlay record at `llms:settings:{host}:{culture}` with TTL `LlmsTxt:SettingsResolverCachePolicySeconds` (default `300s`). Cache invalidation is broadcast-driven via Umbraco's distributed cache refresher — every bound hostname's settings entry is dropped on any `ContentCacheRefresherNotification`, just like the manifest caches. If the resolver throws (e.g. an adopter override misbehaves), the controllers fail-open: log a `Warning` and fall back to the `appsettings.json` snapshot (no doctype overlay, no exclusion list from the doctype). Same shape as Story 2.3's hreflang resolver-throw graceful degradation.

### Public extension point

`ILlmsSettingsResolver` is the fourth public extension point in the package (after `IMarkdownContentExtractor`, `ILlmsTxtBuilder`, `ILlmsFullBuilder`). Override discipline matches the others:

```csharp
// Override BEFORE our composer runs (any composer with no [ComposeAfter])
builder.Services.AddScoped<ILlmsSettingsResolver, MyResolver>();
// Or AFTER our composer
[ComposeAfter(typeof(SettingsComposer))]
public sealed class MyComposer : IComposer
{
    public void Compose(IUmbracoBuilder b)
        => b.Services.AddScoped<ILlmsSettingsResolver, MyResolver>();
}
```

**Lifetime: Scoped.** The default impl reads request-scoped `IUmbracoContextAccessor`. Adopters re-registering as `Singleton` will hit a captive-dependency exception on first request. The package's `Compose_StartupValidation_LlmsSettingsResolver_NoCaptiveDependency` test pins the contract using `ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }` — adopter overrides should follow the same discipline.

## Zero-config defaults (Story 3.3)

From v0.6, every route the package ships works zero-config — no `LlmsTxt:` section in `appsettings.json`, no Settings doctype edits required. The in-code defaults in [`LlmsTxtSettings`](../LlmsTxt.Umbraco/Configuration/LlmsTxtSettings.cs) produce useful output on a typical Umbraco site as soon as `dotnet add package LlmsTxt.Umbraco` finishes. The package never ships an `appsettings.json` snippet — adopters who want to override the defaults add a `LlmsTxt:` section to their host's own `appsettings.json` (or its environment-specific overlay).

| Route | Zero-config behaviour |
|---|---|
| `/llms.txt` | `# {root content node Name}` H1 (or `# Site` literal when no root resolves), no blockquote when no `siteSummary` is set, all published pages grouped under a single `## Pages` section |
| `/llms-full.txt` | Whole-site scope (no doctype narrowing), tree-order page emission, 5 MB body cap |
| `GET /{path}.md` | Per-page extracted Markdown (with frontmatter) for any published page; `404` for excluded pages |

### Effective defaults table

| Setting | Default | Source |
|---|---|---|
| `LlmsTxt:MaxLlmsFullSizeKb` | `5120` | [`LlmsTxtSettings.cs`](../LlmsTxt.Umbraco/Configuration/LlmsTxtSettings.cs) |
| `LlmsTxt:CachePolicySeconds` (per-page) | `60` | same |
| `LlmsTxt:LlmsTxtBuilder:CachePolicySeconds` | `300` | same |
| `LlmsTxt:LlmsTxtBuilder:PageSummaryPropertyAlias` | `"metaDescription"` | same |
| `LlmsTxt:LlmsFullBuilder:Order` | `TreeOrder` | same |
| `LlmsTxt:LlmsFullBuilder:CachePolicySeconds` | `300` | same |
| `LlmsTxt:LlmsFullScope:RootContentTypeAlias` | `null` (whole site) | same |
| `LlmsTxt:LlmsFullScope:IncludedDocTypeAliases` | `[]` | same |
| `LlmsTxt:LlmsFullScope:ExcludedDocTypeAliases` | `["errorPage", "redirectPage"]` | same |
| `LlmsTxt:ExcludedDoctypeAliases` (top-level) | `[]` | same |
| `LlmsTxt:Hreflang:Enabled` | `false` | same |
| `LlmsTxt:SettingsResolverCachePolicySeconds` | `300` | same |
| `LlmsTxt:Migrations:SkipSettingsDoctype` | `false` | same |

A drift-detection fixture ([`LlmsTxtSettingsDefaultsTests`](../LlmsTxt.Umbraco.Tests/Configuration/LlmsTxtSettingsDefaultsTests.cs)) pins every value above; bumping a default in source without updating the fixture fails CI.

### Site name fallback chain

The H1 emitted on `/llms.txt` resolves through the following layers, returning the first non-whitespace value found:

1. Settings doctype `siteName` field (editor-set, via the standard Umbraco content tree or the Backoffice Settings dashboard below)
2. `appsettings.json` `LlmsTxt:SiteName` value (developer-set)
3. The matched root `IPublishedContent.Name` (e.g. `"Home"` on a Clean.Core install)
4. The literal sentinel `"Site"` (used only when no root content node resolves at all — greenfield install before any content is created)

Layer 4 also covers the explicit-empty-string case: an editor clearing the `siteName` field falls through to layer 3 (the resolver and builder both treat empty/whitespace as "fall back").

## Backoffice Settings dashboard (Story 3.2)

From v0.5, the package ships a Backoffice dashboard under **Settings → LlmsTxt** that gives editors a form-based view of the same settings the standard Umbraco content tree exposes on the `LlmsTxt Settings` node — `siteName`, `siteSummary`, the doctype-alias exclusion list, and a read-only list of pages with `excludeFromLlmExports` toggled on.

The dashboard surfaces exactly the same overlay rules `ILlmsSettingsResolver` enforces (Settings node value > appsettings value > in-code default). On first save, if no `LlmsTxt Settings` content node exists yet, the dashboard creates one at the root automatically — adopters using uSync to own the doctype lifecycle don't need to pre-create the node before opening the dashboard.

**Permissions.** The dashboard is conditioned on `Umb.Section.Settings`. Editors without Settings-section access don't see the tile (UX-DR4 — graceful no-render). The Management API behind the dashboard (`/umbraco/management/api/v1/llmstxt/settings/...`) is gated by `[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]`; calls without Settings access return HTTP 403, calls without any auth return HTTP 401.

**Bearer-token auth.** The dashboard's Management API calls use `UMB_AUTH_CONTEXT.getOpenApiConfiguration()` to obtain a bearer token per call (cookie-only fetches against `/umbraco/management/api/...` return HTTP 401 — the Management API enforces OpenIddict bearer auth, not cookies). Adopters scripting the Management API for CI-driven config (e.g. setting site name from a deploy pipeline) must follow the same pattern.

**Save flow.** Save writes through `IContentService.Save` + `IContentService.Publish`. Umbraco's standard `ContentCacheRefresherNotification` fires; Story 3.1's handler clears `llms:settings:` so the next `/llms.txt` request sees the new values without manual cache-bust. The dashboard round-trips the published values back into the form.

**Validation.** `siteSummary` is capped at 500 characters (counter shown beneath the textarea); `excludedDoctypeAliases` cannot contain whitespace-only entries or case-insensitive duplicates. The dashboard validates client-side; the server re-validates as defence-in-depth and returns `400 ProblemDetails` for direct API callers that bypass the form.

**Management API surface.**

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/umbraco/management/api/v1/llmstxt/settings/` | Returns the resolved overlay + the live Settings node key |
| `PUT` | `/umbraco/management/api/v1/llmstxt/settings/` | Validates + persists + publishes; returns the round-tripped record |
| `GET` | `/umbraco/management/api/v1/llmstxt/settings/doctypes` | Lists publish-eligible doctypes for the multi-select source |
| `GET` | `/umbraco/management/api/v1/llmstxt/settings/excluded-pages?skip=0&take=100` | Read-only page list (clamped to take ≤ 200) |

Operations land in the existing `/umbraco/swagger/llmstxtumbraco/swagger.json` Swagger doc.

### First-run onboarding hint (Story 3.3)

On first visit to the LlmsTxt Settings dashboard, an info-level notice appears at the top reminding adopters that the package is already producing default output ("LlmsTxt is now active and producing default output…"). Click `Dismiss` to hide it for your user account; the notice is **per-user**, keyed by your Backoffice user `unique` GUID, and survives browser refreshes and re-logins.

Storage backing: `localStorage` keyed by `llms.onboarding.dismissed.v1.{userUnique}`. The `v1.` segment lets a future onboarding scheme (Story 5.2's auto-hide tying into AI traffic logs) ship a parallel key without ambiguity over which version dismissed a given user. Adopters running storage-disabled browser modes (incognito with site-data blocking) will see the notice each session — the dismiss handler degrades gracefully (no exception, no broken UI) but the flag does not persist across sessions in that mode.

The notice does **not** auto-hide after the first AI-traffic-dashboard request — that auto-hide is deferred to Story 5.2 (AI traffic Backoffice dashboard, Epic 5). Until that lands, the dismiss button is the only signal.

## Discoverability headers + Razor TagHelpers (Story 4.1)

From v0.7, every opted-in HTML response carries an automatic HTTP `Link: </path.md>; rel="alternate"; type="text/markdown"` header so AI crawlers can find the Markdown alternate without URL guessing. Two optional Razor TagHelpers (`<llms-link />` and `<llms-hint />`) emit the equivalent in document markup for adopters who want body-side discoverability or visually-hidden hint text. See [`docs/data-attributes.md`](data-attributes.md) for the copy-paste setup.

Two additional response headers ride the existing Markdown writer (Story 1.3):

- **`X-Markdown-Tokens: <integer>`** — character-based estimate of the body's token count, emitted on every 200 Markdown response (omitted on 304 — body-derived).
- **`Content-Signal: <directives>`** — Cloudflare's content-use policy header. Off by default; configurable per-site and per-doctype (see [`docs/data-attributes.md`](data-attributes.md) for `appsettings` shape).

The MCP Server Card / Agent Skills / WebMCP / OAuth / commerce-protocol surfaces that the [`isitagentready.com`](https://isitagentready.com) scanner checks for are intentionally NOT shipped — they're concerns of the application layer (what services the site offers, who the agent is, how transactions clear), not the content-rendering middleware. See [`docs/data-attributes.md` § Out of scope (intentionally)](data-attributes.md#out-of-scope-intentionally) for the full list and rationale.

## Robots audit Health Check (Story 4.2)

From v0.8, the Backoffice **Settings → Health Check → LLMs** group surfaces a `LLMs robots.txt audit` check that fetches your site's `/robots.txt` and cross-references it against the [`ai-robots-txt/ai.robots.txt`](https://github.com/ai-robots-txt/ai.robots.txt) AI-crawler list. Findings appear grouped by category (training / search-retrieval / user-triggered / opt-out) with copy-pasteable suggested removals.

**The package never modifies your `/robots.txt`** — it audits and surfaces, you decide. See [`docs/robots-audit.md`](robots-audit.md) for the full contract.

| Setting | Default | Effect |
|---|---|---|
| `LlmsTxt:RobotsAuditOnStartup` | `true` | Run a one-shot audit at host startup (per scheduling-publisher / single-instance role only — multi-front-end installs don't all hammer their own origin). |
| `LlmsTxt:RobotsAuditor:RefreshIntervalHours` | `24` | Recurring refresh cadence via Umbraco's `IDistributedBackgroundJob` (exactly-once across a load-balanced deployment). Set to `0` to disable the recurring refresh; the on-demand Health Check view still works. |
| `LlmsTxt:RobotsAuditor:FetchTimeoutSeconds` | `5` | Per-host `/robots.txt` fetch timeout (seconds). Clamped to `[1, 60]` at consumption. |
| `LlmsTxt:RobotsAuditor:DevFetchPort` | `null` | **Dev-only escape hatch.** Overrides the scheme default port for the audit fetch (e.g. `44314` for a TestSite on Kestrel). **DO NOT set in production** — production deploys serve `/robots.txt` on 443/80. Live in `appsettings.Development.json` only. |
| `LlmsTxt:RobotsAuditor:RefreshIntervalSecondsOverride` | `null` | **Dev-only escape hatch.** Forces seconds-precision refresh cycles instead of `RefreshIntervalHours`. Used by the architect-A5 two-instance shared-SQL-Server exactly-once gate. **DO NOT set in production** — would hammer adopter origins. Live in `appsettings.Development.json` only. |

The AI-bot list is **synced from upstream at build time** with SHA pinning — the build hard-fails on a SHA mismatch (deliberate; protects against silent feed tampering). Offline / disconnected builds fall back to the committed snapshot at `LlmsTxt.Umbraco/HealthChecks/AiBotList.fallback.txt` with a warning. See [`docs/maintenance.md`](maintenance.md) for the SHA-refresh process.

Adopters can replace the auditor entirely by registering a Singleton `IRobotsAuditor` of their own (Singleton lifetime is required — see [`docs/robots-audit.md` § Custom auditors](robots-audit.md#custom-auditors)).

## Upgrading from v0.7 to v0.8

Story 4.2 is **non-breaking** for adopters. The robots audit ships as net-new surface; existing routes, headers, and DI shapes are unchanged. Notable new artefacts:

- New `LlmsTxt.Umbraco/HealthChecks/` and `LlmsTxt.Umbraco/Background/` namespaces.
- New build-time MSBuild target `SyncAiBotList` — embeds an AI-bot list resource into the assembly. Online + offline build paths both work; see `docs/maintenance.md`.
- New `IRobotsAuditor` extension point. Adopters wanting custom audit semantics override via `services.AddSingleton<IRobotsAuditor, MyImpl>()`.
- New `LlmsTxt:RobotsAuditOnStartup` + `LlmsTxt:RobotsAuditor:*` configuration keys.

## Upgrading from v0.6 to v0.7

Story 4.1 is a **breaking** v0.7 release for adopters who have customised the Markdown response pipeline. The full set of changes is in [`CHANGELOG.md`](../CHANGELOG.md); the load-bearing items:

- **`MarkdownController` constructor signature changed.** Two new dependencies (`ILlmsExclusionEvaluator`, `IOptionsMonitor<LlmsTxtSettings>`) and the private `IsExcludedAsync`/`TryReadExcludeBool` helpers were removed in favour of the shared `ILlmsExclusionEvaluator` seam. Adopters who subclass or service-locate `MarkdownController` directly will fail to compile until they update; the controller is intended as the package's own surface, not an extension seam, so the breaking change is loud-by-design.
- **`IMarkdownResponseWriter.WriteAsync` gained a 4th parameter `string? contentSignal`.** A 3-arg overload remains available as `[Obsolete]` (it forwards to the 4-arg version with `contentSignal: null`) so adopters who *call* the interface keep working with a deprecation warning. Adopters who *implement* the interface must add the 4-arg overload and will lose Content-Signal emission until they wire it through.
- **New `Link: rel="alternate"` HTTP header on every opted-in HTML response.** Add `LlmsTxt:DiscoverabilityHeader:Enabled: false` to suppress globally. See [`data-attributes.md`](data-attributes.md#http-link-discoverability-header) for the full gating contract.
- **New `<llms-link />` and `<llms-hint />` Razor TagHelpers.** Opt-in via `_ViewImports.cshtml` — see [`data-attributes.md`](data-attributes.md#razor-taghelpers--llms-link--and-llms-hint-).
- **New `Content-Signal` configurable Markdown response header.** Off by default; configure under `LlmsTxt:ContentSignal:*`.
- **New `ILlmsExclusionEvaluator` public seam.** The default `DefaultLlmsExclusionEvaluator` is `public sealed` — adopters can wrap-and-delegate via the DI Decorator pattern. See [`data-attributes.md` § Customising the exclusion contract](data-attributes.md#customising-the-exclusion-contract).
- **New RCL static asset at `/llms-txt-umbraco.css`.** Optional; needed only if you use `<llms-hint />` and don't already ship a `.visually-hidden` (or equivalent) class.

Single-route adopters (only consuming `/llms.txt`, `/llms-full.txt`, or `.md` URLs without subclassing or implementing the package interfaces) need no code changes.

## What's not in v0.8

Coming in later epics:

- Request log + AI traffic dashboard (Epic 5)
- Auto-hide of the Settings-dashboard onboarding notice once the AI traffic dashboard has logged at least one request (Story 5.2)
- v1.0 NuGet release readiness (Epic 6)

## Anti-patterns the package will NOT ship

These are common asks that the package explicitly refuses:

- User-Agent sniffing for Markdown delivery (cloaking; Google penalty)
- `<meta name="llms">` injection (rejected by WHATWG)
- `/.well-known/ai.txt` (no spec, no consensus)
- AI/human toggle UI button (decorative; AI doesn't click)
- `rel="canonical"` from `.md` back to HTML (Cloudflare and Evil Martians both say no)
- Property-walking content registry (architectural principle: the Umbraco template is the canonical visual form of content)
