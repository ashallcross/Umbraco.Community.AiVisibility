namespace LlmsTxt.Umbraco.Configuration;

/// <summary>
/// Strongly-typed binding for the <c>LlmsTxt:</c> section of <c>appsettings.json</c>.
/// Story 1.1 shipped a minimal surface; Story 1.2 added the per-page cache TTL.
/// Story 2.1 added the <c>/llms.txt</c> manifest configuration surface
/// (<see cref="SiteName"/>, <see cref="SiteSummary"/>, <see cref="LlmsTxtBuilder"/>).
/// Story 2.2 added the <c>/llms-full.txt</c> manifest configuration surface
/// (<see cref="MaxLlmsFullSizeKb"/>, <see cref="LlmsFullScope"/>,
/// <see cref="LlmsFullBuilder"/>). Story 2.3 added the <see cref="Hreflang"/>
/// opt-in flag (FR25) for sibling-culture variant suffixes in <c>/llms.txt</c>.
/// Story 3.1 added the top-level <see cref="ExcludedDoctypeAliases"/> +
/// <see cref="SettingsResolverCachePolicySeconds"/> + <see cref="Migrations"/>
/// surface that <c>ILlmsSettingsResolver</c> consumes to overlay the Settings
/// doctype values onto these appsettings values.
/// </summary>
public sealed class LlmsTxtSettings
{
    public const string SectionName = "LlmsTxt";

    /// <summary>
    /// Adopter-configured CSS selector list, consulted after the built-in
    /// <c>data-llms-content</c> → <c>&lt;main&gt;</c> → <c>&lt;article&gt;</c> chain
    /// and before the SmartReader fallback.
    /// </summary>
    public IReadOnlyList<string> MainContentSelectors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Cache TTL for per-page Markdown extraction results, in seconds. Default: 60s.
    /// <para>
    /// Trade-off: shorter TTL means publish-driven invalidation is the only freshness
    /// signal that matters (broadcast is sub-second); longer TTL reduces re-render
    /// load but can mask out-of-band content changes that don't fire
    /// <c>ContentCacheRefresherNotification</c>.
    /// </para>
    /// <para>
    /// Setting to <c>0</c> effectively disables caching — each request re-renders.
    /// Adopters who need that behaviour can set <c>"LlmsTxt:CachePolicySeconds": 0</c>
    /// in <c>appsettings.json</c>.
    /// </para>
    /// </summary>
    public int CachePolicySeconds { get; init; } = 60;

    /// <summary>
    /// Site name override emitted as the H1 of <c>/llms.txt</c>. When null/empty,
    /// the package falls back to the matched root content node's <c>Name</c> (or
    /// the literal <c>"Site"</c> when no root resolves).
    /// <para>
    /// Source in Story 2.1: <c>appsettings.json</c> only. Story 3.1 introduces
    /// <c>ILlmsSettingsResolver</c> so the Settings doctype value (when present)
    /// overlays this appsettings value without changing the contract here.
    /// </para>
    /// </summary>
    public string? SiteName { get; init; }

    /// <summary>
    /// One-paragraph site summary emitted as the blockquote under the H1 of
    /// <c>/llms.txt</c>. When null/empty, the blockquote line emits an empty
    /// marker (<c>&gt; </c>).
    /// <para>
    /// Source in Story 2.1: <c>appsettings.json</c> only. Story 3.1 overlays the
    /// Settings doctype value via <c>ILlmsSettingsResolver</c>.
    /// </para>
    /// </summary>
    public string? SiteSummary { get; init; }

    /// <summary>
    /// Configuration sub-section binding the <c>/llms.txt</c> manifest builder's
    /// behaviour: section grouping by doctype alias, per-page summary property
    /// alias, and the manifest's HTTP <c>Cache-Control</c> max-age.
    /// </summary>
    public LlmsTxtBuilderSettings LlmsTxtBuilder { get; init; } = new();

    /// <summary>
    /// Hard byte cap for the <c>/llms-full.txt</c> manifest body (Story 2.2). Default
    /// 5120 KB (5 MB) per <c>package-spec.md</c> § 10. Pages are emitted in the
    /// configured <see cref="LlmsFullBuilderSettings.Order"/> until the next page
    /// would push the running total over <c>MaxLlmsFullSizeKb * 1024</c> bytes; the
    /// builder then appends a truncation footer documenting how many pages were
    /// emitted of the total in scope.
    /// <para>
    /// Setting to <c>0</c> or a negative value triggers a defensive fallback: the
    /// cap is treated as unlimited and a <c>Warning</c> is logged. Configuration
    /// validation belongs to Story 3.3 onboarding, not the hot path.
    /// </para>
    /// <para>
    /// Source in Story 2.2: <c>appsettings.json</c> only. Story 3.1's
    /// <c>ILlmsSettingsResolver</c> may overlay this with a Settings doctype value
    /// without changing the contract here.
    /// </para>
    /// </summary>
    public int MaxLlmsFullSizeKb { get; init; } = 5120;

    /// <summary>
    /// Configuration sub-section binding the <c>/llms-full.txt</c> manifest's
    /// <b>scope</b>: the subset of pages eligible for inclusion. Default scope is
    /// the whole site (every published descendant under the matched hostname's
    /// root) minus the default <c>ExcludedDocTypeAliases</c>.
    /// </summary>
    public LlmsFullScopeSettings LlmsFullScope { get; init; } = new();

    /// <summary>
    /// Configuration sub-section binding the <c>/llms-full.txt</c> manifest
    /// builder's behaviour: the page <see cref="LlmsFullBuilderSettings.Order"/>
    /// and the manifest's HTTP <c>Cache-Control</c> max-age. Distinct from
    /// <see cref="LlmsTxtBuilder"/> (the index manifest) and
    /// <see cref="CachePolicySeconds"/> (per-page Markdown).
    /// </summary>
    public LlmsFullBuilderSettings LlmsFullBuilder { get; init; } = new();

    /// <summary>
    /// Story 2.3 — opt-in <c>hreflang</c>-style cross-references in
    /// <c>/llms.txt</c> (FR25). When <see cref="HreflangSettings.Enabled"/> is
    /// <c>true</c>, each manifest link is followed by zero-or-more sibling-culture
    /// suffixes in the form <c>(culture: /culture/path.md)</c>, in BCP-47
    /// lexicographic order. Off by default per FR25.
    /// <para>
    /// Hreflang is <b>only</b> applied to <c>/llms.txt</c>. <c>/llms-full.txt</c>
    /// is a single-culture concatenated dump (consumed off-site as a
    /// self-contained body keyed to the matched <c>IDomain</c>); cross-culture
    /// variant linkage is meaningless inside a body that's already culture-scoped.
    /// </para>
    /// <para>
    /// Source in Story 2.3: <c>appsettings.json</c> only. Story 3.1's
    /// <c>ILlmsSettingsResolver</c> may overlay this with a Settings doctype value
    /// without changing the contract here.
    /// </para>
    /// <para>
    /// Cache key shape <c>llms:llmstxt:{host}:{culture}</c> intentionally does
    /// NOT include the hreflang flag — flipping the flag while a cached body
    /// exists keeps the old body until the next invalidation or TTL expiry
    /// (default <see cref="LlmsTxtBuilderSettings.CachePolicySeconds"/> 300s).
    /// Acceptable trade-off: encoding the flag would let two bodies coexist for
    /// the same <c>(host, culture)</c>, doubling memory.
    /// </para>
    /// </summary>
    public HreflangSettings Hreflang { get; init; } = new();

    /// <summary>
    /// Story 3.1 — <b>top-level</b> doctype-alias exclusion list. Pages whose
    /// <c>IPublishedContent.ContentType.Alias</c> matches any entry (case-insensitive)
    /// are omitted from <b>all</b> routes: <c>/llms.txt</c>, <c>/llms-full.txt</c>,
    /// and the per-page <c>.md</c> route returns <c>404</c>.
    /// <para>
    /// <b>Distinct from</b> <see cref="LlmsFullScopeSettings.ExcludedDocTypeAliases"/>
    /// (Story 2.2's <c>/llms-full.txt</c>-only narrowing list, default
    /// <c>["errorPage", "redirectPage"]</c>): the top-level list applies to all
    /// routes; the <see cref="LlmsFullScopeSettings"/> list is a further narrowing
    /// on top of that. Cumulation is logical AND-NOT — a page must pass both
    /// filters to appear in <c>/llms-full.txt</c>.
    /// </para>
    /// <para>
    /// <c>ILlmsSettingsResolver</c> overlays the Settings-doctype
    /// <c>excludedDoctypeAliases</c> field as a <b>union</b> with this appsettings
    /// list — adopters' appsettings entries are never discarded by an editor edit.
    /// </para>
    /// <para>
    /// Default <see cref="Array.Empty{T}"/>: the appsettings layer adds no
    /// implicit exclusions. Adopters who want the same default exclusions
    /// <c>/llms-full.txt</c> ships with should set this list explicitly in
    /// <c>appsettings.json</c>.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ExcludedDoctypeAliases { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Story 3.1 — TTL (in seconds) for the resolver cache at
    /// <c>llms:settings:{host}:{culture}</c>. Default <c>300s</c> matches the
    /// manifest cache TTLs (<see cref="LlmsTxtBuilderSettings.CachePolicySeconds"/>
    /// + <see cref="LlmsFullBuilderSettings.CachePolicySeconds"/>).
    /// <para>
    /// Setting to <c>0</c> disables caching — every resolver call re-walks the
    /// content tree. Negative values are operator typos; the resolver clamps to
    /// <c>0</c> + logs <c>Warning</c> (matches
    /// <c>LlmsFullTxtController.policySeconds</c> defensive policy).
    /// </para>
    /// <para>
    /// Cache invalidation: <c>ContentCacheRefresherHandler</c> clears
    /// <c>llms:settings:{host}:</c> per bound hostname on every refresh
    /// notification (Story 3.1 AC5). Editor-driven Settings-node edits invalidate
    /// the cache within sub-second broadcast latency; TTL is the lower-bound
    /// freshness floor for adopters not using Umbraco's distributed cache
    /// refresher (e.g. external schema-management tools that bypass it).
    /// </para>
    /// </summary>
    public int SettingsResolverCachePolicySeconds { get; init; } = 300;

    /// <summary>
    /// Story 3.1 — migration-plan registration controls. Currently only carries
    /// <see cref="LlmsMigrationsSettings.SkipSettingsDoctype"/> for uSync
    /// coexistence (architecture.md line 1092 + epics.md AC1).
    /// </summary>
    public LlmsMigrationsSettings Migrations { get; init; } = new();

    /// <summary>
    /// Story 4.1 — controls for the always-on HTTP <c>Link</c> discoverability
    /// header emitted on opted-in HTML responses. See
    /// <see cref="DiscoverabilityHeaderSettings.Enabled"/> for the kill switch.
    /// </summary>
    public DiscoverabilityHeaderSettings DiscoverabilityHeader { get; init; } = new();

    /// <summary>
    /// Story 4.1 — Cloudflare Markdown-for-Agents <c>Content-Signal</c> response
    /// header configuration. Site-level default (<see cref="ContentSignalSettings.Default"/>)
    /// + per-doctype-alias override map (<see cref="ContentSignalSettings.PerDocTypeAlias"/>).
    /// Default policy: header NOT emitted (the package does not unilaterally assert
    /// content-use preferences for adopters). See the agent-readiness alignment doc
    /// at <c>_bmad-output/planning-artifacts/agent-readiness-scanner-alignment-2026-04-30.md</c>
    /// for the rationale.
    /// </summary>
    public ContentSignalSettings ContentSignal { get; init; } = new();

    /// <summary>
    /// Story 4.2 — toggles whether the robots audit fires once on host startup
    /// via <c>StartupRobotsAuditRunner</c>. Default <c>true</c> per
    /// package-spec.md § 13. Setting to <c>false</c> disables the startup
    /// invocation; the Backoffice Health Check view still triggers the audit
    /// on demand and the <c>RobotsAuditRefreshJob</c> still runs on its
    /// configured cadence.
    /// </summary>
    public bool RobotsAuditOnStartup { get; init; } = true;

    /// <summary>
    /// Story 4.2 — robots audit configuration sub-section. Carries the
    /// recurring refresh cadence and per-host fetch timeout used by
    /// <c>DefaultRobotsAuditor</c> + <c>RobotsAuditRefreshJob</c>.
    /// </summary>
    public RobotsAuditorSettings RobotsAuditor { get; init; } = new();

    /// <summary>
    /// Story 5.1 — request log configuration. Controls the kill switch
    /// for the package's default <c>IRequestLog</c> writer + the
    /// bounded queue / batch drainer parameters.
    /// </summary>
    public RequestLogSettings RequestLog { get; init; } = new();

    /// <summary>
    /// Story 5.1 — log retention configuration. Drives the
    /// <c>LogRetentionJob</c> (<c>IDistributedBackgroundJob</c>) that
    /// deletes rows older than <see cref="LogRetentionSettings.DurationDays"/>.
    /// </summary>
    public LogRetentionSettings LogRetention { get; init; } = new();

    /// <summary>
    /// Story 5.2 — server-side caps + defaults for the AI Traffic Backoffice
    /// dashboard's <c>LlmsAnalyticsManagementApiController</c> query surface.
    /// </summary>
    public AnalyticsSettings Analytics { get; init; } = new();
}

/// <summary>
/// Story 5.1 — configuration block for the request log writer + bounded
/// queue + batch drainer. Bound from <c>LlmsTxt:RequestLog</c>.
/// </summary>
public sealed class RequestLogSettings
{
    /// <summary>
    /// When <c>false</c>, the package's default
    /// <c>DefaultLlmsRequestLogHandler</c> short-circuits — notifications
    /// still fire (per AC3 — they're public events decoupled from the
    /// writer), but the default writer's <c>EnqueueAsync</c> is never
    /// called. Adopter notification handlers continue to receive events.
    /// Default <c>true</c>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Bounded channel capacity. When full, oldest entries are dropped
    /// (<c>BoundedChannelFullMode.DropOldest</c>) — adopters debugging
    /// recent traffic see fresh entries even under sustained crawl load.
    /// Clamped at consumption time to <c>[64, 65536]</c>. Default
    /// <c>1024</c>.
    /// </summary>
    public int QueueCapacity { get; init; } = 1024;

    /// <summary>
    /// Drain batch size — each scope opens once and inserts up to
    /// <c>BatchSize</c> entries. Clamped at consumption time to
    /// <c>[1, 1000]</c>. Default <c>50</c>.
    /// </summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// Maximum interval between batch flushes when the queue isn't yet
    /// full to <see cref="BatchSize"/>. Clamped at consumption time to
    /// <c>[1, 60]</c>. Default <c>1</c> second.
    /// </summary>
    public int MaxBatchIntervalSeconds { get; init; } = 1;

    /// <summary>
    /// How often the writer logs an overflow Warning (with the dropped
    /// count) when the bounded queue is full. Throttled to avoid log
    /// spam under heavy crawl. Clamped at consumption time to
    /// <c>[5, 3600]</c>. Default <c>60</c> seconds.
    /// </summary>
    public int OverflowLogIntervalSeconds { get; init; } = 60;
}

/// <summary>
/// Story 5.1 — configuration block for the log retention job. Bound from
/// <c>LlmsTxt:LogRetention</c>.
/// </summary>
public sealed class LogRetentionSettings
{
    /// <summary>
    /// Number of days to retain rows in <c>llmsTxtRequestLog</c>. Rows
    /// with <c>createdUtc &lt; UtcNow - DurationDays</c> are deleted by
    /// <c>LogRetentionJob</c> on every cycle. Set to <c>0</c> or negative
    /// to disable retention (<c>Period</c> returns
    /// <c>Timeout.InfiniteTimeSpan</c> — the runner never fires); the
    /// disable check runs BEFORE clamping. Otherwise clamped at
    /// consumption time to <c>[1, 3650]</c> (10 years). Default <c>90</c>.
    /// </summary>
    public int DurationDays { get; init; } = 90;

    /// <summary>
    /// How often the retention job runs. Set to <c>0</c> or negative to
    /// disable the recurring cycle (<c>Period</c> returns
    /// <c>Timeout.InfiniteTimeSpan</c>); the disable check runs BEFORE
    /// clamping. Otherwise clamped at consumption time to <c>[1, 8760]</c>
    /// (one year). Default <c>24</c> hours.
    /// </summary>
    public int RunIntervalHours { get; init; } = 24;

    /// <summary>
    /// Dev/test-only escape hatch. When set positive, the retention job's
    /// <c>Period</c> uses this value (in seconds) instead of
    /// <see cref="RunIntervalHours"/>. Lets the architect-A5 two-instance
    /// shared-SQL-Server exactly-once gate verify ≥3 cycles in minutes
    /// rather than days.
    /// <para>
    /// <b>Do NOT set this in production.</b> Seconds-precision cycles
    /// would hammer the host DB. <c>null</c> (default) means "use
    /// <see cref="RunIntervalHours"/>". Values <c>&lt;= 0</c> are treated
    /// as unset. Clamped at consumption time to <c>[1, 86400]</c> (one
    /// day).
    /// </para>
    /// </summary>
    public int? RunIntervalSecondsOverride { get; init; }
}

/// <summary>
/// Story 5.2 — configuration block for the AI Traffic Backoffice dashboard's
/// Management API (<c>LlmsAnalyticsManagementApiController</c>). Bound from
/// <c>LlmsTxt:Analytics</c>. All values are CEILINGS not floors per
/// project-context.md § Testing Rules — adopters narrow them to suit their
/// host DB sizing.
/// </summary>
public sealed class AnalyticsSettings
{
    /// <summary>
    /// Default page size when the request omits <c>?pageSize=</c>. Clamped at
    /// consumption time to <c>[1, MaxPageSize]</c>. Default <c>50</c>.
    /// </summary>
    public int DefaultPageSize { get; init; } = 50;

    /// <summary>
    /// Maximum allowed page size; requests above this clamp DOWN. Defends
    /// against unbounded JSON response sizes for large host DBs. Default
    /// <c>200</c>.
    /// </summary>
    public int MaxPageSize { get; init; } = 200;

    /// <summary>
    /// Default range span when the request omits <c>?from=</c>. Default 7 days.
    /// </summary>
    public int DefaultRangeDays { get; init; } = 7;

    /// <summary>
    /// Maximum allowed range span; wider requests clamp <c>from = to -
    /// MaxRangeDays</c> AND surface the <c>X-Llms-Range-Clamped: true</c>
    /// response header so the dashboard can display the effective range.
    /// Default <c>365</c> days.
    /// </summary>
    public int MaxRangeDays { get; init; } = 365;

    /// <summary>
    /// Soft cap on total in-range matching rows. When a query's
    /// <c>TotalItems</c> exceeds this, the response body carries
    /// <c>totalCappedAt</c> populated; the dashboard shows the
    /// "Showing first N results — narrow your date range" footer
    /// (epic Failure &amp; Edge Cases case 3). Set to <c>0</c> or negative to
    /// disable the cap surface entirely (the cap is informational only —
    /// pagination already bounds response size). Default <c>10000</c>.
    /// </summary>
    public int MaxResultRows { get; init; } = 10000;
}

/// <summary>
/// Story 4.2 — configuration block for <c>DefaultRobotsAuditor</c> +
/// <c>RobotsAuditRefreshJob</c>. Bound from <c>LlmsTxt:RobotsAuditor</c>.
/// </summary>
public sealed class RobotsAuditorSettings
{
    /// <summary>
    /// How often the <see cref="LlmsTxt.Umbraco.Background.RobotsAuditRefreshJob"/>
    /// (registered as an <c>IDistributedBackgroundJob</c>) re-runs the audit
    /// for every bound hostname. Default <c>24</c> hours. Set to <c>0</c> or
    /// negative to disable the recurring refresh — the Health Check view
    /// still triggers on-demand audits via the cache-miss path.
    /// </summary>
    public int RefreshIntervalHours { get; init; } = 24;

    /// <summary>
    /// Per-host <c>/robots.txt</c> fetch timeout in seconds. Default
    /// <c>5</c>. Distinct from the build-time fetch's MSBuild
    /// <c>Timeout="5000"</c> (also 5 seconds, but those are independent
    /// contracts).
    /// </summary>
    public int FetchTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Dev/test-only escape hatch. When set, the auditor composes the
    /// <c>/robots.txt</c> URI with the supplied port instead of the
    /// scheme default (443 for HTTPS, 80 for HTTP). Useful when running
    /// the TestSite on Kestrel's dev port (e.g. 44314) so the live audit
    /// can round-trip against the running site.
    /// <para>
    /// <b>Do NOT set this in production.</b> Standard hosting deploys
    /// serve <c>/robots.txt</c> on the scheme default port; overriding
    /// it would point the audit at the wrong listener (or fail entirely
    /// if nothing is bound). Convention: live in <c>appsettings.Development.json</c>
    /// only.
    /// </para>
    /// <para>
    /// <c>null</c> (default) means "use the scheme default port" —
    /// the production-correct behaviour.
    /// </para>
    /// </summary>
    public int? DevFetchPort { get; init; }

    /// <summary>
    /// Dev/test-only escape hatch. When set, the recurring refresh job's
    /// <c>Period</c> uses this value (in seconds) instead of
    /// <see cref="RefreshIntervalHours"/>. Useful for the architect-A5
    /// two-instance shared-SQL-Server exactly-once gate (where 1-hour cycles
    /// would make the test run prohibitively long).
    /// <para>
    /// <b>Do NOT set this in production.</b> Seconds-precision cycles would
    /// hammer adopter origins with /robots.txt fetches every minute; the
    /// hours-precision default reflects the actual production cadence
    /// recommended by the architecture.
    /// </para>
    /// <para>
    /// <c>null</c> (default) means "use <see cref="RefreshIntervalHours"/>
    /// in hours" — the production-correct behaviour. Values <c>&lt;= 0</c>
    /// are treated as unset.
    /// </para>
    /// </summary>
    public int? RefreshIntervalSecondsOverride { get; init; }
}

/// <summary>
/// Story 4.1 — kill-switch for the HTTP <c>Link: rel="alternate"; type="text/markdown"</c>
/// discoverability header emitted by <c>DiscoverabilityHeaderMiddleware</c>.
/// Default <c>true</c> per the zero-config three-route round-trip contract.
/// Read live via <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}.CurrentValue"/>
/// so flipping the flag at runtime takes effect on the next request.
/// </summary>
public sealed class DiscoverabilityHeaderSettings
{
    /// <summary>
    /// When <c>false</c>, <c>DiscoverabilityHeaderMiddleware</c> short-circuits
    /// before computing the canonical URL — neither <c>Link</c> nor
    /// <c>Vary: Accept</c> is written by this middleware.
    /// <para>
    /// This kill switch only affects the discoverability middleware. Two other
    /// surfaces emit <c>Vary: Accept</c> independently and are NOT gated by this
    /// flag: (1) Story 1.3's <c>AcceptHeaderNegotiationMiddleware</c> emits it on
    /// every published-content HTML response so HTML and Markdown alternates do
    /// not collide in shared caches; (2) <c>MarkdownResponseWriter</c> emits it on
    /// every Markdown 200/304 response so Vary symmetry holds across both Accept
    /// values of the same resource.
    /// </para>
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Story 4.1 — Cloudflare Content-Signal header policy. Site-level default
/// + per-doctype override map. The header value is passed through verbatim
/// (the package does not validate the Cloudflare directive grammar — adopters
/// who set malformed values get malformed headers).
/// </summary>
public sealed class ContentSignalSettings
{
    /// <summary>
    /// Site-level default value for the <c>Content-Signal</c> response header.
    /// <c>null</c> / empty / whitespace → header is NOT emitted (default).
    /// Example: <c>"ai-train=no, search=yes, ai-input=yes"</c>.
    /// </summary>
    public string? Default { get; init; }

    /// <summary>
    /// Per-doctype-alias override map. Keys are doctype aliases
    /// (<c>IPublishedContent.ContentType.Alias</c>), values are the
    /// Content-Signal value to emit for pages of that doctype.
    /// Lookup is case-insensitive at the resolver layer
    /// (<see cref="ContentSignalResolver"/>) — the dictionary's own comparer
    /// may not survive <c>Microsoft.Extensions.Configuration</c> binding.
    /// </summary>
    public IReadOnlyDictionary<string, string> PerDocTypeAlias { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Story 3.1 — controls for the package's schema migrations. Currently only
/// <see cref="SkipSettingsDoctype"/> ships; future migrations (e.g. Epic 5's
/// request-log table) may add their own opt-out flags here.
/// </summary>
public sealed class LlmsMigrationsSettings
{
    /// <summary>
    /// When <c>true</c>, <c>SettingsComposer</c> does NOT register
    /// <c>LlmsTxtSettingsMigrationPlan</c> with the Umbraco package-migration
    /// pipeline — the Settings doctype + per-page-exclusion composition are
    /// NOT created on first boot. Default <c>false</c>.
    /// <para>
    /// <b>Use case:</b> adopters using <a href="https://github.com/KevinJump/uSync">uSync</a>
    /// or <c>uSync.Complete</c> to own the schema lifecycle. uSync serialises
    /// the doctype on first install; setting this flag to <c>true</c> prevents
    /// our package-migration plan from racing uSync's import or duplicating
    /// the doctype. Documented in <c>docs/getting-started.md</c> § "uSync
    /// coexistence".
    /// </para>
    /// <para>
    /// <b>Caveat:</b> flipping this from <c>false</c> to <c>true</c> AFTER the
    /// migration has already run does NOT remove the doctype from the host DB
    /// (Umbraco's package-migration plan-state record persists the executed
    /// state). Adopters relocating schema ownership to uSync must delete the
    /// doctype manually first.
    /// </para>
    /// </summary>
    public bool SkipSettingsDoctype { get; init; }
}

/// <summary>
/// Configuration block for <c>DefaultLlmsTxtBuilder</c>. Bound from the
/// <c>LlmsTxt:LlmsTxtBuilder</c> sub-section.
/// </summary>
public sealed class LlmsTxtBuilderSettings
{
    /// <summary>
    /// Ordered list of H2 sections the manifest emits, each binding a section title
    /// to a list of doctype aliases. Section ordering is preserved; pages whose
    /// doctype isn't matched by any entry land in a default <c>"Pages"</c> section
    /// emitted after all configured sections.
    /// <para>
    /// When a configured section's <c>DocTypeAliases</c> match no published pages,
    /// the section is omitted from the output (and a <c>Warning</c> is logged
    /// referencing the missing aliases).
    /// </para>
    /// </summary>
    public IReadOnlyList<SectionGroupingEntry> SectionGrouping { get; init; }
        = Array.Empty<SectionGroupingEntry>();

    /// <summary>
    /// Property alias the builder reads to populate per-page summaries. When the
    /// property is missing or empty on a given page, the builder falls back to
    /// the first 160 characters of the page's body Markdown (truncated at the
    /// nearest word boundary, with an ellipsis appended on truncation).
    /// </summary>
    public string PageSummaryPropertyAlias { get; init; } = "metaDescription";

    /// <summary>
    /// Cache TTL for the <c>/llms.txt</c> manifest's HTTP <c>Cache-Control: max-age</c>
    /// header AND its in-memory cache lifetime. Default: 300s (matches the per-llmstxt
    /// guidance in <c>architecture.md</c> § Caching &amp; HTTP). Distinct from
    /// <see cref="LlmsTxtSettings.CachePolicySeconds"/> (per-page Markdown).
    /// </summary>
    public int CachePolicySeconds { get; init; } = 300;
}

/// <summary>
/// One configured H2 section in <c>/llms.txt</c>. Pages whose doctype alias appears
/// in <see cref="DocTypeAliases"/> are grouped under <see cref="Title"/>.
/// </summary>
public sealed class SectionGroupingEntry
{
    /// <summary>
    /// H2 title emitted for this section. Required (empty title → section ignored
    /// with a <c>Warning</c> log).
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Doctype aliases (case-insensitive) that route pages into this section.
    /// </summary>
    public IReadOnlyList<string> DocTypeAliases { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Scope configuration for the <c>/llms-full.txt</c> manifest (Story 2.2). Pre-page
/// inclusion is filtered in the controller before the builder sees the page list:
/// the controller resolves the hostname's root via <c>IDomainService</c>, optionally
/// narrows to a doctype-aliased descendant via <see cref="RootContentTypeAlias"/>,
/// then walks descendants and applies the include / exclude doctype filters.
/// <para>
/// All doctype matching is case-insensitive against
/// <c>IPublishedContent.ContentType.Alias</c>.
/// </para>
/// <para>
/// Per-doctype / per-page exclusion bools (<c>ExcludeFromLlmExports</c>) are Epic 3
/// (Story 3.1) territory. Story 2.2 honours <see cref="ExcludedDocTypeAliases"/>
/// from <c>appsettings</c> only.
/// </para>
/// </summary>
public sealed class LlmsFullScopeSettings
{
    /// <summary>
    /// Optional doctype alias narrowing the manifest scope. When non-null, the
    /// builder's descendant walk starts at the first descendant under the
    /// hostname's root whose <c>ContentType.Alias</c> matches (case-insensitive).
    /// When <c>null</c> (default) the scope is the whole hostname tree.
    /// <para>
    /// If the alias matches no descendant under the hostname's root, the controller
    /// falls back to the hostname root and logs a <c>Warning</c>.
    /// </para>
    /// </summary>
    public string? RootContentTypeAlias { get; init; }

    /// <summary>
    /// Optional positive doctype filter. When non-empty, only pages whose
    /// <c>ContentType.Alias</c> appears in this list (case-insensitive) are
    /// included. When empty (default) all doctypes are eligible.
    /// </summary>
    public IReadOnlyList<string> IncludedDocTypeAliases { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Negative doctype filter that always wins over
    /// <see cref="IncludedDocTypeAliases"/>. Default
    /// <c>["errorPage", "redirectPage"]</c> per <c>package-spec.md</c> § 10 — the
    /// two doctypes that universally ship as out-of-scope on Umbraco templates.
    /// Adopters can override the list entirely (<c>"ExcludedDocTypeAliases": []</c>
    /// removes the defaults).
    /// </summary>
    public IReadOnlyList<string> ExcludedDocTypeAliases { get; init; } = new[]
    {
        "errorPage",
        "redirectPage",
    };
}

/// <summary>
/// Configuration block for <c>DefaultLlmsFullBuilder</c>. Bound from the
/// <c>LlmsTxt:LlmsFullBuilder</c> sub-section.
/// </summary>
public sealed class LlmsFullBuilderSettings
{
    /// <summary>
    /// Page ordering policy for the manifest body. Default
    /// <see cref="LlmsFullOrder.TreeOrder"/> per <c>epics.md</c> § Story 2.2 AC4.
    /// </summary>
    public LlmsFullOrder Order { get; init; } = LlmsFullOrder.TreeOrder;

    /// <summary>
    /// Cache TTL for the <c>/llms-full.txt</c> manifest's HTTP
    /// <c>Cache-Control: max-age</c> header AND its in-memory cache lifetime.
    /// Default: 300s (matches the manifest guidance in <c>architecture.md</c>
    /// § Caching &amp; HTTP). Distinct from
    /// <see cref="LlmsTxtSettings.CachePolicySeconds"/> (per-page Markdown,
    /// default 60s) and from <see cref="LlmsTxtBuilderSettings.CachePolicySeconds"/>
    /// (the index manifest, default 300s).
    /// </summary>
    public int CachePolicySeconds { get; init; } = 300;
}

/// <summary>
/// Configuration block for the Story 2.3 hreflang variant suffix on
/// <c>/llms.txt</c>. Bound from the <c>LlmsTxt:Hreflang</c> sub-section.
/// <para>
/// See architecture.md § Multi-Site &amp; Multi-Language and FR25 for the v1
/// contract. The variant resolution walks the matched <c>IDomain</c> set
/// (one domain per <c>(root, culture)</c> pair) and emits a suffix per
/// sibling-culture variant of each page, in BCP-47 lexicographic order.
/// </para>
/// </summary>
public sealed class HreflangSettings
{
    /// <summary>
    /// When <c>true</c>, <c>DefaultLlmsTxtBuilder</c> emits sibling-culture
    /// variant suffixes after each link (e.g. <c>(fr-fr: /fr/about.md)</c>).
    /// Default <c>false</c> per FR25 — single-culture sites and adopters who
    /// haven't opted in see no change from Story 2.1's output.
    /// </summary>
    public bool Enabled { get; init; }
}

/// <summary>
/// Stable ordering policies for <c>/llms-full.txt</c> page emission (Story 2.2 AC4).
/// </summary>
public enum LlmsFullOrder
{
    /// <summary>
    /// Pages appear in the published-cache descendant walk order — root first,
    /// then descendants per
    /// <c>IDocumentNavigationQueryService.TryGetDescendantsKeys</c>. Default.
    /// </summary>
    TreeOrder = 0,

    /// <summary>
    /// Pages sorted ascending by <c>IPublishedContent.Name</c> using
    /// <c>StringComparer.OrdinalIgnoreCase</c>. Stable sort (LINQ
    /// <c>OrderBy</c> guarantees stability per .NET docs).
    /// </summary>
    Alphabetical = 1,

    /// <summary>
    /// Pages sorted descending by <c>IPublishedContent.UpdateDate</c> (newest
    /// first). Ties broken by tree-order index (stable secondary sort).
    /// </summary>
    RecentFirst = 2,
}
