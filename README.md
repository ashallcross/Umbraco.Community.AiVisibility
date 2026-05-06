# Umbraco.Community.AiVisibility

> AI visibility for Umbraco — request log + dashboard, robots.txt audit, content negotiation, llms.txt manifests, `*.md` page rendering.

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

A drop-in Umbraco v17+ package that makes your CMS visible to AI search engines (ChatGPT, Claude, Perplexity, Gemini, Bing Copilot) and gives editors visibility into who's reading the site. Renders pages as Markdown on demand, advertises that surface to AI crawlers, audits your `robots.txt` against the AI-crawler list, and logs every AI hit to a Backoffice dashboard. Zero configuration on a typical site.

## Status

🚧 **Pre-release.** v0.1.0-alpha. Not yet on NuGet.

## What it does

| Surface | Behaviour |
|---|---|
| **AI Traffic dashboard** | Backoffice **Settings → AI Traffic** shows every AI / human / crawler hit by classification, with date filtering and pagination. Reads from a per-site `aiVisibilityRequestLog` table the package writes on every successful Markdown / `/llms.txt` / `/llms-full.txt` response. |
| **Robots.txt audit** | A Health Check warns you if `/robots.txt` blocks GPTBot / ClaudeBot / PerplexityBot / OAI-SearchBot etc. Bot list synced from upstream at build time with SHA pinning. The package never modifies `robots.txt` — it audits, you decide. |
| **Content negotiation** | Standard `Accept: text/markdown` on any canonical URL returns Markdown. AI crawlers that don't append `.md` still get clean content. |
| `/llms.txt` + `/llms-full.txt` | Auto-generated llms.txt-spec manifests (concatenated full-site Markdown for context loading, plus the per-page index). Hot-path-protected: `If-None-Match` / 304 / single-flight on cache miss. |
| `/{any-page}.md` | Returns a clean Markdown version of the page — no nav, no scripts, just content. Rendered through Umbraco's normal Razor pipeline. |
| **Discoverability headers + TagHelpers** | Auto-injected HTTP `Link: rel="alternate"` headers + optional `<llms-link />` and `<llms-hint />` Razor tag helpers so AI tools can find the Markdown surface. |
| **Settings dashboard** | Backoffice **Settings → AI Visibility** surfaces site-name / site-summary / per-doctype exclusion overrides without editing `appsettings.json`. |

## Installation

```bash
dotnet add package Umbraco.Community.AiVisibility
```

(Available once v0.1.0 ships.)

## Configuration

Zero-config defaults produce useful output on a typical Umbraco site immediately. To customise:

```jsonc
{
  "AiVisibility": {
    "SiteName": "Acme Docs",
    "SiteSummary": "Acme product documentation.",
    "RequestLog": {
      "Enabled": true
    },
    "LogRetention": {
      "DurationDays": 90
    },
    "Hreflang": {
      "Enabled": false
    }
  }
}
```

See [`docs/getting-started.md`](docs/getting-started.md) for the full configuration reference, the extension points (`IMarkdownContentExtractor`, `IRequestLog`, `IRobotsAuditor`, `ISettingsResolver`), and the per-page exclusion contract.

## How it works (architectural decision)

Renders the page through Umbraco's normal template pipeline, extracts the main content region (`data-llms-content` → `<main>` → `<article>` → SmartReader fallback), and converts that HTML to Markdown via ReverseMarkdown. The Umbraco template is the canonical visual form of content — the package respects that rather than re-implementing block rendering.

## What it does NOT do (and why)

Documented anti-patterns — explicitly not shipped:

- ❌ User-Agent sniffing (cloaking; Google penalty risk)
- ❌ `<meta name="llms">` / `<meta name="ai-content-url">` (rejected by WHATWG / no implementation)
- ❌ `/.well-known/ai.txt` (no consensus)
- ❌ AI/human toggle UI (decorative; AI tools don't click buttons)
- ❌ JSON-LD-as-AI-strategy (LLMs largely ignore it; covered by separate Schema packages)
- ❌ Property-walking content registry (the Umbraco template is the canonical visual form of content)

See [Evil Martians: How to make your website visible to LLMs](https://evilmartians.com/chronicles/how-to-make-your-website-visible-to-llms) for the source research.

## License

Apache License 2.0 — see [LICENSE](LICENSE).

## Author

Built by Adam Shallcross. Companion project to [AgentRun.Umbraco](https://github.com/ashallcross/AgentRun).
