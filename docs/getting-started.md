# Getting started with LlmsTxt.Umbraco

LlmsTxt.Umbraco exposes Umbraco published content to AI crawlers and large-language-model search engines via per-page Markdown rendering, a `/llms.txt` index, and a `/llms-full.txt` bulk export.

This document covers what ships in **v0.1 (Story 1.1)** — the per-page Markdown route. Cache headers, content negotiation, the manifests, and the Backoffice surface land in later stories.

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

## Cold-start cost

The first Markdown render of a given template JIT-compiles the Razor view — observed at ~6 seconds against Clean.Core 7.0.5 in the Story 0.A spike. Subsequent renders are 170–600 ms. **Cache layer (Story 1.2) absorbs this from the second hit onwards**; pre-warming on app startup is out of scope for v1.

If first-hit latency matters before Story 1.2 ships, the simplest mitigation is to hit `/your-most-important-pages.md` once at deployment time so the JIT cache is warm.

## What's not in v0.1

Coming in later stories of Epic 1:

- **`Cache-Control`, `ETag`, `If-None-Match`, `Vary: Accept`** — Story 1.2 (caching) and Story 1.3 (Accept-header content negotiation)
- **`Accept: text/markdown` content negotiation** on canonical (non-`.md`) URLs — Story 1.3
- **Public adopter override pattern + benchmark fixture catalogue** — Story 1.4 (`IMarkdownContentExtractor` is already public; Story 1.4 adds documentation, override tests, and BlockGrid/nested-content/table fixtures)

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
