# Umbraco.Community.AiVisibility

> AI visibility for Umbraco — request log + dashboard, robots.txt audit, content negotiation, llms.txt manifests, `*.md` page rendering.

[![NuGet](https://img.shields.io/nuget/v/Umbraco.Community.AiVisibility.svg)](https://www.nuget.org/packages/Umbraco.Community.AiVisibility/)
[![Umbraco Marketplace](https://img.shields.io/badge/Umbraco%20Marketplace-Listed-blue)](https://marketplace.umbraco.com/package/umbraco.community.aivisibility)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

A drop-in Umbraco v17+ package that makes your CMS visible to AI search engines (ChatGPT, Claude, Perplexity, Gemini, Bing Copilot) and gives editors visibility into who's reading the site. Renders pages as Markdown on demand, advertises that surface to AI crawlers, audits your `robots.txt` against the AI-crawler list, and logs every AI hit to a Backoffice dashboard. **Zero configuration on a typical site.**

## Compatibility

| Concern | Required |
|---|---|
| .NET | `10.0` (single-target — v17 floor) |
| Umbraco CMS | `17.3.2+` (floats forward via Central Package Management) |
| Database | SQL Server / Azure SQL / MySQL / PostgreSQL — anything Umbraco supports. SQLite is supported in development. |
| Node.js (Backoffice TS rebuild only) | `≥ 24.11.1` (Umbraco v17 docs floor — only needed if you're rebuilding the bundled JS yourself) |

The Vite bundle ships pre-built inside the NuGet package, so adopters do not need Node.js to run the package.

## Installation

```bash
dotnet add package Umbraco.Community.AiVisibility
```

That's it. The package's `PackageMigrationPlan` runs on first boot and creates an `aiVisibilitySettings` doctype + the `aiVisibilityRequestLog` table; no manual migration step needed.

## Zero-config quick-start

After install, the Markdown surface is immediately reachable. Replace `/about/` with any published page on your site:

```bash
# Content negotiation on any canonical URL — works on every page, no path tricks needed.
# This is the path most AI crawlers take; sidesteps trailing-slash conventions entirely.
curl -H "Accept: text/markdown" https://your-site.example/about/

# Site-level manifests per the llms.txt spec.
curl https://your-site.example/llms.txt          # RFC-style index of every published page
curl https://your-site.example/llms-full.txt     # concatenated full-Markdown export

# Per-page Markdown URLs — both shapes resolve.
curl https://your-site.example/about/index.html.md   # llmstxt.org canonical (trailing-slash) form
curl https://your-site.example/about.md              # short form (also accepted)
```

Two new Backoffice dashboards appear under **Settings**:

- **Settings → AI Visibility** — site name + summary + per-doctype exclusion overrides.
- **Settings → AI Traffic** — every AI / human / crawler hit by classification, with date filtering and pagination.

A Health Check at **Settings → Health Checks → AI Visibility — Robots audit** warns when `/robots.txt` blocks GPTBot / ClaudeBot / PerplexityBot / OAI-SearchBot etc. The package does not modify `robots.txt` — it audits, you decide.

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

## Configuration

Zero-config defaults produce useful output on a typical Umbraco site immediately. The most common `appsettings.json` overrides:

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

See [`docs/configuration.md`](docs/configuration.md) for the full reference (every key under `AiVisibility:` with defaults + constraints), [`docs/getting-started.md`](docs/getting-started.md) for the per-page exclusion contract + extension points, and [`docs/multi-site.md`](docs/multi-site.md) for the multi-host + multi-culture story.

## Extension points

Five interfaces give adopters override paths without forking:

| Interface | Default | Override use case |
|---|---|---|
| `IMarkdownContentExtractor` | `DefaultMarkdownContentExtractor` (HTML → Markdown via ReverseMarkdown + SmartReader fallback) | Replace the extraction pipeline (e.g. domain-specific Markdown shape, custom region selectors). |
| `IRequestLog` | `DefaultRequestLog` (writes to `aiVisibilityRequestLog` host table) | Redirect AI traffic logging to Application Insights, Serilog, an external sink, etc. |
| `IRobotsAuditor` | `DefaultRobotsAuditor` | Replace the audit's HTTP-fetch + bot-list-comparison logic (e.g. fetch via custom HTTP client, support a private bot list). |
| `ISettingsResolver` | `DefaultSettingsResolver` (reads the global `aiVisibilitySettings` content node + appsettings overlay) | Per-host or per-tenant settings overrides on multi-site installs. |
| `IUserAgentClassifier` | `DefaultUserAgentClassifier` | Custom UA classification (e.g. internal bot tracking, custom enterprise UA conventions). |

Register your override with `services.TryAdd*<I, MyImpl>()` in a composer — the package uses `services.TryAdd*` for every default so adopter overrides are honoured without `Remove<>()` ceremony. See [`docs/extension-points.md`](docs/extension-points.md) for the full per-interface contract + multi-instance behaviour.

## How it works (architectural decision)

Renders the page through Umbraco's normal template pipeline, extracts the main content region (`data-llms-content` → `<main>` → `<article>` → SmartReader fallback), and converts that HTML to Markdown via ReverseMarkdown. The Umbraco template is the canonical visual form of content — the package respects that rather than re-implementing block rendering.

## Security & privacy notes

- **PII discipline.** `aiVisibilityRequestLog` captures path, content key, culture, UA classification, and referrer host **only**. Never query strings, cookies, tokens, session IDs, or full referrer paths. Adopter handlers replacing `IRequestLog` (e.g. App Insights forwarding) are expected to honour the same discipline.
- **Backoffice Management API** is behind Umbraco's standard authorisation policy — the dashboards' `/umbraco/management/api/v1/aivisibility/...` endpoints require the configured Section policy (default: Settings).
- **SSRF defence** — the robots audit's HTTP fetcher refuses RFC1918 / loopback / link-local / cloud-metadata IPs and rejects 3xx redirects in-app to defend against redirect-based amplification.
- **XSS defence** — the Health Check's HTML-rendered messages run every adopter-controlled value through `WebUtility.HtmlEncode`.

## Known limitations

- **Custom `IRequestLog` write-sink + AI Traffic dashboard.** If you replace the default `IRequestLog` to redirect writes elsewhere (e.g. Application Insights, Serilog, an external sink), the **Settings → AI Traffic** Backoffice dashboard reads from the local `aiVisibilityRequestLog` host-DB table — it will appear empty unless your override ALSO seeds that table. The dashboard does not consume the alternate sink. See [`docs/extension-points.md`](docs/extension-points.md) § "Backoffice consumers of `IRequestLog`" for the full caveat + the suggested dual-write pattern.
- **`/llms-full.txt` size cap.** The full-Markdown manifest enforces a hard byte cap (default 5 MB, configurable via `AiVisibility:MaxLlmsFullSizeKb`). Pages emit in tree order until the next page would push past the cap; the body then ends with a truncation footer documenting how many of the total pages were emitted. Pagination of `/llms-full.txt` for very large sites is deferred to v1.1.
- **Single global Settings node by design.** There is exactly one `aiVisibilitySettings` content node per Umbraco install — the same site name, summary, and per-doctype exclusion list applies to every bound host. Adopters needing per-host or per-tenant overrides supply their own `ISettingsResolver` implementation; see [`docs/extension-points.md`](docs/extension-points.md) and [`docs/multi-site.md`](docs/multi-site.md) for the override pattern.
- **Umbraco v17 only.** Single-target on `.NET 10` + `Umbraco.Cms 17.3.2+`. There is no v13 / v15 / v16 multi-target. The `[Obsolete]` API call sites flagged for v18 / v19 removal are catalogued in [`docs/dependency-status.md`](docs/dependency-status.md) and will be migrated when the corresponding Umbraco majors land.

## Documentation

- [`docs/getting-started.md`](docs/getting-started.md) — install + extraction-region contract + per-page exclusion + extension-point overview.
- [`docs/configuration.md`](docs/configuration.md) — full `AiVisibility:` config reference (every key + default + constraint).
- [`docs/extension-points.md`](docs/extension-points.md) — per-interface adopter contracts (IRequestLog, IUserAgentClassifier, IRobotsAuditor, IMarkdownContentExtractor, ISettingsResolver) + notification subscription guide.
- [`docs/multi-site.md`](docs/multi-site.md) — multi-host (`IDomainService.GetAll`) + multi-culture (BCP-47 routing) + per-host cache-key shapes.
- [`docs/data-attributes.md`](docs/data-attributes.md) — `data-llms-content` / `data-llms-ignore` extraction-region attributes.
- [`docs/robots-audit.md`](docs/robots-audit.md) — Health Check + bot-list refresh process.
- [`docs/maintenance.md`](docs/maintenance.md) — maintainer-only operational notes (SHA refresh, two-instance Docker SQL Server setup).
- [`docs/release-checklist.md`](docs/release-checklist.md) — recurring per-release checklist (pack output, dependency triage, smoke trio, README freshness).
- [`docs/dependency-status.md`](docs/dependency-status.md) — NU1902/NU1903 + CS0618 catalogue with resolution status + target review dates.

## What it does NOT do (and why)

Documented anti-patterns — explicitly not shipped:

- ❌ User-Agent sniffing (cloaking; Google penalty risk)
- ❌ `<meta name="llms">` / `<meta name="ai-content-url">` (rejected by WHATWG / no implementation)
- ❌ `/.well-known/ai.txt` (no consensus)
- ❌ AI/human toggle UI (decorative; AI tools don't click buttons)
- ❌ JSON-LD-as-AI-strategy (LLMs largely ignore it; covered by separate Schema packages)
- ❌ Property-walking content registry (the Umbraco template is the canonical visual form of content)

See [Evil Martians: How to make your website visible to LLMs](https://evilmartians.com/chronicles/how-to-make-your-website-visible-to-llms) for the source research.

## Support

- **Issues + feature requests:** [GitHub issues](https://github.com/ashallcross/Umbraco.Community.AiVisibility/issues)
- **Pull requests:** welcome — keep the change focused (one feature or fix per PR), include tests covering new behaviour, and document any new public surface in `docs/` so adopters discover it.
- **License:** Apache 2.0 — see [LICENSE](LICENSE).

## Author

Built by Adam Shallcross. Companion project to [AgentRun.Umbraco](https://github.com/ashallcross/AgentRun).
