# Data attributes, discoverability headers, and Razor TagHelpers

This page documents the adopter-facing markup hooks that Umbraco.Community.AiVisibility ships:

- **`data-llms-content` / `data-llms-ignore`** — extraction-region opt-in / opt-out attributes.
- **HTTP `Link` discoverability header** — auto-emitted on every opted-in HTML response.
- **`<llms-link />` and `<llms-hint />` Razor TagHelpers** — optional body-side discoverability markup.
- **Cloudflare "Markdown for Agents" alignment** — `X-Markdown-Tokens` and `Content-Signal` response headers.

---

## `data-llms-content` and `data-llms-ignore`

The default Markdown extractor walks an opt-in chain when picking the page's main content region: `[data-llms-content]` → `<main>` → `<article>` → `AiVisibility:MainContentSelectors` (configured) → SmartReader fallback.

Add `data-llms-content` to the element you want extracted, in any view or partial:

```cshtml
<section data-llms-content>
    @Html.GetGridHtml(Model, "bodyText")
</section>
```

Add `data-llms-ignore` to any descendant inside the selected region that should be stripped from extraction (cookie banners, related-posts asides, social-share blocks):

```cshtml
<aside data-llms-ignore>
    <h3>Related posts</h3>
    @* this block won't appear in the .md output *@
</aside>
```

Both attributes work without configuration. The package never modifies your HTML — it only reads these as extraction hints.

---

## HTTP `Link` discoverability header

On every opted-in HTML response, the package emits:

```
Link: </home.md>; rel="alternate"; type="text/markdown"
Vary: Accept
```

The `href` is computed by a single rule: trailing-slash URLs (`/blog/`) get `/index.html.md` appended (per the [llms.txt trailing-slash convention](https://llmstxt.org)); everything else gets `.md` appended.

### When the header is suppressed

- Non-content responses (404 / 5xx, static assets, Backoffice URLs, surface controllers, MVC routes that don't resolve to `IPublishedContent`).
- `.md` and `/index.html.md` self-requests (the response IS the Markdown body).
- Pages excluded via the per-page `excludeFromLlmExports` toggle OR pages whose doctype alias is in the resolved `AiVisibility:ExcludedDoctypeAliases` list. The exclusion list is configurable in `appsettings.json` (see [Getting Started § Settings doctype + Backoffice](getting-started.md) for the resolution overlay) — top-level scope applies to the `Link` header, the `.md` route, `/llms.txt`, and `/llms-full.txt`.
- Pages flagged via Umbraco Public Access (member-restricted access). The default `IExclusionEvaluator` consults `IPublicAccessService.IsProtected(content.Path)`; the suppression applies to the `Link` header, the `.md` route, `/llms.txt`, and `/llms-full.txt`.
- When `AiVisibility:DiscoverabilityHeader:Enabled` is set to `false` (kill switch — see below).

### Kill switch

Add the following to `appsettings.json` to suppress the header globally:

```json
{
  "AiVisibility": {
    "DiscoverabilityHeader": {
      "Enabled": false
    }
  }
}
```

The flag is read live via `IOptionsMonitor` — flipping it at runtime takes effect on the next request without a restart.

---

## Razor TagHelpers — `<llms-link />` and `<llms-hint />`

The TagHelpers are **opt-in** alternatives to the auto-emitted HTTP `Link` header. Use them when you want the Markdown alternate to surface in the rendered HTML body (for body-side discoverability or for AI tools that scan visible content).

### Setup

Add the package's TagHelper namespace to your `_ViewImports.cshtml`:

```cshtml
@addTagHelper Umbraco.Community.AiVisibility.LlmsTxt.*, Umbraco.Community.AiVisibility
```

Both TagHelpers are auto-discovered once the directive is in scope. No `services.AddXxx()` call required. The namespace scope (rather than wildcard `*, Umbraco.Community.AiVisibility`) is deliberate so future internal types in other namespaces don't auto-register as adopter-facing TagHelpers.

### `<llms-link />` — emits `<link rel="alternate">` in `<head>`

Place inside the `<head>` of your layout:

```cshtml
<head>
    <!-- ...other <link> / <meta> tags... -->
    <llms-link />
</head>
```

Renders:

```html
<link rel="alternate" type="text/markdown" href="/home.md" />
```

The TagHelper renders nothing when the active request isn't an Umbraco-routed page (surface controllers, custom MVC views, Backoffice screens) or when the page is excluded.

### `<llms-hint />` — visually-hidden body hint

Place anywhere in the body (typically near the page title or top of `<main>`):

```cshtml
<body>
    <llms-hint />
    <!-- ...page content... -->
</body>
```

Renders:

```html
<div class="llms-hint" role="note">
    This page is also available as Markdown at <a href="/home.md" rel="alternate" type="text/markdown">/home.md</a>.
</div>
```

Visually hidden via the `llms-hint` CSS class — sighted users don't see it, but screen readers announce it as supplemental content (`role="note"`) and AI tools that scan body text find the alternate URL.

#### Optional CSS stylesheet

The package ships `wwwroot/umbraco-community-aivisibility.css` as an RCL static asset containing the `.llms-hint` visually-hidden ruleset. Add this to your layout's `<head>` if you don't already have a `.visually-hidden` (or equivalent) class in your own CSS:

```cshtml
<link href="/umbraco-community-aivisibility.css" rel="stylesheet" />
```

CSS-class-only (no inline styles) — strict CSP `style-src 'self'` adopters are not affected.

### Gating contract (both TagHelpers)

Both helpers render **nothing** in any of the following conditions:

- The request isn't an Umbraco-routed page (no `UmbracoRouteValues` feature on `HttpContext`).
- The active page is excluded (per-page `excludeFromLlmExports` bool, `ContentType.Alias` in the exclusion list, or page flagged via Umbraco Public Access — all three are checked by the default `IExclusionEvaluator`).
- `IPublishedUrlProvider.GetUrl(...)` returns null/empty/`#` or throws.

These match the HTTP `Link` header's gating exactly — adopters who toggle a page's exclusion don't need to flush downstream caches: the next render sees the new state.

---

## Customising the exclusion contract

`IExclusionEvaluator` is the seam the `Link` header middleware, the `.md` controller, the Accept-negotiation middleware, and both TagHelpers consume to decide "should this page emit Markdown discoverability?". The default `DefaultExclusionEvaluator` (public sealed) implements the per-page-bool-then-doctype-list rule described above.

To layer extra rules without re-implementing the default — for example, suppressing on staging-only paths, or routing exclusion through your own settings store — wrap-and-delegate via the standard ASP.NET Core DI Decorator pattern:

```csharp
using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;

[ComposeAfter(typeof(Umbraco.Community.AiVisibility.Composing.RoutingComposer))]
public sealed class AcmeExclusionComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Replace the default registration with a decorator that wraps it.
        builder.Services.AddScoped<IExclusionEvaluator, AcmeExclusionEvaluator>();
        // Re-register the default as itself so the decorator can ctor-inject it.
        builder.Services.AddScoped<DefaultExclusionEvaluator>();
    }
}

internal sealed class AcmeExclusionEvaluator : IExclusionEvaluator
{
    private readonly DefaultExclusionEvaluator _inner;

    public AcmeExclusionEvaluator(DefaultExclusionEvaluator inner)
    {
        _inner = inner;
    }

    public async Task<bool> IsExcludedAsync(
        IPublishedContent content,
        string? culture,
        string? host,
        CancellationToken ct)
    {
        // Adopter rule first — short-circuit before the default's resolver call.
        if (host?.StartsWith("staging.", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }
        return await _inner.IsExcludedAsync(content, culture, host, ct);
    }
}
```

`IExclusionEvaluator` is `Scoped` (the default reads the request-scoped `ISettingsResolver`); your decorator must register `Scoped` too. Adopters who re-register as `Singleton` will hit a captive-dependency exception on first request.

---

## Cloudflare "Markdown for Agents" alignment

The package emits two additional response headers on Markdown responses to align with [Cloudflare's "Markdown for Agents" spec](https://developers.cloudflare.com/fundamentals/reference/markdown-for-agents/) (relevant to adopters fronted by Cloudflare and to scanners like [`isitagentready.com`](https://isitagentready.com)).

### `X-Markdown-Tokens` — always-on, character-based heuristic

Emitted on every successful 200 Markdown response (`.md` route + `Accept: text/markdown` negotiation):

```
X-Markdown-Tokens: 412
```

The estimate is `Math.Max(1, length / 4)` where `length` is the .NET `string.Length` of the Markdown body (UTF-16 code units, not bytes, not graphemes). Cloudflare's spec only requires the header, not a specific tokeniser. The package does NOT pull in `Microsoft.ML.Tokenizers` / `tiktoken` (the cost-to-precision trade-off doesn't justify the dependency).

Omitted on 304 responses (the value is **body-derived** — sending a token count for a body the client isn't receiving would mislead). Contrast with `Content-Signal` below, which DOES ride 304s because it is **representation-metadata policy**, not a body-derived value (RFC 7232 § 4.1's "headers that would have been sent in the 200 must also be on the 304" applies to representation metadata; `X-Markdown-Tokens` is the exception that proves the rule).

### `Content-Signal` — opt-in, configurable per-site and per-doctype

Emitted only when configured:

```
Content-Signal: ai-train=no, search=yes, ai-input=yes
```

The package passes the configured value through verbatim — the only sanitisation is rejection of values containing CR/LF (header-injection guard). The spec's grammar is comma-separated `name=value` pairs; the comma-space form shown above is convention, not requirement. The [Cloudflare Content-Signals Policy spec](https://blog.cloudflare.com/content-signals-policy/) is the source of truth — consult it for the directive vocabulary.

#### Configuration

Site-level default:

```json
{
  "AiVisibility": {
    "ContentSignal": {
      "Default": "ai-train=no, search=yes, ai-input=yes"
    }
  }
}
```

Per-doctype override (case-insensitive):

```json
{
  "AiVisibility": {
    "ContentSignal": {
      "Default": "ai-train=no, search=yes",
      "PerDocTypeAlias": {
        "articlePage": "ai-train=yes, search=yes, ai-input=yes",
        "landingPage": "ai-train=no, search=no"
      }
    }
  }
}
```

Resolution rule: per-doctype override (case-insensitive lookup) → site-level default → header NOT emitted.

#### Why opt-in?

The package is not Cloudflare. We don't unilaterally assert content-use preferences for your site — adopters who want the signal opt in deliberately. Default policy: header off.

#### 304 contract

The `Content-Signal` header travels on 304 responses too (RFC 7232 § 4.1 — representation-metadata that would have been on the 200 must also be on the 304). Distinct from `X-Markdown-Tokens`, which is body-derived.

#### Cache flush after a config change

The Markdown response ETag is computed from `(host + route + culture + contentVersion)` — it does NOT include the resolved `Content-Signal` value. After you change `AiVisibility:ContentSignal:Default` or a per-doctype entry, previously-cached representations will continue to revalidate as 304 and ride the **new** policy automatically (the writer reads the current policy on every response, including the 304 path). However, downstream caches keyed on the response body alone (rare; most CDNs key on URL+headers) won't see the change until the body cache rolls. To flush proactively: save+publish a content node (the package's distributed-cache handler invalidates all per-page Markdown entries on every load-balanced instance), or restart the host. Adopters editing `Content-Signal` regularly should treat it as a content-shape concern, not a hot-reload toggle.

---

## Verifying with `curl`

Quick sanity check on a running site:

```bash
# HTML page — Link header + Vary present (assuming page is opted-in):
curl -I https://your-site.com/home

# .md route — X-Markdown-Tokens + Content-Signal (if configured):
curl -I https://your-site.com/home.md

# Accept-header negotiation — should match the .md headers byte-for-byte:
curl -I -H 'Accept: text/markdown' https://your-site.com/home

# 304 path — set If-None-Match to the previously returned ETag:
curl -I -H 'If-None-Match: "<etag-from-previous-call>"' https://your-site.com/home.md
```

If `Link: rel="alternate"` is missing from the HTML response, walk the gating contract: check the kill switch, the package's exclusion settings, and confirm the page IS resolved by Umbraco (try `curl -I https://your-site.com/home.md` — if that returns 404, the issue is route resolution, not discoverability).

---

## Out of scope (intentionally)

The following [`isitagentready.com`](https://isitagentready.com) scanner expectations are NOT shipped:

- MCP Server Card (`/.well-known/mcp/server-card.json`)
- Agent Skills (`/.well-known/agent-skills/index.json`)
- WebMCP (`navigator.modelContext`)
- OAuth Discovery (`/.well-known/oauth-authorization-server`)
- Web Bot Auth (HTTP Message Signatures)
- Commerce protocols (ACP, UCP, MPP, x402)

These are wrong-layer for a content-rendering middleware. Adopters who need them ship them at the application layer — the rationale: MCP server cards, Agent Skills, WebMCP, OAuth discovery, web-bot-auth, and commerce protocols are concerns of the *application* (what services it offers, who the agent is, how transactions clear), not the *content layer* (where the Markdown lives). Umbraco.Community.AiVisibility is a Markdown-discoverability package; conflating the two layers would create an extension surface adopters could not opt out of cleanly.
