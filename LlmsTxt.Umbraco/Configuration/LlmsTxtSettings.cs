namespace LlmsTxt.Umbraco.Configuration;

/// <summary>
/// Strongly-typed binding for the <c>LlmsTxt:</c> section of <c>appsettings.json</c>.
/// Story 1.1 shipped a minimal surface; Story 1.2 added the per-page cache TTL.
/// Story 2.1 added the <c>/llms.txt</c> manifest configuration surface
/// (<see cref="SiteName"/>, <see cref="SiteSummary"/>, <see cref="LlmsTxtBuilder"/>).
/// Story 2.2 added the <c>/llms-full.txt</c> manifest configuration surface
/// (<see cref="MaxLlmsFullSizeKb"/>, <see cref="LlmsFullScope"/>,
/// <see cref="LlmsFullBuilder"/>). Story 3.1 fills out the rest of the surface and
/// introduces <c>ILlmsSettingsResolver</c> for the doctype-overlay resolution.
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
