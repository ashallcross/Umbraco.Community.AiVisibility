# LlmsTxt.Umbraco

> Expose Umbraco published content to LLMs and AI search engines.

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

A drop-in Umbraco v17+ package that makes your CMS citable by ChatGPT, Claude, Perplexity, Gemini, and Bing Copilot. Auto-generates per-page Markdown, `/llms.txt`, and `/llms-full.txt` — plus content negotiation, `Link` headers, and a robots.txt audit. Zero configuration on a typical site.

## Status

🚧 **Pre-development.** Architecture and PRD in progress. Not yet on NuGet.

## What it does

| Surface | Behaviour |
|---|---|
| `/{any-page}.md` | Returns a clean Markdown version of the page — no nav, no scripts, just content. |
| `/{any-page}` with `Accept: text/markdown` | Same canonical URL serves Markdown when the client requests it. Standards-compliant content negotiation. |
| `/llms.txt` | Auto-generated index of your site, following the [llms.txt spec](https://llmstxt.org). |
| `/llms-full.txt` | Concatenated full-site Markdown for LLM context loading. |
| HTTP `Link: rel="alternate"` headers + HTML `<link>` tags | Auto-injected so AI tools can discover the Markdown surface. |
| robots.txt audit | A Health Check warns you if you're blocking GPTBot / ClaudeBot / PerplexityBot. |

## How it works (architectural decision)

Renders the page through Umbraco's normal template pipeline, extracts the main content region (`data-llms-content` → `<main>` → `<article>` → SmartReader fallback), and converts that HTML to Markdown via ReverseMarkdown. The Umbraco template is the canonical visual form of content — the package respects that rather than re-implementing block rendering.

## Installation

```bash
dotnet add package LlmsTxt.Umbraco
```

(Available once v0.1.0 ships.)

## Configuration

See `docs/configuration.md` (forthcoming).

## What it does NOT do (and why)

Documented anti-patterns — explicitly not shipped:

- ❌ User-Agent sniffing (cloaking; Google penalty risk)
- ❌ `<meta name="llms">` / `<meta name="ai-content-url">` (rejected by WHATWG / no implementation)
- ❌ `/.well-known/ai.txt` (no consensus)
- ❌ AI/human toggle UI (decorative; AI tools don't click buttons)
- ❌ JSON-LD-as-AI-strategy (LLMs largely ignore it; covered by separate Schema packages)

See [Evil Martians: How to make your website visible to LLMs](https://evilmartians.com/chronicles/how-to-make-your-website-visible-to-llms) for the source research.

## License

Apache License 2.0 — see [LICENSE](LICENSE).

## Author

Built by Adam Shallcross. Companion project to [AgentRun.Umbraco](https://github.com/ashallcross/AgentRun).
