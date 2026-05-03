# Robots audit

LlmsTxt.Umbraco includes a Backoffice Health Check that audits your site's `/robots.txt` against the [`ai-robots-txt/ai.robots.txt`](https://github.com/ai-robots-txt/ai.robots.txt) AI-crawler list and surfaces copy-pasteable suggested removals when AI bots are blocked.

**The package is read-only on your `/robots.txt`** — it audits and surfaces, you decide what to change. There is no auto-fix button, no auto-PR, no `IRobotsAuditWriter`. This is by design (UX-DR3 + project-context.md § Critical Don't-Miss Rules).

## What the audit does

1. Fetches `/robots.txt` from each configured host.
2. Parses User-agent / Disallow blocks (RFC 9309 grammar; tolerant of comments, blank lines, malformed lines).
3. Cross-references each User-agent block against the embedded AI-bot list — flags any block that combines a known AI-crawler token (or `User-agent: *`) with a full-site `Disallow: /`.
4. Caches the result per hostname.
5. Surfaces findings in the Backoffice Health Check view at `Settings → Health Check → LLMs`.

## When the audit runs

| Trigger | Source |
|---|---|
| Host startup | `StartupRobotsAuditRunner : IHostedService` — gated by `LlmsTxt:RobotsAuditOnStartup` (default `true`) and `IServerRoleAccessor.CurrentServerRole ∈ { SchedulingPublisher, Single }` (so multi-instance front-end servers don't all hammer their own origin at boot). |
| Recurring refresh | `RobotsAuditRefreshJob : IDistributedBackgroundJob` — period is `LlmsTxt:RobotsAuditor:RefreshIntervalHours` (default 24h). Umbraco coordinates exactly-once execution across a load-balanced deployment via the host-DB lock. Set the period to `0` to disable. |
| On-demand | Open the Backoffice Health Check view; the cache miss path triggers a fresh audit if no cached result is available. |

## Bot categories

Each AI-bot token in the curated list maps to one of:

| Category | Examples | What blocking signals |
|---|---|---|
| **Training** | `GPTBot`, `ClaudeBot`, `cohere-training-data-crawler`, `Bytespider` | "Don't use my content for model training." |
| **Search-retrieval** | `OAI-SearchBot`, `PerplexityBot`, `Claude-SearchBot` | "Don't include me in AI-mediated search answers." (Usually a different intent than training.) |
| **User-triggered** | `ChatGPT-User`, `Claude-User`, `Perplexity-User`, `MistralAI-User` | "Block users who ask their LLM to fetch my page." (Usually unintentional.) |
| **Opt-out** | `Google-Extended` | "Allow regular search; opt out of AI-only training." |
| **Unclassified** | (any token not yet in the curated map) | Surfaced verbatim — patch the curated map in `LlmsTxt.Umbraco/HealthChecks/AiBotList.cs` to fix. |

## Deprecated tokens

Two tokens still ship in the upstream feed but should be replaced:

| Deprecated | Use instead |
|---|---|
| `anthropic-ai` | `ClaudeBot` |
| `Claude-Web` | `ClaudeBot` |

The audit annotates findings with the modern replacement.

## Bytespider / Grok caveat

Some crawlers are documented to ignore `robots.txt` entirely (notably **Bytespider** / ByteDance and **GrokBot** / x.ai). The audit still surfaces blocks against these tokens — you may want them in your `robots.txt` for downstream inference / signalling — but adds an `Info`-severity caveat noting the protocol non-compliance. Adopters wanting a stronger guarantee should consider IP/network-level blocks at their CDN.

## Build-time AI-bot-list sync

The list is fetched from upstream at **build time**:

```
Source URL:   https://raw.githubusercontent.com/ai-robots-txt/ai.robots.txt/main/robots.txt
Pinned SHA:   <ExpectedAiBotListSha256> in LlmsTxt.Umbraco/LlmsTxt.Umbraco.csproj
Fallback:     LlmsTxt.Umbraco/HealthChecks/AiBotList.fallback.txt (committed)
```

| Path | Behaviour |
|---|---|
| **Online + SHA matches** | Embed fetched content; emit info-level message naming the SHA. |
| **Online + SHA mismatch** | Build hard-fails. **Deliberate** — forces maintainer review. Refresh the pinned SHA via [`docs/maintenance.md`](maintenance.md) after auditing the upstream change. |
| **Offline / fetch failed** | Fall back to the committed snapshot; emit a warning. Build is offline-safe. |

The fallback file is byte-identical to the upstream snapshot at the moment the SHA was pinned — so online and offline builds produce identical embedded bytes when the upstream content matches the pin.

## Custom auditors

Adopters wanting different audit semantics replace the default:

```csharp
services.AddSingleton<IRobotsAuditor, MyAuditor>();
```

**Lifetime MUST be Singleton.** The default `RobotsAuditRefreshJob` (`IDistributedBackgroundJob`) captures the auditor by constructor; a Scoped or Transient registration produces a captive dependency that the canonical DI gate (`Compose_StartupValidation_HealthChecksComposer_NoCaptiveDependency`) catches at composition time.

`IRobotsAuditor` is a small interface:

```csharp
public interface IRobotsAuditor
{
    Task<RobotsAuditResult> AuditAsync(
        string hostname,
        string scheme,
        CancellationToken cancellationToken);
}
```

## Configuration reference

| Key | Default | Effect |
|---|---|---|
| `LlmsTxt:RobotsAuditOnStartup` | `true` | One-shot audit at host startup. Set `false` to skip; on-demand + recurring paths still work. |
| `LlmsTxt:RobotsAuditor:RefreshIntervalHours` | `24` | Recurring refresh cadence via `IDistributedBackgroundJob`. Set `0` (or negative) to disable. |
| `LlmsTxt:RobotsAuditor:FetchTimeoutSeconds` | `5` | Per-host `/robots.txt` fetch timeout. Distinct from the build-time MSBuild fetch timeout (also 5s, but configured separately in the csproj target). |
| `LlmsTxt:RobotsAuditor:DevFetchPort` | `null` | **Dev/test only — DO NOT set in production.** When set, the auditor composes the audit URI with the supplied port instead of the scheme default (443/80). Useful when running the TestSite on Kestrel's dev port (e.g. `44314`) so the live audit can round-trip against the running site. Convention: live in `appsettings.Development.json` only. `null` (default) → use scheme default port — the production-correct behaviour. |
| `LlmsTxt:RobotsAuditor:RefreshIntervalSecondsOverride` | `null` | **Dev/test only — DO NOT set in production.** When set, the recurring refresh job's `Period` uses this value (in seconds) instead of `RefreshIntervalHours`. Used by the architect-A5 two-instance shared-SQL-Server exactly-once gate (where 1-hour cycles would make the test prohibitively long). `null` (default) → use the hours knob. Values `<= 0` are treated as unset. |

## Caveats

- **CDN-served `/robots.txt`** — the audit fetches from the origin host header. If your CDN serves a different `/robots.txt` than your origin, the audit reflects what the origin serves. Verify with `curl -I origin.example.com/robots.txt` directly.
- **Wildcard expansion** — `User-agent: * \n Disallow: /` flags every known AI-bot token in our list. Each finding gets a per-bot suggested removal that creates a more permissive rule rather than removing the wildcard outright.
- **Partial-path disallow** — `Disallow: /private/` does NOT trigger a finding. We only flag full-site blocks (`Disallow: /`). Adopters wanting partial-path enforcement should audit manually.
- **No `IRobotsAuditWriter`** — the package never writes to your `/robots.txt`. This is architecturally enforced.
