# Getting started with LlmsTxt.Umbraco

LlmsTxt.Umbraco exposes Umbraco published content to AI crawlers and large-language-model search engines via per-page Markdown rendering, a `/llms.txt` index, and a `/llms-full.txt` bulk export.

This document covers what ships in **v0.1 (Stories 1.1 + 1.2 + 1.3 + 1.4)** — the per-page Markdown route, per-page caching with publish-driven invalidation, `Accept: text/markdown` content negotiation on canonical URLs, and the public `IMarkdownContentExtractor` / `IContentRegionSelector` extension points with a parameterised quality-benchmark fixture catalogue. The manifests and the Backoffice surface land in later stories.

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
| **Cache key** | `llms:page:{nodeKey:N}:{culture}` — `nodeKey` is `IPublishedContent.Key` (Guid), culture is BCP-47 lowercased (or `_` for invariant content). |
| **TTL** | Configured by `LlmsTxt:CachePolicySeconds`; default `60` seconds. Set to `0` to disable caching. |
| **Invalidation** | `INotificationAsyncHandler<ContentCacheRefresherNotification>` keyed off Umbraco's distributed cache refresher — fires on every load-balanced instance independently when content publishes, moves, or unpublishes. The handler walks branch descendants via `IDocumentNavigationQueryService` for branch-publish events. |
| **Per-instance** | The cache and its node-to-key index are per-process in-memory. Cross-instance invalidation works via the broadcast notification — no Redis or shared state required. |

### HTTP response headers

Successful `.md` responses carry:

- `Cache-Control: public, max-age={CachePolicySeconds}` — `public` because Markdown is stateless.
- `Vary: Accept` — required so downstream caches don't return Markdown to a caller that sent `Accept: text/html`. Now load-bearing: Story 1.3's content negotiation means the same canonical URL can return either Markdown or HTML.
- `ETag: "<hash>"` — strong validator computed from `(route + culture + contentVersion)`, where `contentVersion` is the page's `IPublishedContent.UpdateDate`. Every successful publish bumps it.
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

## Cold-start cost

The first Markdown render of a given template JIT-compiles the Razor view — observed at ~6 seconds against Clean.Core 7.0.5 in the Story 0.A spike. Subsequent renders are 170–600 ms. **Story 1.2's cache absorbs this from the second hit onwards** — pre-warming on app startup is out of scope for v1.

If first-hit latency matters at deploy time, hit `/your-most-important-pages.md` once after a deployment so the JIT cache (and the LlmsTxt cache) are warm.

## What's not in v0.1

Coming in later epics:

- `/llms.txt` and `/llms-full.txt` site manifests (Epic 2)
- Settings doctype + Backoffice dashboard (Epic 3)
- HTTP `Link` discoverability header + Razor TagHelpers + robots.txt audit (Epic 4)
- Request log + AI traffic dashboard (Epic 5)
- v1.0 NuGet release readiness (Epic 6)

## Anti-patterns the package will NOT ship

These are common asks that the package explicitly refuses, with rationale documented in [`_bmad-output/planning-artifacts/package-spec.md` § 15](../_bmad-output/planning-artifacts/package-spec.md):

- User-Agent sniffing for Markdown delivery (cloaking; Google penalty)
- `<meta name="llms">` injection (rejected by WHATWG)
- `/.well-known/ai.txt` (no spec, no consensus)
- AI/human toggle UI button (decorative; AI doesn't click)
- `rel="canonical"` from `.md` back to HTML (Cloudflare and Evil Martians both say no)
- Property-walking content registry (architectural principle: the Umbraco template is the canonical visual form of content)
