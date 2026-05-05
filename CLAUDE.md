# Project Instructions for Claude Code

Umbraco.Community.AiVisibility — Expose Umbraco published content to LLMs and AI search engines: per-page Markdown rendering, `/llms.txt`, and `/llms-full.txt`, plus AI traffic request log + dashboard, robots.txt audit, content negotiation, and link/header advertisement.

## Two-Repo Development Split (Non-Negotiable)

This project is developed across **two repositories**, mirroring the AgentRun.Umbraco pattern:

- **Public repo** at `~/Documents/UmbracoCommunityAiVisibility/` — contains package source, tests, docs, and what ships to NuGet + the Umbraco Marketplace
- **Private repo** at `~/Documents/UmbracoCommunityAiVisibility-planning/` — contains all BMAD planning artefacts, story specs, retrospectives, sprint status, deferred work, Claude skills, and agent definitions

The folders `_bmad-output/`, `_bmad/`, `.agents/`, and `.claude/` inside the public repo are **symlinks** pointing at the private repo. The public repo's `.gitignore` excludes those paths.

### Commit routing (apply automatically)

When the user asks to commit or push, route changes to the correct repo:

| What changed | Which repo to commit in |
|---|---|
| Files under `Umbraco.Community.AiVisibility/`, `Umbraco.Community.AiVisibility.Tests/`, `Umbraco.Community.AiVisibility.TestSite/` | Public |
| `docs/`, README.md, LICENSE, NOTICE, csproj files, Directory.Packages.props, umbraco-marketplace.json, .gitignore | Public |
| Anything under `_bmad-output/`, `_bmad/`, `.agents/`, `.claude/` (reached via symlink) | **Private** — cd to `~/Documents/UmbracoCommunityAiVisibility-planning/` to commit |

A typical story completion produces **two separate commits** across the two repos. This is correct and expected. Never combine them.

### Red flags

- `git status` in the public repo showing any `_bmad-output/`, `_bmad/`, `.agents/`, or `.claude/` content → symlinks or `.gitignore` are broken; stop and investigate before staging
- An agent proposing to copy files between the repos "for simplicity" → violates the split; follow the split
- Committing to the public repo and finding planning artefacts in the changelist → immediately unstage, don't force through

The full rules and rationale (originally captured for AgentRun) live in the auto-loaded memory entry `feedback_two_repo_split.md`. The same rules apply here, with paths swapped from `AgentRun*` to `Umbraco.Community.AiVisibility*`.

### Frozen rollback baselines (Story 6.0c — package rename)

The OLD pre-rename folders at `~/Documents/LlmsTxt/`, `~/Documents/LlmsTxt-planning/`, and the OLD auto-memory directory at `~/.claude/projects/-Users-adamshallcross-Documents-LlmsTxt/memory/` exist as frozen rollback baselines per Story 6.0c's non-destructive guarantee. **Never edit, rename, or delete those paths from any agent session.** Manual cleanup ~1 week post-cutover is the user's responsibility, not Claude's.

## Other Rules

- **Tests**: `dotnet test Umbraco.Community.AiVisibility.slnx` — never bare `dotnet test` (multi-project repo; bare call fails with MSB1011).
- **Package management**: this repo uses Central Package Management. All NuGet versions live in the root `Directory.Packages.props`. csproj `<PackageReference>` entries do **not** carry version attributes — add a `<PackageVersion>` line to `Directory.Packages.props` first.
- **Architectural principle**: the Umbraco template is the canonical visual form of content. The package renders pages through Umbraco's normal pipeline and converts the resulting HTML to Markdown — it does **not** walk properties and reconstruct content. See `_bmad-output/planning-artifacts/package-spec.md` (section 5) for the full rationale.
- **Anti-patterns explicitly NOT shipped**: User-Agent sniffing, `<meta name="llms">`, `/.well-known/ai.txt`, JSON-LD-as-AI-strategy, AI/human toggle UI. See spec section 15.
- **Spec & architecture**: see `_bmad-output/planning-artifacts/package-spec.md` (pre-architecture decisions) and the BMAD architecture doc once `bmad-create-architecture` has run.
- **Spec-bound external contracts (preserved through the rename)**: `/llms.txt`, `/llms-full.txt`, `*.md` routes; `Accept: text/markdown` content negotiation; `Link: rel="llms-txt"` header value; `data-llms-content` and `data-llms-ignore` extraction-region attributes; `<link rel="alternate" type="text/markdown" …>` HTML markup. The package-internal identifiers were renamed in Story 6.0c (2026-05-05); the public-facing spec contracts above were NOT.
- **Umbraco v17 official requirements** (verified against `docs.umbraco.com/umbraco-cms/fundamentals/setup/requirements` 2026-04-28): .NET 10.0+, Node.js 24.11.1+, SQL Server 2016+ or SQLite-in-dev. The package's CI must use Node ≥ 24.11.1; do NOT downgrade to Node 20.
- **Umbraco extension/package template** (verified against `docs.umbraco.com/umbraco-cms/extending/packages/creating-a-package` 2026-04-28): the `dotnet new` short name is `umbraco-extension` (NOT `umbracopackage`, which appeared in older docs). Install via `dotnet new install Umbraco.Templates`.
