---
title: Architecture deep-dive
url: https://example.test/clean-core-nested-tables-images
updated: 2026-04-29T12:00:00Z
---
# Architecture deep-dive

A long-form walk through the package's runtime topology, with the trade-offs we considered along the way.

![Diagram of the runtime topology including Umbraco frontends, the host database, and the cache refresher broadcast](https://example.test/media/topology-diagram.png)

Figure 1 — Runtime topology in a load-balanced deployment.

## Request pipeline

Two surfaces serve Markdown: the suffix route and the canonical-URL Accept-header negotiation middleware.

### The .md suffix route

A custom route registered via `UmbracoPipelineFilter.Endpoints` handles every `GET /{path}.md`. It resolves `IPublishedContent` via `IPublishedRouter`, then delegates to the extractor.

#### Resolution edge cases

URL-encoded paths decode before lookup; trailing-slash and `index.html.md` variants normalise to a single canonical form.

### Accept-header negotiation

A `PostRouting` middleware reads `UmbracoRouteValues` from `HttpContext.Features` and, when the client prefers `text/markdown`, calls the same extractor + cache decorator the suffix route uses.

#### q-value resolution

Highest quality wins; ties break on first-listed; `*/*` is treated as no-preference and HTML is served.

## Caching topology

One in-memory cache per instance, invalidated by Umbraco's `ContentCacheRefresherNotification`.

| Cache key | Lifetime | Invalidator |
| --- | --- | --- |
| `llms:page:{nodeKey}:{culture}` | Configurable TTL (default 60s) | Per-node refresh |
| `llms:llmstxt:{hostname}:{culture}` | Pessimistic clear on any node change | Hostname-scoped |
| `llms:llmsfull:{hostname}:{culture}` | Pessimistic clear on any node change | Hostname-scoped |

The cache key deliberately omits the content version. Every successful publish triggers an invalidation event, so the entry under a stable key is always the latest version. Including the version would just leak stale entries that never get cleared.

### ETag derivation

The ETag *does* include the content version. Clients use it for `If-None-Match` revalidation, and the package emits `304 Not Modified` for matching tokens.

![Sequence diagram of an If-None-Match revalidation against a cached Markdown response](https://example.test/media/etag-flow.png)

Figure 2 — Revalidation flow with conditional GET.

## Extension points

Two layered DI seams cover the extraction pipeline.

| Interface | Scope | Default impl |
| --- | --- | --- |
| `IMarkdownContentExtractor` | Full pipeline | `DefaultMarkdownContentExtractor` |
| `IContentRegionSelector` | Region selection only | `DefaultContentRegionSelector` |

### When to override which

If you need a different HTML parser or Markdown converter, override `IMarkdownContentExtractor` and accept that the package's caching decorator will *not* wrap your extractor. If you only need to change which DOM region is selected, override `IContentRegionSelector` and keep the rest of the pipeline.

## What's deliberately excluded

- User-Agent sniffing (cloaking; Google penalty)
- `rel="canonical"` from `.md` back to HTML (Cloudflare and Evil Martians both say no)
- Property-walking content reconstruction (the template is the canonical visual form)
