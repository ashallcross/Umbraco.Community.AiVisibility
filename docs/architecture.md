# Architecture

A one-page summary of how `Umbraco.Community.AiVisibility` is put together — for adopters evaluating "should I trust this package?" and for developers wiring up overrides. For full per-feature depth, follow the cross-references at the bottom.

## How it works

The package's design principle is **the Umbraco template is the canonical visual form of content.** When an AI crawler asks for `/some-page.md`, the package renders the page through Umbraco's normal Razor pipeline — same template, same Block List rendering, same Block Grid output — then parses the resulting HTML with AngleSharp, picks the main content region (`data-llms-content` → `<main>` → `<article>` → SmartReader fallback), strips boilerplate, absolutifies relative URLs, and converts to GitHub-flavoured Markdown via [ReverseMarkdown](https://github.com/mysticmind/reversemarkdown-net). The Markdown is then YAML-frontmatter-prefixed with `title`, `url`, `updated` and returned with `Content-Type: text/markdown; charset=utf-8`.

The package does **not** walk content properties and reconstruct content. It does not parse Block List children, does not template-fork, does not duplicate the renderer. Whatever your Razor templates produce — including any Block List / Block Grid composition, any custom view components, any third-party rendering integration — is what the AI gets, in clean Markdown form.

`/llms.txt` and `/llms-full.txt` are derived from the same per-page Markdown surface — `/llms.txt` is the RFC-style index of every published page (title + URL + summary); `/llms-full.txt` is the concatenated full-Markdown export, hard-capped at a configurable byte limit. Both are hot-path-protected (`If-None-Match` → 304 → single-flight on cache miss) and rebuild only when content publishes invalidate the cache.

## Standards alignment

The package's external contracts follow two converging conventions for AI ingest, so adopters can verify shape against published references rather than internal docs.

- **[llms.txt](https://llmstxt.org)** — the canonical reference for the `/llms.txt` + `/llms-full.txt` site-level manifests, the document structure (H1 + blockquote summary + H2 link-list sections), and the per-page URL convention. The spec's trailing-slash rule (`/path/` → append `/index.html.md`, `/path` → append `.md`) drives the package's emit side; the serve side accepts **either** shape on every page so adopters and crawlers don't have to guess which form to request.
- **Cloudflare "Markdown for Agents" headers** — the package emits `X-Markdown-Tokens: <integer>` on every successful 200 Markdown response (character-based estimate, `Math.Max(1, length / 4)`) so CDN-side AI-traffic tooling can size token budgets without parsing the body. This is a header convention sitting alongside the llms.txt URL conventions, not a replacement.

Intentionally **not** implemented: User-Agent sniffing, `<meta name="llms">` tags, `/.well-known/ai.txt`, JSON-LD-as-AI-strategy, AI/human content toggle UI, and Schema.org `speakable` (different domain — accessibility/voice, not AI ingest).

## What's in the box

| Public surface | Source folder | Notes |
|---|---|---|
| `/{path}.md` route | `Routing/` + `Extraction/` | Per-page Markdown via Umbraco's Razor pipeline + AngleSharp + ReverseMarkdown |
| `Accept: text/markdown` content negotiation | `Routing/` | Standard `Accept`-header negotiation on canonical URLs |
| `/llms.txt` | `LlmsTxt/` | Index manifest (RFC-style links + summaries) |
| `/llms-full.txt` | `LlmsTxt/` | Concatenated full-Markdown export with a configurable size cap |
| `Link: rel="alternate"` discoverability header | `Routing/` | Auto-emitted on every opted-in HTML response; idempotent `Vary: Accept` |
| `<llms-link />` / `<llms-hint />` Razor TagHelpers | `LlmsTxt/` (TagHelpers folder) | Optional in-Razor declaration of the alternate URL |
| Robots audit Health Check | `Robots/` | `Settings → Health Checks → AI Visibility — Robots audit` |
| AI Traffic dashboard | `Backoffice/` (controller) + `Client/src/elements/` (Lit element) | `Settings → AI Traffic`; reads `aiVisibilityRequestLog` |
| Settings dashboard | `Backoffice/` + `Client/src/elements/` | `Settings → AI Visibility`; site name + summary + per-doctype exclusion |
| `aiVisibilityRequestLog` host-DB table | `Persistence/Migrations/` | Schema-from-annotations on `RequestLogEntry` |
| Per-page caching | `Caching/` | Publish-driven invalidation; per-host cache key shape |
| Build-time AI-bot list sync | `Robots/AiBotList.fallback.txt` + csproj `<Target Name="SyncAiBotList">` | SHA-pinned online fetch + offline fallback |

## Extension points

Six interfaces give adopters override paths without forking. Register your override with `services.TryAdd*<I, MyImpl>()` in a composer; the package uses `services.TryAdd*` for every default so adopter overrides are honoured without `Remove<>()` ceremony.

- **`IMarkdownContentExtractor`** — *full extraction-pipeline replacement.* Swap this when you need a domain-specific Markdown shape, custom region selectors beyond the default `data-llms-content` → `<main>` → `<article>` → SmartReader fallback chain, or to integrate a different HTML-to-Markdown converter.
- **`IContentRegionSelector`** — *region-only override.* Swap this when the default region-selection chain works but you want to point at a custom `[data-llms-content]` selector or a content-type-specific element. Cheaper than replacing `IMarkdownContentExtractor` wholesale.
- **`IRequestLog`** — *AI traffic write-sink replacement.* Swap this when you want to redirect AI traffic logging to Application Insights, Serilog, an external sink, or a different table than the default `aiVisibilityRequestLog`. Note: the **Settings → AI Traffic** Backoffice dashboard reads from the local `aiVisibilityRequestLog` table — if you replace `IRequestLog` to write elsewhere, the dashboard appears empty unless your override also dual-writes that table.
- **`IRobotsAuditor`** — *audit-pipeline replacement.* Swap this when you want to fetch `/robots.txt` via a custom HTTP client (corporate proxy, mTLS, etc.), support a private bot list alongside the embedded curated list, or replace the audit logic entirely.
- **`ISettingsResolver`** — *per-host or per-tenant settings override.* Swap this when running a multi-site install where each bound hostname needs different Settings dashboard content (the default `DefaultSettingsResolver` reads the single global `aiVisibilitySettings` content node).
- **`IUserAgentClassifier`** — *UA classification replacement.* Swap this when you have internal bot tracking, custom enterprise UA conventions, or want to classify a token the embedded list doesn't know about.

See [`docs/extension-points.md`](extension-points.md) for the full per-interface contract, multi-instance behaviour, and the `IRequestLog` dual-write pattern.

## Lifetime + thread-safety constraints

Adopter overrides MUST honour the documented lifetime for each extension point. The composer hard-validates these at composition time and throws on a mis-registration so problems surface at boot, not at request time.

- **`IRobotsAuditor` — Singleton.** The auditor caches the embedded AI-bot list at construction; non-Singleton overrides multiply that cost per request and break the SHA-pinning guarantee.
- **`IRequestLog` — Singleton (with bounded channel).** The default writer is a process-wide `Channel<RequestLogEntry>` drained by a hosted-service. Non-Singleton overrides break batched-write semantics and cause unbounded resource use under concurrent traffic.
- **`ILlmsTxtBuilder` + `ILlmsFullBuilder` — Transient.** These are captive-dependency safe — each request builds its own builder. The cache layer is in front of the builder; the builder itself is stateless per-request.
- **`IUserAgentClassifier` — Singleton.** Stateless lookup against the embedded bot list; Singleton matches the lookup table's lifetime.
- **`ISettingsResolver` — Singleton with internal caching.** The default resolver caches per-host settings reads; adopter overrides should match this shape unless they're explicitly designed for per-request resolution (rare).

See [`docs/configuration.md`](configuration.md) and the existing [`docs/extension-points.md`](extension-points.md) for the full per-extension-point lifetime contract.

## Production deployment notes

This package assumes a **production-like deployment shape** by default:

- **Load-balanced front-end nodes.** Multiple `Subscriber`-role instances behind a load balancer. The AI traffic log writer runs on every node (each instance drains its own bounded channel into the shared host DB); the robots audit refresh job + the log retention job both use Umbraco's `IDistributedBackgroundJob` for exactly-once execution across the cluster (only one node fires the job per cycle).
- **Split Backoffice / Frontend (optional).** If you split Backoffice off as a separate web role, both sides need the package installed. Backoffice serves the dashboards + Management API endpoints; Frontend serves the `.md` / `/llms.txt` / `/llms-full.txt` routes + writes to `aiVisibilityRequestLog`.
- **uSync coexistence.** The package's settings doctype (`aiVisibilitySettings`) is auto-created on first boot via a `PackageMigrationPlan`. Adopters running uSync should let the migration run once on the first deploy, then export the resulting doctype to uSync for subsequent restores. The migration is idempotent.
- **No SQLite in production.** The `IDistributedBackgroundJob` lock semantics rely on real database lock primitives (SQL Server `sp_getapplock`, PostgreSQL advisory locks, etc.). SQLite's shared-lock approximation works in development but does not coordinate exactly-once execution across multiple host instances — verify your distributed-job shape against a real DB before going to production. See [`docs/maintenance.md`](maintenance.md) for the two-instance Docker SQL Server verification procedure.

## Where to read more

- [`docs/getting-started.md`](getting-started.md) — install + extraction-region contract + per-page exclusion + extension-point overview.
- [`docs/configuration.md`](configuration.md) — full `AiVisibility:` config-key reference (every key + default + constraint).
- [`docs/extension-points.md`](extension-points.md) — per-interface adopter contracts, lifetime constraints, multi-instance behaviour, dual-write patterns.
- [`docs/multi-site.md`](multi-site.md) — multi-host (`IDomainService.GetAll`) + multi-culture (BCP-47 routing) + per-host cache-key shapes.
- [`docs/data-attributes.md`](data-attributes.md) — `data-llms-content` / `data-llms-ignore` extraction-region attributes.
- [`docs/robots-audit.md`](robots-audit.md) — Health Check + bot-list refresh process.
- [`docs/maintenance.md`](maintenance.md) — maintainer-only operational notes (SHA refresh, two-instance Docker SQL Server setup, IValidateOptions validator authoring template).
- [`docs/release-checklist.md`](release-checklist.md) — pre-release + release-execution + post-release procedures.
- [`docs/dependency-status.md`](dependency-status.md) — NU1902 / NU1903 vulnerability warnings + CS0618 obsolete-API call-sites with target migration windows.
