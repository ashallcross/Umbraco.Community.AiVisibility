# Configuration reference

Every key under the `AiVisibility:` section of `appsettings.json`. The package's strongly-typed binding lives at `Umbraco.Community.AiVisibility/Configuration/AiVisibilitySettings.cs` — read that for xmldoc detail beyond the summary tables below.

Zero-config defaults produce useful output on a typical Umbraco site immediately. Every key is optional.

> **Note on resolution layers.** Most string + boolean keys are also editable via the **Settings → AI Visibility** Backoffice dashboard, which writes to a global `aiVisibilitySettings` content node. The `ISettingsResolver` overlays that node's values on top of `appsettings.json`; editor saves take effect within sub-second broadcast latency. Per-doctype overrides + per-page exclusions live ONLY on the dashboard / per-page property.

## Top-level keys

| Key | Type | Default | What it controls |
|---|---|---|---|
| `AiVisibility:SiteName` | `string?` | `null` | H1 emitted at the top of `/llms.txt`. When null/empty, falls back to the matched root content node's `Name` (or literal `"Site"` if no root resolves). |
| `AiVisibility:SiteSummary` | `string?` | `null` | Blockquote line emitted under the H1 of `/llms.txt`. When null/empty, the line is skipped. |
| `AiVisibility:CachePolicySeconds` | `int` | `60` | TTL for per-page Markdown extraction results. `0` disables caching (re-extract every request). |
| `AiVisibility:MaxLlmsFullSizeKb` | `int` | `5120` (5 MB) | Hard byte cap for `/llms-full.txt`. Pages emitted in `LlmsFullBuilder.Order` until the next page would push past the cap; truncation footer documents how many of the total emitted. `0` or negative → unlimited (with a `Warning` log). |
| `AiVisibility:ExcludedDoctypeAliases` | `string[]` | `[]` | Doctype aliases (case-insensitive) entirely omitted from `/llms.txt`, `/llms-full.txt`, and the per-page `.md` route (404). UNION'd with the editable Settings-doctype list. |
| `AiVisibility:SettingsResolverCachePolicySeconds` | `int` | `300` | TTL for the resolver overlay cache (`aiv:settings:{culture}`). `0` disables. |
| `AiVisibility:RobotsAuditOnStartup` | `bool` | `true` | When `true`, the robots audit fires once on host startup. The Backoffice Health Check view + `RobotsAuditRefreshJob` continue regardless. |
| `AiVisibility:MainContentSelectors` | `string[]` | `[]` | Adopter CSS selectors consulted after the built-in `data-llms-content` → `<main>` → `<article>` chain, before the SmartReader fallback. |

## `AiVisibility:LlmsTxtBuilder` — `/llms.txt` manifest builder

| Key | Type | Default | What it controls |
|---|---|---|---|
| `LlmsTxtBuilder:CachePolicySeconds` | `int` | `300` | Cache TTL for `/llms.txt` (HTTP `Cache-Control: max-age` + in-memory). |
| `LlmsTxtBuilder:PageSummaryPropertyAlias` | `string` | `"metaDescription"` | Property alias the builder reads for per-page summaries; falls back to the first 160 characters of body Markdown when missing/empty. |
| `LlmsTxtBuilder:SectionGrouping` | `[{ Title, DocTypeAliases[] }]` | `[]` | Ordered H2 sections grouping pages by doctype. Pages whose doctype is unmatched land in a default `"Pages"` section. |

## `AiVisibility:LlmsFullScope` — `/llms-full.txt` page-inclusion filter

| Key | Type | Default | What it controls |
|---|---|---|---|
| `LlmsFullScope:RootContentTypeAlias` | `string?` | `null` | Optional doctype alias narrowing the manifest scope. When set, the descendant walk starts at the first matching descendant under the hostname's root. |
| `LlmsFullScope:IncludedDocTypeAliases` | `string[]` | `[]` | Positive doctype filter. When non-empty, only matching pages are included. |
| `LlmsFullScope:ExcludedDocTypeAliases` | `string[]` | `["errorPage", "redirectPage"]` | Negative doctype filter — always wins over included. Adopters can override (`[]` removes the defaults). |

## `AiVisibility:LlmsFullBuilder` — `/llms-full.txt` builder behaviour

| Key | Type | Default | What it controls |
|---|---|---|---|
| `LlmsFullBuilder:Order` | `TreeOrder \| Alphabetical \| RecentFirst` | `TreeOrder` | Page ordering policy. |
| `LlmsFullBuilder:CachePolicySeconds` | `int` | `300` | Cache TTL for `/llms-full.txt`. `0` disables caching. |

## `AiVisibility:Hreflang` — sibling-culture variant suffixes on `/llms.txt`

| Key | Type | Default | What it controls |
|---|---|---|---|
| `Hreflang:Enabled` | `bool` | `false` | When `true`, each `/llms.txt` link gets sibling-culture suffixes (e.g. `(fr-fr: /fr/about.md)`). Off by default per FR25; only applied to `/llms.txt`, never `/llms-full.txt`. |

## `AiVisibility:DiscoverabilityHeader` — HTTP `Link` discoverability header

| Key | Type | Default | What it controls |
|---|---|---|---|
| `DiscoverabilityHeader:Enabled` | `bool` | `true` | Kill switch for the auto-emitted `Link: rel="alternate"; type="text/markdown"` header. The `Vary: Accept` header from `AcceptHeaderNegotiationMiddleware` is independent of this flag. Read live via `IOptionsMonitor`. |

## `AiVisibility:ContentSignal` — Cloudflare Markdown-for-Agents header policy

| Key | Type | Default | What it controls |
|---|---|---|---|
| `ContentSignal:Default` | `string?` | `null` (header NOT emitted) | Site-level default value for the `Content-Signal` response header. Example: `"ai-train=no, search=yes, ai-input=yes"`. |
| `ContentSignal:PerDocTypeAlias` | `Dictionary<string, string>` | `{}` | Per-doctype-alias override map. Case-insensitive at the resolver layer. |

## `AiVisibility:Migrations` — schema migration controls

| Key | Type | Default | What it controls |
|---|---|---|---|
| `Migrations:SkipSettingsDoctype` | `bool` | `false` | When `true`, `SettingsComposer` does NOT register `AiVisibilityPackageMigrationPlan` (uSync coexistence — see `docs/getting-started.md` § "uSync coexistence"). |

## `AiVisibility:RobotsAuditor` — robots audit + bot-list refresh

| Key | Type | Default | What it controls |
|---|---|---|---|
| `RobotsAuditor:RefreshIntervalHours` | `int` | `24` | How often `RobotsAuditRefreshJob` re-audits every bound hostname. `0` or negative disables the recurring refresh. |
| `RobotsAuditor:FetchTimeoutSeconds` | `int` | `5` | Per-host `/robots.txt` fetch timeout. |
| `RobotsAuditor:DevFetchPort` | `int?` | `null` | **Dev/test only** — overrides the scheme default port (443 / 80) when fetching `/robots.txt`. Useful for hitting a local Kestrel dev port (e.g. 44314). Do NOT set in production. |
| `RobotsAuditor:RefreshIntervalSecondsOverride` | `int?` | `null` | **Dev/test only** — seconds-precision cycle. Do NOT set in production. |

## `AiVisibility:RequestLog` — AI traffic log writer + bounded queue

| Key | Type | Default | What it controls |
|---|---|---|---|
| `RequestLog:Enabled` | `bool` | `true` | Kill switch for the package's default `IRequestLog` writer. Notifications still fire; only the default writer's `EnqueueAsync` is gated. |
| `RequestLog:QueueCapacity` | `int` | `1024` | Bounded channel capacity. When full, oldest entries dropped. Clamped to `[64, 65536]`. |
| `RequestLog:BatchSize` | `int` | `50` | Drain batch size. Clamped to `[1, 1000]`. |
| `RequestLog:MaxBatchIntervalSeconds` | `int` | `1` | Maximum interval between batch flushes. Clamped to `[1, 60]`. |
| `RequestLog:OverflowLogIntervalSeconds` | `int` | `60` | How often the writer logs an overflow Warning under sustained drop pressure. Clamped to `[5, 3600]`. |

## `AiVisibility:LogRetention` — `aiVisibilityRequestLog` row-retention job

| Key | Type | Default | What it controls |
|---|---|---|---|
| `LogRetention:DurationDays` | `int` | `90` | Days to retain rows. `0` or negative disables retention (`Period` returns infinite). Otherwise clamped to `[1, 3650]`. |
| `LogRetention:RunIntervalHours` | `int` | `24` | Cycle period for `LogRetentionJob`. `0` or negative disables. Clamped to `[1, 8760]` (one year). |
| `LogRetention:RunIntervalSecondsOverride` | `int?` | `null` | **Dev/test only** — seconds-precision cycle. Do NOT set in production. |

## `AiVisibility:Analytics` — AI Traffic dashboard Management API caps

| Key | Type | Default | What it controls |
|---|---|---|---|
| `Analytics:DefaultPageSize` | `int` | `50` | Page size when the request omits `?pageSize=`. Clamped to `[1, MaxPageSize]`. |
| `Analytics:MaxPageSize` | `int` | `200` | Maximum allowed page size; defends against unbounded JSON responses for large host DBs. |
| `Analytics:DefaultRangeDays` | `int` | `7` | Default range span when the request omits `?from=`. |
| `Analytics:MaxRangeDays` | `int` | `365` | Maximum allowed range span; wider requests clamp `from = to - MaxRangeDays` AND surface `X-AiVisibility-Range-Clamped: true`. |
| `Analytics:MaxResultRows` | `int` | `10000` | Soft cap on total in-range matching rows (informational footer in the dashboard). `0` or negative disables the cap surface. |

## Settings node + per-doctype overlay

The package's migration plan creates an `aiVisibilitySettings` doctype + a single global content node. The dashboard at **Settings → AI Visibility** writes to this node; `ISettingsResolver` overlays its values on top of the appsettings layer. Specifically:

- `SiteName`, `SiteSummary` — Settings-node values override the appsettings values when non-null.
- `ExcludedDoctypeAliases` — Settings-node list is **unioned** with the appsettings list (adopters' appsettings entries are never lost to an editor edit).

Per-page exclusion uses the `excludeFromLlmExports` boolean property on the `llmsTxtSettingsComposition` composition (added to the doctype tree by the same migration plan).

See `docs/getting-started.md` § "Per-page exclusion" for the per-page surface; `docs/multi-site.md` for the per-host story; `docs/extension-points.md` for the `ISettingsResolver` adopter contract (replace the resolver entirely for per-tenant overrides).

## Validation

`AiVisibilitySettings` ships an `IValidateOptions<AiVisibilitySettings>` implementation (`AiVisibilitySettingsValidator`) that throws on first read at boot if any required field violates a documented constraint. Failures throw a clear `ValidationException` with the offending key — flip the option in `appsettings.json` and re-boot.

The hot path additionally clamps numeric values at consumption time (the per-key clamp ranges noted above) so a misconfigured value never crashes a request — only the validator's boot-time check fails fast.
