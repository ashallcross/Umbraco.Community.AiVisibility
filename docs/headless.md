# Headless Umbraco — what works today, what's coming

A common question from adopters running Umbraco in headless mode (Delivery API + an external frontend on Vercel / Netlify / Cloudflare Pages / etc.) is whether this package works against their setup. The honest answer is **partially today, fully in a future release**. This page covers exactly what's supported, what isn't, and where the boundary sits — so you can make an informed call before installing.

## TL;DR

| Setup | Markdown rendering (`.md`, `/llms-full.txt`) | `/llms.txt` index | AI Traffic dashboard | Robots audit + Settings dashboard |
|---|---|---|---|---|
| Traditional Umbraco (Razor templates on the backend) | ✅ Full | ✅ Full | ✅ Full | ✅ Full |
| Umbraco with hijacked `RenderController` + custom ViewModels | ✅ Full (via the dual-strategy renderer — see [`configuration.md`](configuration.md#aivisibilityrenderstrategy--page-rendering-for-hijacked-content)) | ✅ Full | ✅ Full | ✅ Full |
| Fully headless (Delivery API + Vercel/Next/Astro frontend) | ❌ Not in current release | ⚠️ Partial — URLs only | ❌ — crawlers hit the frontend, not Umbraco | ✅ Full |

If you're running a fully-headless setup, the package's Markdown surface is not useful to you yet. **Full headless support is on the roadmap as the first post-v1 feature** — see "What's coming" below.

## Why the rendering surfaces don't work in headless

The package's design principle is that the **rendered HTML output of a page is the canonical source for its Markdown form**. This is deliberate — the alternative (walking properties and reconstructing the page) duplicates every adopter's template logic and breaks the moment someone customises a partial.

To extract Markdown the package needs HTML. In a traditional Umbraco install the HTML comes from Razor templates served by the same .NET process the package is installed in. In a fully-headless install the HTML lives on Vercel/Netlify/Cloudflare/etc. — the Umbraco backend never renders pages. The package's renderer has nothing to extract from.

This affects three surfaces:

- **`/{any-page}.md`** — the renderer has no HTML to convert.
- **`/llms-full.txt`** — needs per-page Markdown bodies, which need rendering.
- **`Accept: text/markdown` content negotiation** — triggers the same rendering path.

Two surfaces partially work:

- **`/llms.txt` index** — just a list of titles + URLs + summaries; no body rendering. It will produce output, but the URLs will point at whatever host the Umbraco backend is bound to. You'll likely want to override `ILlmsTxtBuilder` or configure hostname binding so the URLs point at your public frontend host.
- **AI Traffic dashboard** — the dashboard works, but it can only log requests that hit the Umbraco backend's `.md` route. Since AI crawlers fetch the frontend, the dashboard will show no AI traffic in a headless setup.

Two surfaces work regardless:

- **Robots audit Health Check** — pure HTTP fetch of `/robots.txt`. Configure it against your frontend host and it works fine.
- **Settings dashboard** — pure config UI. No rendering involved.

## What's coming — headless rendering strategy

The package's renderer ships today with three modes (`Razor`, `Loopback`, `Auto` — see [`configuration.md`](configuration.md#aivisibilityrenderstrategy--page-rendering-for-hijacked-content)) built around a strategy pattern. A **headless rendering strategy** slots in cleanly as a fourth mode:

```
RenderStrategy:Mode = Headless
RenderStrategy:HeadlessBaseUrl = https://www.your-frontend.com
```

In Headless mode the renderer will:

1. Resolve the published content's path via Umbraco's normal `IPublishedUrlProvider`
2. Substitute the configured frontend base URL for the host
3. Issue an HTTP GET to `https://www.your-frontend.com/some-page` with `Accept: text/html`
4. Run the same extraction pipeline (AngleSharp + region selection + SmartReader fallback + ReverseMarkdown) against the response
5. Cache the result via the same publish-driven invalidation as the existing renderer

Everything downstream — the `*.md` route, `/llms.txt` + `/llms-full.txt` builders, caching, content negotiation, exclusion rules — works unchanged. Only the rendering layer is new.

**The AI Traffic dashboard remains a separate concern in headless setups.** When crawlers hit Vercel they bypass the Umbraco backend entirely; the dashboard can't see them. The future-work options there are (a) an edge function on the frontend that POSTs to a backend analytics endpoint, or (b) an `IRequestLog` override that reads from your existing analytics platform (Plausible, GA, Cloudflare Analytics, etc.). Both are realistic — adopters who replace `IRequestLog` are already a documented extension point.

## Workarounds for v1

If you're determined to ship the package against a headless setup before v1.1 lands, two paths work today:

### 1. Override `IMarkdownContentExtractor`

The extraction layer is an extension point. Register your own implementation:

```csharp
public class HeadlessMarkdownContentExtractor : IMarkdownContentExtractor
{
    private readonly HttpClient _client;
    // ...

    public async Task<MarkdownContentExtractionResult> ExtractAsync(
        IPublishedContent content,
        string? culture,
        CancellationToken cancellationToken)
    {
        // Fetch the rendered HTML from your frontend
        var frontendUrl = $"https://www.your-frontend.com{content.Url(culture)}";
        var html = await _client.GetStringAsync(frontendUrl, cancellationToken);

        // Reuse the package's existing pipeline against the fetched HTML.
        // (Inject the package's DefaultMarkdownContentExtractor here and delegate
        // its "ExtractFromHtmlAsync" seam, OR roll your own AngleSharp + ReverseMarkdown
        // shape.)
    }
}

// In a composer
services.TryAddTransient<IMarkdownContentExtractor, HeadlessMarkdownContentExtractor>();
```

This is the most architecturally clean path — you're swapping one strategy for another at the documented seam.

### 2. Use only the surfaces that work

Run with default settings, accept that the `.md` and `/llms-full.txt` routes won't be useful, and use the package for:

- The `/llms.txt` index (with appropriate hostname overrides so the URLs in the index point at your frontend)
- The robots.txt audit Health Check
- The Settings dashboard

Then ship the v1 surfaces that work for you, and switch on the full Markdown rendering surface when the Headless strategy lands.

## When will the Headless strategy ship?

No fixed date — it's on the roadmap, prioritised against other post-v1 work based on actual adopter demand. If headless Umbraco is your setup and you'd benefit from this strategy landing sooner, [file an issue](https://github.com/ashallcross/Umbraco.Community.AiVisibility/issues) saying so. Demand signal moves the priority.

## Related reading

- [`docs/configuration.md`](configuration.md) — the full settings reference (`RenderStrategy:Mode` ships today with `Razor`/`Loopback`/`Auto`; the Headless strategy will add a fourth `Headless` mode + `RenderStrategy:HeadlessBaseUrl` when it ships)
- [`docs/extension-points.md`](extension-points.md) — `IMarkdownContentExtractor` extension contract for the workaround path
- [`docs/getting-started.md`](getting-started.md) — full per-feature documentation for the v1 surface
