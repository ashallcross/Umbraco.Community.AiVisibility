# Changelog

All notable changes to **Umbraco.Community.AiVisibility** are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html) (with a pre-1.0 caveat: v0.x minor versions may include breaking changes — call-outs below).

## [Unreleased]

### Known limitations

- **`RenderStrategy:Mode = Auto` — fallback trigger not yet runtime-verified.** The `Auto` strategy is documented to fall back to the `Loopback` renderer when the `Razor` renderer surfaces a `ModelBindingException` (the canonical "custom view model hijack" failure mode). The fallback's exception-shape match is currently asserted by unit tests using a synthetic `ModelBindingException`; it has not been verified end-to-end against a real Razor pipeline failure. Depending on which pipeline layer surfaces the failure, the real exception may arrive *wrapped* (`TargetInvocationException`, `AggregateException`, `RuntimeBinderException`) and bypass the fallback. If you set `Mode = Auto` and observe a 5xx response on a page whose controller hijacks the view model, pin `Mode = Razor` to revert to the v1.0.x default behaviour and [file an issue](https://github.com/ashallcross/Umbraco.Community.AiVisibility/issues) including the exception type from your logs so the trigger can be widened with evidence. `Mode = Razor` (the default) and `Mode = Loopback` are unaffected.

## [v1.0.1] — 2026-05-08: Marketplace listing fixes (docs-only)

Patch release — package code is identical to v1.0.0. Fixes the Umbraco Marketplace listing rendering, which is the only adopter-visible surface that needed polish post-launch.

### Fixed

- **README internal links** — every `docs/*.md` and `LICENSE` reference rewritten from a repo-relative path to an absolute `https://github.com/ashallcross/Umbraco.Community.AiVisibility/blob/main/...` URL. The Marketplace listing renderer does not resolve relative paths the way GitHub does (every relative link surfaced as a dead `<a href="">`); absolute URLs render correctly on both GitHub and the Marketplace.
- **`umbraco-marketplace.json` description** — rewritten as a single tight elevator-pitch paragraph (~190 words). The Marketplace renders the description card as one paragraph regardless of `\n\n` separators in the JSON; the multi-paragraph v1.0.0 wording collapsed into one wall of text. The detailed multi-section content lives in the README below the description card.

### Notes

- No code changes; same `Umbraco.Community.AiVisibility.dll`, same Vite bundle, same DI graph.
- NuGet publish required because the README ships inside the `.nupkg` and is immutable post-publish — adopters installing via `dotnet add package Umbraco.Community.AiVisibility` from v1.0.1 onward see the corrected README.
- Marketplace listing description refresh does NOT require a NuGet republish — the Marketplace re-fetches `umbraco-marketplace.json` from the repo's `<PackageProjectUrl>` every 2h (or immediately via the expedite endpoint).

## [v1.0.0] — 2026-05-07: launch readiness

This is the inaugural stable release. It folds a multi-step launch-readiness pass — correctness fixes from external code review, launch hygiene + packaging + docs polish, the package rename, and the NuGet + Marketplace ship — into a single big-bang version entry.

### Added

- **`umbraco-marketplace.json`** at repo root — Marketplace listing JSON validated against `https://marketplace.umbraco.com/umbraco-marketplace-schema.json` at submission time. Title `"AI Visibility for Umbraco"`, primary `Category: "Artificial Intelligence"`, alternate `"Search"`, `LicenseTypes: ["Free"]`, `PackageType: "Package"`, full `AuthorDetails` block.
- **`docs/architecture.md`** — adopter-facing summary covering how the package works (template = canonical visual form; render → extract → convert), what's in the box (mapping public surfaces to source folders), the six extension-point seams, lifetime + thread-safety constraints (Singleton vs Transient surfaces), and production deployment notes for load-balanced + split-role installs.
- **`docs/getting-started.md` § Prerequisites** — .NET 10.0+, Umbraco v17.3.2+, Node.js ≥ 24.11.1 (for source rebuilds only — NuGet adopters do not need Node).
- **`docs/getting-started.md` § "For editors"** — Backoffice user guide: Settings dashboard, AI Traffic dashboard, Robots audit Health Check, "What you DON'T need to do" anti-FAQs.
- **`docs/configuration.md` § "Migrating from a pre-v1 install"** — exhaustive old → new mapping table for every config key (`LlmsTxt:* → AiVisibility:*`), envvar prefix (`LlmsTxt__ → AiVisibility__`), cache prefix (`llms: → aiv:`), header prefix (`X-Llms- → X-AiVisibility-`). Adopters find/replace mechanically.
- **`docs/release-checklist.md` § "v1 cross-cutting non-functional + architectural-requirement audit"** — one row per non-functional requirement and architectural requirement with file path / commit SHA / test name / screenshot / doc-link evidence.
- **`docs/release-checklist.md` § "IValidateOptions sweep"** — audit table with one row per `AiVisibility:*` settings sub-block (validator shipped vs `no invariants worth encoding` justification).
- **`docs/screenshots/`** — 4-6 static PNG screenshots covering the Settings dashboard, AI Traffic dashboard (populated + empty states), Robots audit Health Check, sample `/llms.txt` output, sample `.md` output. Demo gif/video deferred to a follow-up release.
- **`Configuration/LogRetentionSettingsValidator`** — `internal sealed`, `IValidateOptions<AiVisibilitySettings>`. Encodes invariants on `LogRetention`: `DurationDays >= 1` OR `<= 0` for disable; `RunIntervalHours >= 1` OR `<= 0` for disable; `RunIntervalSecondsOverride >= 1` OR `null` for unset. Registered via `services.TryAddEnumerable(ServiceDescriptor.Singleton<…>)` in `NotificationsComposer`. Paired tests + `AppendedNotReplaced` registration test + `Compose_StartupValidation_NoCaptiveDependency` lifetime gate.
- **`Configuration/RobotsAuditorSettingsValidator`** — `internal sealed`. Invariants on `RobotsAuditor`: `RefreshIntervalHours >= 1` OR `<= 0` for disable; `FetchTimeoutSeconds >= 1`; `DevFetchPort` in `[1, 65535]` when set; `RefreshIntervalSecondsOverride >= 1` OR `null` for unset. Registered in `RobotsComposer`.
- **`Configuration/RequestLogSettingsValidator`** — `internal sealed`. Invariants on `RequestLog`: `QueueCapacity >= 64` (matches consumption-time clamp); `BatchSize >= 1`; `MaxBatchIntervalSeconds >= 1`; `OverflowLogIntervalSeconds >= 5`. Registered in `NotificationsComposer`.
- **`Configuration/LegacyConfigurationProbe`** — `IHostedService` that scans `IConfiguration` at boot for residual `LlmsTxt:` keys (left over from pre-1.0 installs) and emits a single `LogLevel.Warning` line listing the stale keys. **Warn-loud, no shim** — the package does NOT honour the old keys at runtime; silent fall-back to defaults for any unmigrated key is the intentional shape (the no-shim principle). Wired through the new `AiVisibilityComposer`.
- **csproj v1 metadata bump**: `<Version>0.1.0-alpha → 1.0.0`, `<Title>` → `"AI Visibility for Umbraco"`, NEW `<PackageIcon>icon.png`, NEW `<None Include="..\icon.png" Pack="true" PackagePath="\" Condition="Exists('..\icon.png')" />` pack item.
- **`icon.png`** at repo root (~512×512 placeholder PNG, charcoal-navy + white "AIV" monogram).
- **README badges**: NuGet version + CI status + Umbraco Marketplace listing alongside the existing Apache-2.0 license badge.
- **`NOTICE`** — Apache-2.0 third-party attribution audit verified post-pack: only `Umbraco.Community.AiVisibility.dll` ships under `lib/net10.0/`, transitive deps resolve adopter-side per `Microsoft.NET.Sdk.Razor`. `SmartReader` (Apache-2.0) attribution present; `AngleSharp` + `ReverseMarkdown` (MIT) do not require NOTICE entries.
- **Source-map exclusion** in `vite.config.ts` — production-default-off for `.map` files; `VITE_INCLUDE_SOURCEMAP=true` env var enables source maps for maintainer debugging.
- **`authenticated-fetch.ts`** Bellissima utility — bearer-token fetch helper unifying both dashboards' auth shape via `UMB_AUTH_CONTEXT.getOpenApiConfiguration()`. Strips both casings of `Authorization` from caller `options.headers` before merge so callers cannot override the helper's bearer-auth contract.
- **IPv6 host normalisation** rewrite in `NormaliseHost` — RFC 5952-compliant: lowercases the hex digits, strips the embedded zone-id (`%eth0`), preserves square brackets in URI form, normalises double-colon shorthand. Backed by parameterised tests covering the well-known IPv6 forms.
- **`docs/configuration.md`** — exhaustive `AiVisibility:` config-key reference: top-level + `LlmsTxtBuilder` + `LlmsFullScope` + `LlmsFullBuilder` + `Hreflang` + `DiscoverabilityHeader` + `ContentSignal` + `RobotsAuditor` + `RequestLog` + `LogRetention` + `Analytics` + `Migrations`. Each key gets type, default, valid range, what reads it, and "set this when you want to…" use case.
- **`docs/multi-site.md`** — adopter reference for hostname → root resolution: `IDomainService.GetAll(true)` matching, multi-`IDomain` deduplication for the AI Traffic dashboard, hreflang variant linking, catch-all + wildcard `*<rootId>` distinction.
- **`docs/release-checklist.md`** — pre-release verification + release execution + post-release sections.
- **`docs/dependency-status.md`** — living catalogue of NU1902/NU1903 vulnerability warnings + CS0618 obsolete-API call-sites with target migration windows (Umbraco 18 + Umbraco 19).
- **README rewrite** — full marketplace-grade adopter onboarding: compatibility table, install command, zero-config quick-start, "What you get" feature table, license + contribution notes, links to every `docs/` page.
- **`docs/marketplace-listing-checklist.md`** — canonical Umbraco Marketplace docs distillation. Covers csproj field requirements, Marketplace JSON authoring, expedite curl, validate.umbraco.com, listing-live confirmation. Cross-referenced from `docs/release-checklist.md`.

### Changed (BREAKING — package rename)

This is the **breaking-rename release**. Every adopter-facing identifier prefix carrying `LlmsTxt`, `LlmsTxt.Umbraco`, or `llms` was renamed in one big-bang step pre-1.0:

| Before | After |
|---|---|
| Package ID `LlmsTxt.Umbraco` | `Umbraco.Community.AiVisibility` |
| Root namespace `LlmsTxt.Umbraco.*` | `Umbraco.Community.AiVisibility.*` |
| Config section `LlmsTxt:` | `AiVisibility:` |
| Cache key prefix `llms:` | `aiv:` |
| HTTP header prefix `X-Llms-` | `X-AiVisibility-` |
| Backoffice manifest alias prefix `Llms.<Type>.<Name>` | `AiVisibility.<Type>.<Name>` |
| Backoffice custom-element tag prefix `<llms-...>` (only the dashboard tags — `<llms-ai-traffic-dashboard>` etc.) | `<aiv-...>` (`<aiv-ai-traffic-dashboard>`, `<aiv-settings-dashboard>`) |
| Settings doctype alias `llmsTxtSettings` | `aiVisibilitySettings` |
| DB table `llmsTxtRequestLog` | `aiVisibilityRequestLog` |
| Backoffice route prefix `/umbraco/management/api/v1/llmstxt/...` | `/umbraco/management/api/v1/aivisibility/...` |
| RCL static-asset path `App_Plugins/LlmsTxtUmbraco/` | `App_Plugins/UmbracoCommunityAiVisibility/` |
| Vite entry-bundle filename `llms-txt-umbraco.js` | `umbraco-community-aivisibility.js` |
| HTTP client name `LlmsTxt.RobotsAudit` | `AiVisibility.RobotsAudit` |
| Health Check root GUID | rotated 2026-05-06 (rationale: identity reset on rename) |

- **`AiVisibilityPackageMigrationPlan`** — extends the pre-rename migration plan (key + state-record GUIDs preserved per migration immutability) with one new `RenameRequestLogTable_2_0` step that renames `llmsTxtRequestLog → aiVisibilityRequestLog` (idempotent table-rename via `DatabaseSchemaCreatorFactory`).

### Changed (correctness fixes from external code review)

- **`LlmsRequestLogDrainHostedService` Subscriber-role behaviour** — drainer now starts (not skips) when `IServerRoleAccessor.CurrentServerRole == Subscriber`, matching the original design intent. Pre-fix, subscribers silently dropped writes.
- **Culture-aware URL provider** in the Markdown extraction pipeline — multi-culture adopters now receive the correct URL per culture for canonical-URL emission, content-negotiation alternate URLs, and `/llms.txt` link emission.
- See commit history for the additional correctness patches landed in the same review pass.

### Changed (v1 metadata + IValidateOptions sweep)

- **`Umbraco.Community.AiVisibility.csproj`** — `<Version>1.0.0`, `<Title>` "AI Visibility for Umbraco", `<PackageIcon>icon.png`, `<None Include="..\icon.png">` pack item.
- **`NotificationsComposer`** — registers `LogRetentionSettingsValidator` + `RequestLogSettingsValidator` via `services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<…>, …>())` (many-to-one shape — coexists with the existing `AiVisibilitySettingsValidator` for `Analytics`).
- **`RobotsComposer`** — registers `RobotsAuditorSettingsValidator` via the same canonical shape.
- **Hey API client deletion** — the auto-generated TypeScript HTTP client was deleted; both dashboards consume Management API endpoints via the new `authenticated-fetch.ts` helper directly. Removes the `Client/src/api/` build-time tooling churn.

### Migration from pre-1.0

**If you're upgrading from a v0.x pre-release install carrying environment variable or `appsettings.json` overrides under the legacy `LlmsTxt:` prefix, the package will warn-loud at boot but NOT honour the old keys.** Pre-1.0 "no shim" principle: silent fall-back to defaults is the runtime behaviour for any unmigrated key.

The package's `LegacyConfigurationProbe` runs once at host startup and emits a `LogLevel.Warning` listing the stale keys — point your structured-logging surface (Application Insights, Serilog file sink, console) at the warning text to discover what to migrate.

For the exhaustive old → new mapping (config keys, envvar prefixes, cache prefixes, header prefixes), see [`docs/configuration.md` § "Migrating from a pre-v1 install"](docs/configuration.md). The mapping is finite — adopters find/replace mechanically.

**Public external contracts are PRESERVED through the rename.** Adopters who only consume routes via HTTP (`*.md` URLs, `/llms.txt`, `/llms-full.txt`, `Accept: text/markdown` content negotiation, `Link: rel="llms-txt"` headers, `<link rel="alternate" type="text/markdown" …>` HTML markup, `data-llms-content` / `data-llms-ignore` extraction-region attributes) see ZERO changes. The `<llms-link />` and `<llms-hint />` Razor TagHelper element names are also retained — they sit in adopter `_ViewImports.cshtml` files and consuming Razor views; renaming would force adopters to edit every consuming view.

### Architectural decisions

- **Big-bang rename pre-1.0.** The rename absorbed every adopter-facing identifier shift in a single release pre-v1. Splitting the rename across multiple releases would have produced a longer migration path for adopters who happened to install pre-1.0 vs post-1.0. The big-bang shape closes the migration window in one step.
- **Package rename via the v1.0.0 ship, not a separate v0.11 entry.** The CHANGELOG preamble at line 3 is the file-level descriptor that reflects the current package name (`Umbraco.Community.AiVisibility`). Per-entry sub-sections (v0.10 down) are immutable historical record — they document what types and config keys were called at the time. Historical accuracy beats post-hoc consistency for archived entries.
- **`IValidateOptions<T>` sweep via `TryAddEnumerable` (NOT `TryAddSingleton`).** Many-to-one shape — multiple validators against the same `IValidateOptions<T>` coexist. `TryAddSingleton` would replace, breaking the canonical contract. v1.0.0 extends the contract to three more validators (LogRetention / RobotsAuditor / RequestLog) without touching the existing `AiVisibilitySettingsValidator` (Analytics) — colocated with consumer composers per the package's Configuration & DI conventions.
- **`umbraco-marketplace` token first in semicolon-separated `<PackageTags>`.** Marketplace pickup looks for this exact token in the package's tags; the Marketplace JSON fields complement (not replace) the csproj tags.
- **Marketplace listing primary `Category: "Artificial Intelligence"`, alternate `"Search"`.** The package's value-prop is making content visible *for* AI search engines — `"Search"` reaches the second-most-relevant audience after AI itself.
- **Demo media downgrade**: v1.0.0 ships static screenshots only. A 30-60s screen-capture demo is deferred to a follow-up release.
- **Author's-call validators**: shipped 3 mandatory (`LogRetentionSettingsValidator`, `RobotsAuditorSettingsValidator`, `RequestLogSettingsValidator`) + recorded `no invariants worth encoding` justifications for the optional ones in [`docs/release-checklist.md` § "IValidateOptions sweep"](docs/release-checklist.md). Coverage targets are CEILINGS, not floors — only encode invariants that catch real operator typos at boot.

### Tests

- **Test suite green throughout the launch-readiness pass.** Pre-rename baseline: 745. Post-rename: 747 (+2 net new for `LegacyConfigurationProbe`). Post-launch-hygiene: 755 (+8 net new for IPv6 NormaliseHost parameterised cases). v1.0.0 adds approximately 25 NEW tests for the IValidateOptions sweep (paired tests per validator + `AppendedNotReplaced` registration test + `StartupValidation_NoCaptiveDependency` lifetime gate per the canonical DI test contract).

### Non-breaking notes

- **The package rename is the only breaking change in v1.0.0.** Adopters carrying envvar / config overrides under the legacy `LlmsTxt:` prefix see warnings at boot + silent fall-back to defaults at runtime.
- **HTTP-route adopters see ZERO changes.** Public external contracts (`/llms.txt`, `/llms-full.txt`, `*.md` routes, `Accept: text/markdown` content negotiation, `Link: rel="llms-txt"` header value, `data-llms-content` / `data-llms-ignore` extraction-region attributes, `<link rel="alternate" type="text/markdown" …>` HTML markup, `<llms-link />` and `<llms-hint />` Razor TagHelper elements) are preserved through the rename.

## [v0.10] — AI Traffic Backoffice dashboard + Management API + permissions

### Added

- **AI Traffic Backoffice dashboard** — second dashboard tile under `Umb.Section.Settings` alongside the prior "LlmsTxt" Settings tile. Manifest alias `Llms.Dashboard.AiTraffic`, custom element tag `<llms-ai-traffic-dashboard>`, weight 90 (Settings tile @100 renders first). Read-only mini-analytics surface backed by the `llmsTxtRequestLog` table.
- **`LlmsAnalyticsManagementApiController`** — sealed; routes to `/umbraco/management/api/v1/llmstxt/analytics/...` via `[VersionedApiBackOfficeRoute("llmstxt/analytics")]`. Four GET actions:
  - `GET /requests` — paginated rows ordered `createdUtc DESC, id DESC`. Query parameters: `?from=` / `?to=` (ISO-8601 UTC, explicit timezone designator required); `?class=` (repeated, validated against `UserAgentClass` enum names); `?page=` (1-based) / `?pageSize=` (clamped to `[1, MaxPageSize]`). Surfaces `X-Llms-Range-Clamped: true` header when range exceeds `MaxRangeDays`. Surfaces `totalCappedAt` body field when total in-range rows exceed `MaxResultRows`.
  - `GET /classifications` — distinct UA classifications with at least one row in range, sorted by descending count. Drives the chip-toggle source so editors never see chips with zero matching rows.
  - `GET /summary` — single-row aggregate (count + first-seen + last-seen). Feeds the dashboard's "Showing N requests from X to Y" header line.
  - `GET /retention` — one-shot read of `LlmsTxt:LogRetention:DurationDays` for the retention-aware empty-state hint.
  - All four gated by `[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]`. Bearer-token auth via OpenIddict (cookie-only fetches return 401). 401/403 schemas auto-added by the framework's `BackOfficeSecurityRequirementsOperationFilterBase` — explicit `[ProducesResponseType(401|403)]` deliberately omitted.
- **Lit dashboard element** `LlmsAiTrafficDashboardElement` (file `llms-ai-traffic-dashboard.element.ts`). `UMB_AUTH_CONTEXT.getOpenApiConfiguration()` bearer-token fetches; AbortController-per-refresh prevents stale state mutation on rapid filter changes; native `<input type="date">` date-range pickers (UUI v17.3.x has no polished date primitive); `<uui-tag>`-based classification chip toggle with semantic colour mapping (AI training/search/user-triggered → primary; AI deprecated → warning; HumanBrowser → positive; CrawlerOther + Unknown → default); `<uui-pagination>` with `Math.min(totalPages, 200)` UI cap (DOM-explosion defence); empty-state with retention-aware hint pulled from `/retention` endpoint.
- **`ILlmsAnalyticsReader`** + **`DefaultLlmsAnalyticsReader`** — testability seam wrapping the three NPoco queries (paged rows, GROUP-BY classification counts, single-row aggregate). Public interface; internal default impl. **NOT a documented public extension point in v1** — adopters who replace `ILlmsRequestLog` with a non-DB sink ship their own dashboard against their own analytics surface.
- **`AnalyticsSettings` configuration block** under `LlmsTxt:Analytics:`. Properties: `DefaultPageSize` (50), `MaxPageSize` (200), `DefaultRangeDays` (7), `MaxRangeDays` (365), `MaxResultRows` (10000). All values are CEILINGS not floors — adopters narrow to suit their host DB sizing.
- **Vite output** — new `llms-ai-traffic-dashboard.element-<hash>.js` chunk (13.11 kB raw / 4.00 kB gzipped) emitted alongside the existing settings dashboard chunk. Both dashboards coexist; bundle output committed to the repo.
- **27 new tests** — `LlmsAnalyticsManagementApiControllerTests` (24 — auth-attribute reflection, GetRequests happy / validation / filtering, GetClassifications, GetSummary, GetRetention, sealed-class assertion, all-actions-are-GET assertion, cancellation, TryParseUtc helper) + `LlmsAnalyticsApiControllerCompositionTests` (1 — `Compose_StartupValidation_LlmsAnalyticsApiController_NoCaptiveDependency` lifetime gate) + `LlmsTxtSettingsDefaultsTests` extension (5 new asserts pinning the Analytics defaults).
- **Documentation.** `docs/getting-started.md` bumps v0.9 → v0.10 with the dashboard section + Analytics config-keys table. `docs/extension-points.md` adds a "Backoffice consumers of `ILlmsRequestLog`" section explaining the read-side caveat for adopters who override the writer with a non-DB sink.

### Changed

- **`NotificationsComposer`** — registers `ILlmsAnalyticsReader → DefaultLlmsAnalyticsReader` Singleton via `TryAddSingleton`. The composer-time hard-validation logic for `ILlmsRequestLog` is unchanged (still enforces `Singleton` lifetime on adopter overrides).
- **`Client/src/bundle.manifests.ts`** — additive import + spread of `dashboardAiTraffic` manifests alongside the existing `dashboardSettings`. The settings dashboard tile is unaffected.

### Architectural decisions

- **Read path bypasses `ILlmsRequestLog`.** `ILlmsRequestLog` is intentionally write-only (`EnqueueAsync` is its only method). The analytics controller queries the host DB's `llmsTxtRequestLog` table directly via NPoco. Pluggable read seam deferred to v1.1+ pending real-adopter demand.
- **Section placement: `Umb.Section.Settings`.** Both dashboards (Settings + AI Traffic) coexist under the same section.
- **Class naming: `LlmsAnalyticsManagementApiController`.** Sibling to the prior `LlmsSettingsManagementApiController`; both under `Controllers/Backoffice/`.

### Tests

- **737/737 passing** (711 baseline → +26 net new this version; +1 Analytics defaults assert in the existing `LlmsTxtSettingsDefaultsTests` fixture brings the total to +27).

### Non-breaking

- **Non-breaking.** The dashboard manifest is additive — the prior "LlmsTxt" Settings tile is unaffected. Adopters upgrading from v0.9 see the new "AI Traffic" tile appear under Settings as soon as they restart.

## [v0.9] — Public notifications + log table + `ILlmsRequestLog` writer + `IUserAgentClassifier`

### Added

- **Three sealed public notifications** in `LlmsTxt.Umbraco.Notifications/`: `MarkdownPageRequestedNotification` (fires from `MarkdownController` + `AcceptHeaderNegotiationMiddleware`), `LlmsTxtRequestedNotification` (fires from `LlmsTxtController`), `LlmsFullTxtRequestedNotification` (fires from `LlmsFullTxtController`, carries `BytesServed`). All implement `Umbraco.Cms.Core.Notifications.INotification`; published fire-and-forget via `IEventAggregator.PublishAsync`. **Skipped on 304 / 404 / 500** so adopter analytics aren't double-counted by revalidation.
- **`IUserAgentClassifier`** Singleton extension point + `DefaultUserAgentClassifier` projecting the `AiBotList` to a coarse 7-value `UserAgentClass` enum (`Unknown`, `AiTraining`, `AiSearchRetrieval`, `AiUserTriggered`, `AiDeprecated`, `HumanBrowser`, `CrawlerOther`). Match priority: AI tokens (longest substring first) → curated non-AI crawlers → browser tells.
- **`ILlmsRequestLog`** Singleton extension point + `DefaultLlmsRequestLog` (process-wide bounded `Channel<LlmsTxtRequestLogEntry>`, `BoundedChannelFullMode.DropOldest`). Adopters override with `services.AddSingleton<ILlmsRequestLog, MyImpl>()`; **composer-time hard-validation throws if a non-Singleton lifetime is registered** — same shape as `IRobotsAuditor`.
- **`DefaultLlmsRequestLogHandler`** — Scoped, subscribes to all three notifications via `INotificationAsyncHandler<T>`. Translates each notification to a `LlmsTxtRequestLogEntry` and forwards via `ILlmsRequestLog.EnqueueAsync`. Short-circuits when `LlmsTxt:RequestLog:Enabled: false` (notifications still fire — the kill switch is on the writer, not on the events).
- **`LlmsRequestLogDrainHostedService`** Singleton hosted service. Drains the channel into `llmsTxtRequestLog` in batches via Infrastructure-flavour `IScopeProvider` + NPoco `Database.InsertBulk`. Boot is never blocked (`Task.Run` pattern). Server-role gate (`SchedulingPublisher` / `Single` only) prevents N front-end servers all writing to shared DB. Tunable batch flush via `LlmsTxt:RequestLog:BatchSize` + `MaxBatchIntervalSeconds`.
- **`LogRetentionJob : IDistributedBackgroundJob`** — recurring exactly-once DELETE of rows older than `LlmsTxt:LogRetention:DurationDays` (default 90). Canonical `Task ExecuteAsync()` parameterless surface. Period clamps via `RunIntervalHours` (default 24h); `Timeout.InfiniteTimeSpan` when disabled (NOT `TimeSpan.Zero`). Concurrent-cycle guard via `Interlocked.CompareExchange`. Emits `LlmsTxt log retention job RUN — InstanceId={InstanceId} CycleStart={CycleStart} RowsDeleted={RowsDeleted}` for two-instance verification.
- **`AddRequestLogTable_1_0 : AsyncMigrationBase`** — chained into the existing `LlmsTxtSettingsMigrationPlan` (key remains `"LlmsTxt.Umbraco"`). Idempotent via `DatabaseSchemaCreatorFactory.Create(db).TableExists("llmsTxtRequestLog")`. Schema-from-annotations via NPoco `[TableName]` + `[Column]` + `[Length]` + `[NullSetting]` + `[Index]` on `LlmsTxtRequestLogEntry`.
- **`NotificationsComposer`** orchestrates the new graph. Registers `TimeProvider.System`, `IUserAgentClassifier`, `ILlmsNotificationPublisher` (the internal helper), `ILlmsRequestLog`, the drainer, the retention job, and three `AddNotificationAsyncHandler<T, DefaultLlmsRequestLogHandler>()` calls. Throws at composition time if `ILlmsRequestLog` is registered with a non-Singleton lifetime.
- **`ILlmsNotificationPublisher`** internal helper centralising the four publication sites' shared work (UA classification, referrer host parsing, exception-isolated `IEventAggregator.PublishAsync`). Not a public extension point — adopters subscribe via `INotificationAsyncHandler<T>` rather than replacing this helper.
- **Configuration keys.** `LlmsTxt:RequestLog:{Enabled, QueueCapacity, BatchSize, MaxBatchIntervalSeconds, OverflowLogIntervalSeconds}`. `LlmsTxt:LogRetention:{DurationDays, RunIntervalHours, RunIntervalSecondsOverride}`. All bounded values clamped at consumption time.
- **Documentation.** New `docs/extension-points.md` (canonical adopter reference for the three notifications + `ILlmsRequestLog` + `IUserAgentClassifier` + cross-links to existing extension points). `docs/getting-started.md` bumps v0.8 → v0.9 with the new surface section + config table. `docs/maintenance.md` extends the two-instance shared-SQL-Server setup with the `LogRetentionJob` exactly-once verification procedure.

### Changed

- **`LlmsTxtSettings.cs`** — added `RequestLog` (`RequestLogSettings`) and `LogRetention` (`LogRetentionSettings`) sub-sections.
- **`LlmsTxt.Umbraco/Persistence/`** — extended with `IUserAgentClassifier`, `DefaultUserAgentClassifier`, `UserAgentClass`, `ILlmsRequestLog`, `DefaultLlmsRequestLog`, `Entities/LlmsTxtRequestLogEntry`.
- **`LlmsTxt.Umbraco/Notifications/`** — new namespace housing the three notifications, `DefaultLlmsRequestLogHandler`, `ILlmsNotificationPublisher`, `DefaultLlmsNotificationPublisher`.
- **`LlmsTxt.Umbraco/Background/`** — added `LlmsRequestLogDrainHostedService` + `LogRetentionJob` alongside the existing `RobotsAuditRefreshJob`.
- **`LlmsTxt.Umbraco/Composers/`** — added `NotificationsComposer`.
- **`LlmsTxtSettingsMigrationPlan.DefinePlan()`** — chained the new `AddRequestLogTable_1_0` step (state-record GUID `9B3D7E4A-2C8F-4F1B-A5E0-7D9B2A6F1C8E`).

### Breaking changes

- **`MarkdownController`, `LlmsTxtController`, `LlmsFullTxtController`, `AcceptHeaderNegotiationMiddleware`** each gained one new constructor parameter (`ILlmsNotificationPublisher`). These types are package-internal surfaces, not public extension seams, but adopters who subclass / service-locate them directly will need to update. Adopters who consume the routes via HTTP only — or who subscribe to notifications via `INotificationAsyncHandler<T>` — need no changes.

## [v0.8] — Robots audit Health Check + build-time AI-bot-list sync

### Added

- **Backoffice Health Check `LLMs robots.txt audit`** at `Settings → Health Check → LLMs`. Auto-discovered via Umbraco's `TypeLoader`; surfaces matched-and-blocked AI crawlers grouped by category (training / search-retrieval / user-triggered / opt-out) with copy-pasteable suggested removals. Read-only — never modifies the host's `/robots.txt`. Stable Health Check ID at `Constants.HealthChecks.RobotsAuditGuid` (do NOT regenerate between releases — Umbraco persists IDs in adopter logs).
- **`IRobotsAuditor` public extension point** (Singleton lifetime). Default implementation `DefaultRobotsAuditor` fetches `/robots.txt` via `IHttpClientFactory`, parses User-agent / Disallow blocks (RFC 9309-tolerant), and cross-references against the embedded AI-bot list. Adopters override via `services.AddSingleton<IRobotsAuditor, MyImpl>()`.
- **Build-time AI-bot-list sync**. New `<Target Name="SyncAiBotList" BeforeTargets="BeforeBuild">` MSBuild target fetches `https://raw.githubusercontent.com/ai-robots-txt/ai.robots.txt/main/robots.txt`, verifies SHA-256 against the pinned `<ExpectedAiBotListSha256>` constant, and embeds the content as `LlmsTxt.Umbraco.HealthChecks.AiBotList.txt`. **SHA mismatch on a successful fetch is a hard build failure** (deliberate; protects against silent feed tampering). Offline / unreachable-source builds fall back to the committed snapshot at `LlmsTxt.Umbraco/HealthChecks/AiBotList.fallback.txt` with a warning.
- **`AiBotList`** Singleton loader with a hand-curated category map for ~80 known AI-crawler tokens. Two deprecated tokens flagged with their modern replacements: `anthropic-ai` → `ClaudeBot`, `Claude-Web` → `ClaudeBot`. Bytespider/Grok robots-noncompliance caveat surfaces in the Health Check description when those tokens are blocked.
- **`StartupRobotsAuditRunner : IHostedService`** — fires the audit once per bound hostname at host startup. Gated on `LlmsTxt:RobotsAuditOnStartup` (default `true`) and `IServerRoleAccessor.CurrentServerRole ∈ { SchedulingPublisher, Single }` (defensive — multi-front-end installs don't all hammer their own origin at boot).
- **`RobotsAuditRefreshJob : IDistributedBackgroundJob`** — recurring exactly-once refresh via Umbraco's host-DB-lock coordination. Period configured by `LlmsTxt:RobotsAuditor:RefreshIntervalHours` (default 24h; set to `0` to disable). Emits `Robots audit refresh job RUN — InstanceId={InstanceId} CycleStart={CycleStart}` log line for two-instance verification.
- **Build-target online + offline code paths** — `dotnet build` exercises the upstream-fetch path by default (verifies SHA against the pinned constant); maintainers can force the offline fallback path locally via `/p:AiBotListSourceUrl=http://localhost:65535/unreachable.txt` to verify the committed snapshot is the embedded resource. **No scheduled SHA-bump** — refresh is maintainer-only (PR review).
- **Configuration keys.** `LlmsTxt:RobotsAuditOnStartup` (default `true`), `LlmsTxt:RobotsAuditor:RefreshIntervalHours` (default `24`), `LlmsTxt:RobotsAuditor:FetchTimeoutSeconds` (default `5`).
- **Documentation.** New `docs/robots-audit.md` (full audit contract), new `docs/maintenance.md` (SHA-refresh process + two-instance shared-SQL-Server manual gate setup). `docs/getting-started.md` bumps v0.7 → v0.8 with the new surface section.

### Changed

- **`Constants.cs`** — added `Constants.HealthChecks.RobotsAuditGuid` and `Constants.Cache.RobotsPrefix`.
- **`LlmsCacheKeys.cs`** — added `RobotsPrefix` constant + `Robots(string? hostname)` helper. The robots-audit cache lives under a different invalidation regime than per-page / manifest caches (rewritten by the refresh job, not by content-cache refresher notifications).
- **`LlmsTxtSettings.cs`** — added `RobotsAuditOnStartup` + `RobotsAuditor` sub-section (`RefreshIntervalHours`, `FetchTimeoutSeconds`).
- **New `LlmsTxt.Umbraco/HealthChecks/` namespace** — `AiBotList`, `AiBotEntry`, `BotCategory`, `IRobotsAuditor`, `DefaultRobotsAuditor`, `RobotsAuditResult`, `RobotsAuditFinding`, `RobotsAuditOutcome`, `RobotsAuditHealthCheck`, `StartupRobotsAuditRunner`.
- **New `LlmsTxt.Umbraco/Background/` namespace** — `RobotsAuditRefreshJob` (introduced ahead of the later `LogRetentionJob`).
- **New `LlmsTxt.Umbraco/Composers/HealthChecksComposer.cs`** — wires `AiBotList` (Singleton), `IRobotsAuditor` (Singleton + `TryAdd*`), `RobotsAuditHealthCheck` (Transient), `StartupRobotsAuditRunner` (HostedService), `RobotsAuditRefreshJob` (Singleton `IDistributedBackgroundJob`).

### Migration

Non-breaking for adopters. The robots audit ships as net-new surface; existing routes, headers, controllers, and DI seams are unchanged. Adopters who want different audit semantics override `IRobotsAuditor` with a Singleton implementation. See [`docs/robots-audit.md` § Custom auditors](docs/robots-audit.md#custom-auditors).

### Notes

- The canonical `IDistributedBackgroundJob` method surface in Umbraco.Cms.Infrastructure 17.3.2 is `Task ExecuteAsync()` (parameterless) — verified against `~/.nuget/packages/umbraco.cms.infrastructure/17.3.2/lib/net10.0/Umbraco.Infrastructure.xml` lines 60-64. The package implements `ExecuteAsync` accordingly.

## [v0.7] — HTTP `Link` discoverability header + Razor TagHelpers + Cloudflare addendum headers

### Added

- **HTTP `Link: rel="alternate"; type="text/markdown"` discoverability header** on every opted-in HTML response. Auto-emitted by a new `DiscoverabilityHeaderMiddleware` registered via `UmbracoPipelineFilter.PostRouting`. Includes idempotent `Vary: Accept`. Headers are flushed via `Response.OnStarting` with a `StatusCode < 300` guard so downstream filters that rewrite to 4xx/5xx don't carry the header onto error responses.
- **`<llms-link />` Razor TagHelper** — emits `<link rel="alternate" type="text/markdown" href="/path.md" />` inside `<head>`.
- **`<llms-hint />` Razor TagHelper** — emits a visually-hidden `<div role="note">` with a body anchor pointing at the Markdown alternate. Visually hidden via the new `.llms-hint` CSS class shipped at `/llms-txt-umbraco.css` (RCL static asset).
- **`X-Markdown-Tokens: <integer>` response header** on every successful 200 Markdown response (omitted on 304 — body-derived). Cloudflare-convention character-based estimate (`Math.Max(1, length / 4)`).
- **`Content-Signal: <directives>` response header** on Markdown responses when configured. Off by default; configurable site-wide and per-doctype under `LlmsTxt:ContentSignal:Default` and `LlmsTxt:ContentSignal:PerDocTypeAlias:<alias>`. Rides 304 responses (RFC 7232 § 4.1 representation-metadata).
- **`ILlmsExclusionEvaluator` public extension seam.** Default implementation `DefaultLlmsExclusionEvaluator` is `public sealed` so adopters can wrap-and-delegate via the DI Decorator pattern. Replaces the previously-duplicated `IsExcludedAsync` private helpers in `MarkdownController` and `AcceptHeaderNegotiationMiddleware`.
- **Configuration keys.** `LlmsTxt:DiscoverabilityHeader:Enabled` (default `true`), `LlmsTxt:ContentSignal:Default` (default `null`), `LlmsTxt:ContentSignal:PerDocTypeAlias:<alias>` (default empty map). All read live via `IOptionsMonitor` — flipping at runtime takes effect on the next request without restart.
- **Documentation.** New `docs/data-attributes.md` covers the full surface — discoverability header, TagHelpers, optional CSS asset, Cloudflare alignment, exclusion-decorator pattern, `curl` verification.

### Changed (BREAKING — pre-1.0)

- **`MarkdownController` constructor signature changed.** Now takes `ILlmsExclusionEvaluator` and `IOptionsMonitor<LlmsTxtSettings>`. The previously-private `IsExcludedAsync` and `TryReadExcludeBool` helpers were removed (logic lifted into the shared evaluator). Adopters who subclass or service-locate the controller directly will fail to compile until they update. The controller is the package's own HTTP surface, not an adopter extension seam — the loud break is by design.
- **`IMarkdownResponseWriter.WriteAsync` gained a 4th positional parameter `string? contentSignal`.** A 3-arg overload remains available as `[Obsolete("Pass null for contentSignal explicitly via the 4-arg overload. This overload is removed in v1.0.")]` and forwards to the 4-arg version with `contentSignal: null`. **Adopters who *call* the interface** keep working with a deprecation warning. **Adopters who *implement* the interface** must add the 4-arg overload — their existing 3-arg implementation is no longer the abstract method on the interface, and they will lose Content-Signal emission until they wire it through.

### Fixed (review patches)

- `MarkdownAlternateUrl.Append("")`, `Append(null)`, and `Append("/")` now collapse to the same root alternate (`/index.html.md`) — previously `Append("")` returned `/.md` (inconsistent with the trailing-slash rule).
- `MarkdownAlternateUrl.Append` no longer hard-codes the `"index.html.md"` literal — uses `Constants.Routes.IndexHtmlMdSuffix`.
- `DiscoverabilityHeaderMiddleware` now uses `IsNullOrWhiteSpace` (not `IsNullOrEmpty`) for the canonical-URL guard so adopter `IPublishedUrlProvider` overrides returning whitespace-only values don't produce mangled `Link` values.
- `DiscoverabilityHeaderMiddleware` now sanitises the alternate URL for CR/LF and `<>` characters to defend against header injection from hostile/buggy `IPublishedUrlProvider` overrides.
- `MarkdownResponseWriter` now sanitises `Content-Signal` for CR/LF before writing to defend against header injection from malformed adopter config.
- `ContentSignalResolver.Resolve` is now defensively null-coalesced against `settings.ContentSignal` and `settings.ContentSignal.PerDocTypeAlias` being explicitly null (init-set property edge case).
- `<llms-link />` / `<llms-hint />` TagHelpers now catch `UriFormatException` around the request-URI build (previously bubbled to the Razor view) and `OperationCanceledException` (suppress output cleanly when the request is aborted mid-render).
- New CSS rules in `/llms-txt-umbraco.css`: `:focus-within`/`:focus-visible` reveal pattern (WCAG 2.4.7 Focus Visible), `@media (forced-colors: active)` hide (some High Contrast modes strip the `clip` rule), `@media print` hide.
- `_ViewImports.cshtml` adopter snippet uses namespace-scoped `@addTagHelper LlmsTxt.Umbraco.TagHelpers.*, LlmsTxt.Umbraco` (not wildcard `*, LlmsTxt.Umbraco`) to prevent future internal types in other namespaces auto-registering as TagHelpers.

### Migration

See [`docs/getting-started.md` § Upgrading from v0.6 to v0.7](docs/getting-started.md#upgrading-from-v06-to-v07) for adopter actions. Single-route adopters (only consuming `/llms.txt`, `/llms-full.txt`, or `.md` URLs without subclassing or implementing the package interfaces) need no code changes.

## [v0.6] — Zero-config defaults + onboarding hint

(Earlier history: see `git log` and per-commit messages.)
