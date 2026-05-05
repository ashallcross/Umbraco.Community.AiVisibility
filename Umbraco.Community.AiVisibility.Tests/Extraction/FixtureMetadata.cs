namespace LlmsTxt.Umbraco.Tests.Extraction;

/// <summary>
/// Single source of truth for per-scenario fixture metadata used by both
/// <see cref="ExtractionQualityBenchmarkTests"/> and <c>FixtureCaptureHelper</c>.
/// Frontmatter (<c>title</c>, <c>url</c>, <c>updated</c>) is part of the byte-equal
/// gate; if these values drift between capture and verify, every fixture's
/// <c>expected.md</c> reports false drift on the frontmatter alone.
/// </summary>
internal static class FixtureMetadata
{
    public static string TitleFor(string scenario) => scenario switch
    {
        "clean-core-home" => "Welcome to Clean.Core",
        "clean-core-blog-list" => "Articles",
        "clean-core-blockgrid-cards" => "Our services",
        "clean-core-nested-tables-images" => "Architecture deep-dive",
        _ => throw new ArgumentException(
            $"Unknown fixture scenario '{scenario}'. Add a case to {nameof(FixtureMetadata)}.{nameof(TitleFor)} and {nameof(ContentKeyFor)} before adding the fixture directory.",
            nameof(scenario)),
    };

    /// <summary>
    /// Stable, scenario-keyed Guids — frontmatter doesn't surface the content key but
    /// keeping the seed deterministic makes future debug-print runs easier to compare.
    /// </summary>
    public static Guid ContentKeyFor(string scenario) => scenario switch
    {
        "clean-core-home" => Guid.Parse("00000000-0000-0000-0000-000000000001"),
        "clean-core-blog-list" => Guid.Parse("00000000-0000-0000-0000-000000000002"),
        "clean-core-blockgrid-cards" => Guid.Parse("00000000-0000-0000-0000-000000000003"),
        "clean-core-nested-tables-images" => Guid.Parse("00000000-0000-0000-0000-000000000004"),
        _ => throw new ArgumentException(
            $"Unknown fixture scenario '{scenario}'. Add a case to {nameof(FixtureMetadata)}.{nameof(TitleFor)} and {nameof(ContentKeyFor)} before adding the fixture directory.",
            nameof(scenario)),
    };
}
