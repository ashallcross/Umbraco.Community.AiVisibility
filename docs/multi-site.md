# Multi-site + multi-culture guide

This page covers what the package does on multi-host + multi-culture Umbraco installs: how it discovers hostnames, how it routes requests across cultures, and the per-host cache-key shapes adopters can rely on.

## Hostname discovery

The package reads `IDomainService.GetAll(true)` once per request lifecycle and matches the request's `Host` header against the registered domains. This is Umbraco's canonical multi-site mechanism — we don't introduce any `appsettings`-level host-to-root mapping.

Each `IDomain` row binds a content tree root to one `(hostname, culture)` pair:

```
Domains:
  www.acme.com         → /home (en-US)
  fr.acme.com          → /accueil (fr-FR)
  www.acme.co.uk       → /home (en-GB)
```

A request for `https://www.acme.com/about.md` resolves to the `www.acme.com` root + `en-US` culture; the per-page Markdown extractor uses that culture for `IPublishedUrlProvider.GetUrl(content, UrlMode.Absolute, culture)`, so the frontmatter `url:` key always reflects the request's culture even on multilingual installs.

## Per-host cache keys

Cache entries are keyed by hostname so multi-domain bindings against the same content node never collide on a CDN/proxy fronting both hosts:

```
Per-page Markdown:    aiv:page:{nodeKey:N}:{host}:{culture}
/llms.txt manifest:   aiv:llmstxt:{host}:{culture}
/llms-full.txt:       aiv:llmsfull:{host}:{culture}
Robots audit:         aiv:robots:{host}
Settings overlay:     aiv:settings:{culture}             (host-omitted — see below)
```

Hostname normalisation rules (see `Umbraco.Community.AiVisibility/Caching/AiVisibilityCacheKeys.cs:NormaliseHost`):

| Input | Cache-key host segment |
|---|---|
| `SiteA.Example` (uppercase) | `sitea.example` |
| `sitea.example:443` (with port) | `sitea.example` |
| `[::1]:443` (bracketed IPv6 + port) | `[::1]` (preserved verbatim, port stripped) |
| `[::1]` (bracketed IPv6, no port) | `[::1]` |
| `::1` (bare IPv6, no port — RFC 7230 violation but defensive) | `::1` |
| `fe80::1` (multi-segment bare IPv6) | `fe80::1` |
| `null` / empty / whitespace | `_` (sentinel) |

The same `NormaliseHost` is reused by the ETag computation in `MarkdownResponseWriter` so `If-None-Match` revalidation works correctly across host casings.

## Multi-culture story

The package treats culture as a request-time concern, not a config-time concern. There's no "list of supported cultures" you set in `appsettings.json`; the supported cultures are exactly the cultures Umbraco's `IDomainService` reports for your registered domains.

For each request, the culture chain is:

1. `IPublishedRequestBuilder.SetCulture(...)` from `IDomainService` matching → the canonical culture for the request.
2. `PageRenderer.RenderAsync(content, absoluteUri, culture, ct)` runs the Razor template under that culture's `CultureInfo.CurrentUICulture`.
3. `DefaultMarkdownContentExtractor.ResolveAbsoluteContentUrl(content, requestUri, culture)` resolves the `url:` frontmatter key under the request's culture.

Behaviour examples on a `(en-US, fr-FR)` site at `www.acme.com` (en-US default):

```
GET https://www.acme.com/about.md
  → frontmatter url: https://www.acme.com/about/      (en-US, default culture)

GET https://fr.acme.com/a-propos.md
  → frontmatter url: https://fr.acme.com/a-propos/    (fr-FR culture binding)

GET https://www.acme.com/cy/about.md   (Welsh culture variant via Umbraco URL routing)
  → frontmatter url: https://www.acme.com/cy/about/   (cy culture passed through)
```

## Hreflang sibling-culture suffixes

Opt-in via `AiVisibility:Hreflang:Enabled = true`. When enabled, `/llms.txt` emits sibling-culture variant suffixes after each link, in BCP-47 lexicographic order:

```
- [About](/about.md): About this site (fr-fr: /fr/about.md) (de-de: /de/about.md)
```

Hreflang is **only** applied to `/llms.txt`. `/llms-full.txt` is a single-culture concatenated dump scoped to the matched `IDomain` — cross-culture variant linkage is meaningless inside a body that's already culture-scoped.

## Single-Settings-node design (host-omitted resolver cache)

There is exactly **one** `aiVisibilitySettings` content node per Umbraco install — applied to every host bound to that install. The resolver cache key omits host (`aiv:settings:{culture}`) because the resolved snapshot is identical for every host; a host segment would just produce N duplicate cache entries.

Adopters who need per-host or per-tenant overrides supply their own `ISettingsResolver` implementation:

```csharp
public sealed class PerTenantSettingsResolver : ISettingsResolver
{
    public Task<ResolvedAiVisibilitySettings> ResolveAsync(string? host, string? culture, CancellationToken ct)
    {
        // ... walk the request's tenant identity, return per-tenant snapshot ...
    }
}

// In a composer:
builder.Services.AddTransient<ISettingsResolver, PerTenantSettingsResolver>();
```

`services.TryAdd*` is honoured for every default — adopter overrides take effect without `Remove<>()` ceremony. See `docs/extension-points.md` for the full per-interface contract.

## Robots audit on multi-host installs

`RobotsAuditRefreshJob` (an `IDistributedBackgroundJob` — single-runner across the cluster) walks every distinct hostname returned by `IDomainService.GetAll(true)` and runs the audit against `https://{host}/robots.txt`. Each host's audit lands in its own cache entry (`aiv:robots:{host}`); Backoffice → Health Checks → AI Visibility — Robots audit shows one row per host.

The audit reuses the request pipeline's outbound HTTP client with SSRF defence enabled (RFC1918 / loopback / link-local / cloud-metadata IPs refused). The fetcher is also configured with `AllowAutoRedirect = false` and rejects 3xx responses in-app to defend against redirect-based SSRF amplification.

## Cache invalidation across hosts

When `ContentCacheRefresherNotification` fires for a node:

1. `ContentCacheRefresherHandler` calls `CacheKeyIndex.GetEntries(nodeKey)` to find every per-host cache key warmed for that node.
2. Each per-host entry is cleared by exact key (`aiv:page:{nodeKey:N}:{host}:{culture}`).
3. Manifest invalidation uses prefix-clear (`ClearByKey(aiv:llmstxt:{host}:)`) per affected host — drops every culture's manifest entry for that hostname in one call.

The multi-host + multi-culture paths are exercised by the package's integration tests against `sitea.example` + `siteb.example` (multi-host) and the Welsh `/cy/` variant URL (multi-culture).

## See also

- `docs/configuration.md` — full per-key reference for the `AiVisibility:` section.
- `docs/extension-points.md` — `ISettingsResolver` adopter contract (replace resolver for per-tenant overrides).
- `docs/data-attributes.md` — `data-llms-content` / `data-llms-ignore` extraction-region attributes.
- Architecture document § Multi-Site & Multi-Language — the canonical design source for everything above.
