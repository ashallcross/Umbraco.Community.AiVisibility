# Project Instructions for Claude Code

Umbraco.Community.AiVisibility — Expose Umbraco published content to LLMs and AI search engines: per-page Markdown rendering, `/llms.txt`, and `/llms-full.txt`, plus AI traffic request log + dashboard, robots.txt audit, content negotiation, and link/header advertisement.

This file captures repo-level conventions any contributor (human or AI) working in this codebase should know about. Adopter-facing reference lives in [`docs/`](docs/); package-internal architecture lives in `Umbraco.Community.AiVisibility/`.

## Rules

- **Tests**: `dotnet test Umbraco.Community.AiVisibility.slnx` — never bare `dotnet test` (multi-project repo; bare call fails with MSB1011).
- **Package management**: this repo uses Central Package Management. All NuGet versions live in the root `Directory.Packages.props`. csproj `<PackageReference>` entries do **not** carry version attributes — add a `<PackageVersion>` line to `Directory.Packages.props` first.
- **Architectural principle**: the Umbraco template is the canonical visual form of content. The package renders pages through Umbraco's normal pipeline and converts the resulting HTML to Markdown — it does **not** walk properties and reconstruct content. See [`docs/architecture.md`](docs/architecture.md) for the adopter-facing summary.
- **Anti-patterns explicitly NOT shipped**: User-Agent sniffing, `<meta name="llms">`, `/.well-known/ai.txt`, JSON-LD-as-AI-strategy, AI/human toggle UI. See the [README](README.md#what-this-package-does-not-do-and-why)'s "What this package does NOT do" section for the rationale on each.
- **Spec-bound external contracts**: `/llms.txt`, `/llms-full.txt`, `*.md` routes; `Accept: text/markdown` content negotiation; `Link: rel="llms-txt"` header value; `data-llms-content` and `data-llms-ignore` extraction-region attributes; `<link rel="alternate" type="text/markdown" …>` HTML markup. These contracts are stable across versions; package-internal identifiers were renamed pre-1.0 but the public-facing contracts above were NOT.
- **Umbraco v17 official requirements** (verified against [`docs.umbraco.com/umbraco-cms/fundamentals/setup/requirements`](https://docs.umbraco.com/umbraco-cms/fundamentals/setup/requirements) 2026-04-28): .NET 10.0+, Node.js 24.11.1+, SQL Server 2016+ or SQLite-in-dev. The package's CI must use Node ≥ 24.11.1; do NOT downgrade to Node 20.
- **Umbraco extension/package template** (verified against [`docs.umbraco.com/umbraco-cms/extending/packages/creating-a-package`](https://docs.umbraco.com/umbraco-cms/extending/packages/creating-a-package) 2026-04-28): the `dotnet new` short name is `umbraco-extension` (NOT `umbracopackage`, which appeared in older docs). Install via `dotnet new install Umbraco.Templates`.
