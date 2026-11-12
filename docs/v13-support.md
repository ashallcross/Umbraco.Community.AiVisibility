# Umbraco v13 support — the lite SKU

> **End of support.** The v13 package (`Umbraco.Community.AiVisibility.V13`) is **frozen at 1.2.x**. It receives no further releases, fixes or bot-list updates, and it is **unsupported from December 2026**, when Umbraco 13 reaches end of support. All new work, including the v1.3.0 fixes, ships in the v17 package only. If you can, move to Umbraco 17+ and the flagship package.

AI Visibility ships as **two packages** from one codebase:

| Package | Umbraco | .NET | Backoffice UI |
|---|---|---|---|
| `Umbraco.Community.AiVisibility` | 17+ | 10 | Full — Settings + AI Traffic dashboards |
| `Umbraco.Community.AiVisibility.V13` | 13 (LTS) | 8 | **None** — configure via `appsettings.json` |

The v13 package behaves as the 1.2.x release did: it shares the content-extraction engine as it stood at 1.2.x, so it does **not** include the v1.3.0 fixes and changes (heading conversion, request-log table creation under `SkipSettingsDoctype`, the "AI Visibility" tab for the exclusion property, configurable exclusion aliases, exclusion of member-protected pages from the manifests, the refreshed AI-crawler list). See the [CHANGELOG](../CHANGELOG.md) for what changed in the v17 package after 1.2.x. The v13 package is a deliberately **"lite" SKU**: it drops the Backoffice frontend (which is AngularJS on v13 and Bellissima/Lit on v17 — the two don't share code) and keeps everything that renders and serves content.

## Which package do I install?

Match the package to your Umbraco major:

```bash
# Umbraco 13 / .NET 8
dotnet add package Umbraco.Community.AiVisibility.V13

# Umbraco 17+ / .NET 10
dotnet add package Umbraco.Community.AiVisibility
```

Do **not** install both on the same site — that combination is unsupported (see [Co-installation](#co-installation-is-unsupported) below).

## What the v13 lite SKU ships vs the v17 package

| Capability | v13 lite | v17 |
|---|---|---|
| Per-page Markdown at `/{url}.md` | ✅ | ✅ |
| `/llms.txt` + `/llms-full.txt` manifests | ✅ | ✅ |
| `Accept: text/markdown` content negotiation | ✅ | ✅ |
| `Link: rel="alternate"; type="text/markdown"` header + `<llms-link />` / `<llms-hint />` TagHelpers | ✅ | ✅ |
| robots.txt audit Health Check | ✅ | ✅ |
| AI-traffic request log (`aiVisibilityRequestLog` table) | ✅ | ✅ |
| Multi-site / multi-culture URL resolution | ✅ | ✅ |
| Dual-strategy renderer (Razor + loopback fallback) | ✅ | ✅ |
| Configuration via `appsettings.json` (`AiVisibility:`) | ✅ | ✅ |
| **Settings → AI Visibility** Backoffice dashboard | ❌ | ✅ |
| **Settings → AI Traffic** Backoffice dashboard | ❌ | ✅ |
| Management API endpoints (`/umbraco/management/api/v1/aivisibility/...`) | ❌ | ✅ |
| Exactly-once recurring-job coordination | ❌ (best-effort — see below) | ✅ |

**On v13, configure through `appsettings.json`** (every key under `AiVisibility:` — see [`configuration.md`](configuration.md)) and **read the request log directly via SQL** against the `aiVisibilityRequestLog` table. There is no editor-facing UI on v13.

## Recurring-job coordination: v13 vs v17

The package runs two recurring jobs — a robots-audit refresh and a log-retention sweep. How they coordinate across a load-balanced deployment differs by platform:

- **v17** uses Umbraco's `IDistributedBackgroundJob`, which coordinates **exactly-once** execution across all nodes via a host-database lock.
- **v13** has no equivalent, so the jobs are **best-effort**: they run when the node holds `ServerRole.SchedulingPublisher` or `ServerRole.Single`. A single-server install runs each job once per cycle as expected. A **load-balanced v13 deployment can run the same job on more than one node** in a cycle.

This is safe because the write paths are idempotent under duplication:

- **Log retention** is an idempotent `DELETE ... WHERE createdUtc < @cutoff` — running it twice deletes the same rows once; the second run is a no-op.
- **Robots refresh** is last-write-wins cache state. A duplicate cycle performs duplicate *origin work* (a second `/robots.txt` fetch + audit per hostname) — that is **wasteful, not corrupting**.

If you run v13 load-balanced and want to avoid the duplicate origin work, host the recurring jobs on a single scheduling-publisher node per your load-balancer configuration.

## Support window

The v13 lite SKU targets Umbraco 13 on .NET 8. It is **frozen at 1.2.x**:

- **No further releases.** The last published 1.2.x version is the final v13 version — no security or correctness patches, no feature work, no bot-list refreshes.
- **Unsupported from December 2026**, when Umbraco 13 reaches end of support.
- All new work lands in the v17 package (`Umbraco.Community.AiVisibility`).
- If you're starting a new project, use Umbraco 17+ and the flagship package. The v13 SKU exists only for existing Umbraco 13 LTS sites that can't move majors yet.

## Co-installation is unsupported

Installing **both** `Umbraco.Community.AiVisibility` and `Umbraco.Community.AiVisibility.V13` on the same site is not supported. The guard is each package's own Umbraco dependency floor — the v13 package depends on Umbraco 13.x, the v17 package on 17.x — so on any given site only one is the correct match. If both were force-loaded, both would register their TagHelpers and you'd get duplicated discoverability markup. Pick the one that matches your Umbraco major.

## Building from source

The repository builds with the **.NET 10 SDK**, which down-targets `net8.0` cleanly. The .NET 8 SDK alone **cannot** build the `net10.0` targets (`NETSDK1045`). The .NET 8 runtime is required only to *run* the v13 package (and to run the v13 integration test suite, which boots a real Umbraco 13 stack in-process — the v13 unit tests run on .NET 10).

## See also

- [`README.md`](../README.md) — feature overview + install.
- [`configuration.md`](configuration.md) — every `AiVisibility:` key (keys added in v1.3.0 or later apply to the v17 package only).
- [`robots-audit.md`](robots-audit.md) — Health Check + bot-list refresh.
- [`getting-started.md`](getting-started.md) — extraction-region contract + per-page exclusion.
