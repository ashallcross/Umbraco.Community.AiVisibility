namespace LlmsTxt.Umbraco.Configuration;

/// <summary>
/// Strongly-typed binding for the <c>LlmsTxt:</c> section of <c>appsettings.json</c>.
/// Story 1.1 shipped a minimal surface; Story 1.2 added the per-page cache TTL.
/// Story 3.1 fills out the rest of the surface and introduces
/// <c>ILlmsSettingsResolver</c> for the doctype-overlay resolution.
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
}
