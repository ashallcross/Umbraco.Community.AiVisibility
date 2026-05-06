namespace LlmsTxt.Umbraco.Tests.ManifestBenchmark;

/// <summary>
/// Story 2.3 — fixture descriptor for the parameterised
/// <c>ManifestQualityBenchmarkTests</c>. Mirrored 1:1 from each
/// <c>fixture.json</c> under <c>Fixtures/Manifests/&lt;scenario&gt;/</c>.
/// </summary>
internal sealed record ManifestFixture(
    string Description,
    string Hostname,
    string Culture,
    ManifestSettingsFixture Settings,
    IReadOnlyList<ManifestPageFixture> Pages,
    IReadOnlyDictionary<string, IReadOnlyList<ManifestVariantFixture>>? Variants);

/// <summary>
/// One page in the seeded fixture tree. Drives <see cref="IPublishedContent"/>
/// stub construction in <see cref="ManifestFixtureBuilder"/>.
/// </summary>
internal sealed record ManifestPageFixture(
    string Key,
    string Name,
    string ContentTypeAlias,
    string RelativeUrl,
    string? AbsoluteUrl,
    string Body,
    string? UpdateDate);

/// <summary>
/// One sibling-culture variant for hreflang. <see cref="RelativeUrl"/> is the
/// raw relative URL (no <c>.md</c> suffix); <see cref="ManifestFixtureBuilder"/>
/// applies the same suffix logic <c>HreflangVariantsResolver</c> would
/// (<c>/</c> → <c>/index.html.md</c>; otherwise append <c>.md</c>).
/// </summary>
internal sealed record ManifestVariantFixture(string Culture, string RelativeUrl);

/// <summary>
/// Subset of <see cref="AiVisibilitySettings"/> needed to drive the manifest
/// builders from a fixture. Optional fields fall back to defaults.
/// </summary>
internal sealed record ManifestSettingsFixture(
    string? SiteName,
    string? SiteSummary,
    bool HreflangEnabled,
    int? CachePolicySecondsLlmsTxt,
    int? MaxLlmsFullSizeKb,
    int? CachePolicySecondsLlmsFull);
