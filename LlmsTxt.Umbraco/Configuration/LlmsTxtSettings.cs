namespace LlmsTxt.Umbraco.Configuration;

/// <summary>
/// Strongly-typed binding for the <c>LlmsTxt:</c> section of <c>appsettings.json</c>.
/// Story 1.1 shipped a minimal surface; Story 1.2 added the per-page cache TTL.
/// Story 2.1 added the manifest configuration surface (<see cref="SiteName"/>,
/// <see cref="SiteSummary"/>, <see cref="LlmsTxtBuilder"/>). Story 3.1 fills out the
/// rest of the surface and introduces <c>ILlmsSettingsResolver</c> for the
/// doctype-overlay resolution.
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
