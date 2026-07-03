using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;

namespace Umbraco.Community.AiVisibility.V13.Tests.Extraction;

/// <summary>
/// TFM-stability proof for the shared extraction pipeline, not a re-validation of
/// extraction logic. Runs the shared extraction fixtures through the extractor on
/// <b>net8.0</b> and asserts byte-identical Markdown vs <c>expected.md</c>.
///
/// <para>
/// This is the ONE cheap proof per the test-density ceiling. The full ~60-case
/// <c>DefaultMarkdownContentExtractorTests</c> corpus is NOT ported — that logic is
/// the same assembly source pinned on net10; recompiled to net8 it is identical
/// code, so re-running the corpus is mirror-testing with no new edge case.
/// </para>
///
/// <para>
/// Pure HTML→Markdown via the internal <c>ExtractFromHtmlAsync</c> seam — no Umbraco
/// runtime, no boot. Runs fine under the net10 host (no .NET 8 runtime needed),
/// hence NOT <c>[Category("Integration")]</c> and NOT under the booted
/// <c>…Integration</c> namespace.
/// </para>
/// </summary>
[TestFixture]
[Category("ExtractionQuality")]
public sealed class V13ExtractionTfmStabilityTests
{
    private static readonly string[] RequiredScenarios =
    {
        "clean-core-home",
        "clean-core-blog-list",
        "clean-core-blockgrid-cards",
        "clean-core-nested-tables-images",
    };

    public static IEnumerable<TestCaseData> FixturePairs()
    {
        var root = ResolveFixtureRoot();
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var input = Path.Combine(dir, "input.html");
            var expected = Path.Combine(dir, "expected.md");
            if (File.Exists(input) && File.Exists(expected))
            {
                yield return new TestCaseData(Path.GetFileName(dir), input, expected)
                    .SetName($"V13_Extraction_{Path.GetFileName(dir)}");
            }
        }
    }

    [TestCaseSource(nameof(FixturePairs))]
    public async Task FixtureBytesEqualExpected_OnNet8(string scenarioName, string inputPath, string expectedPath)
    {
        var html = await File.ReadAllTextAsync(inputPath);
        var expected = (await File.ReadAllTextAsync(expectedPath)).ReplaceLineEndings("\n");

        var extractor = BuildExtractor();
        var sourceUri = new Uri($"https://example.test/{scenarioName}");
        var meta = new ContentMetadata(
            Title: TitleFor(scenarioName),
            AbsoluteUrl: sourceUri.ToString(),
            UpdatedUtc: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            ContentKey: ContentKeyFor(scenarioName),
            Culture: "en-GB");

        var result = await extractor.ExtractFromHtmlAsync(html, sourceUri, meta, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found),
            $"Fixture {scenarioName} did not extract — status {result.Status}: {result.Error?.Message}");

        var actual = result.Markdown!.ReplaceLineEndings("\n");
        Assert.That(actual, Is.EqualTo(expected),
            $"Fixture {scenarioName} drifted on net8 — the Core HTML→Markdown pipeline is NOT byte-stable across TFMs. " +
            "Same expected.md the v17 (net10) benchmark verifies; a net8 divergence here is a real TFM-portability bug.");
    }

    /// <summary>Assert by identity; a rename/delete would pass a count-only check.</summary>
    [Test]
    public void FixtureDiscovery_AllRequiredScenariosPresent()
    {
        var discovered = FixturePairs()
            .Select(p => (string)p.Arguments[0]!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = RequiredScenarios.Where(r => !discovered.Contains(r)).ToArray();

        Assert.That(missing, Is.Empty,
            "The four shared extraction fixtures must be link-shared from the v17 Tests project into the v13 test bin. " +
            "Missing: " + string.Join(", ", missing) + ". Check the <None Include=\"..\\…Tests\\Fixtures\\Extraction\\**\"> link-share in the csproj.");
    }

    // ---- Inlined fixture metadata (must match the v17 FixtureMetadata values that
    //      were baked into expected.md's frontmatter, else byte-equality fails). ----

    private static string TitleFor(string scenario) => scenario switch
    {
        "clean-core-home" => "Welcome to Clean.Core",
        "clean-core-blog-list" => "Articles",
        "clean-core-blockgrid-cards" => "Our services",
        "clean-core-nested-tables-images" => "Architecture deep-dive",
        _ => throw new ArgumentException($"Unknown fixture scenario '{scenario}'.", nameof(scenario)),
    };

    private static Guid ContentKeyFor(string scenario) => scenario switch
    {
        "clean-core-home" => Guid.Parse("00000000-0000-0000-0000-000000000001"),
        "clean-core-blog-list" => Guid.Parse("00000000-0000-0000-0000-000000000002"),
        "clean-core-blockgrid-cards" => Guid.Parse("00000000-0000-0000-0000-000000000003"),
        "clean-core-nested-tables-images" => Guid.Parse("00000000-0000-0000-0000-000000000004"),
        _ => throw new ArgumentException($"Unknown fixture scenario '{scenario}'.", nameof(scenario)),
    };

    private static DefaultMarkdownContentExtractor BuildExtractor()
    {
        var settings = new AiVisibilitySettings { MainContentSelectors = Array.Empty<string>() };
        var options = new StubOptionsSnapshot<AiVisibilitySettings>(settings);

        return new DefaultMarkdownContentExtractor(
            pageRenderer: null!,          // exercised via the internal seam; unused by ExtractFromHtmlAsync
            regionSelector: new DefaultContentRegionSelector(NullLogger<DefaultContentRegionSelector>.Instance),
            converter: new MarkdownConverter(),
            publishedUrlProvider: null!,  // unused in ExtractFromHtmlAsync
            httpContextAccessor: null!,   // unused in ExtractFromHtmlAsync
            settings: options,
            logger: NullLogger<DefaultMarkdownContentExtractor>.Instance);
    }

    private static string ResolveFixtureRoot()
    {
        // Fixtures are link-shared into the test bin under Fixtures/Extraction/
        // (CopyToOutputDirectory=PreserveNewest), so resolve relative to the assembly.
        var assemblyDir = Path.GetDirectoryName(typeof(V13ExtractionTfmStabilityTests).Assembly.Location)!;
        return Path.Combine(assemblyDir, "Fixtures", "Extraction");
    }

    private sealed class StubOptionsSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public StubOptionsSnapshot(T value) { Value = value; }
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
