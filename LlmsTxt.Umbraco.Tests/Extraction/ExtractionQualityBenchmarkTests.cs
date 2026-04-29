using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmsTxt.Umbraco.Tests.Extraction;

/// <summary>
/// Parameterised quality gate — for each <c>Fixtures/Extraction/&lt;scenario&gt;/</c>
/// folder containing both <c>input.html</c> and <c>expected.md</c>, runs the default
/// extractor against the input HTML and asserts the produced Markdown is byte-equal
/// to <c>expected.md</c>.
///
/// <para>
/// Story 1.1 ships exactly the <c>clean-core-home</c> seed fixture. Adam captures the
/// real Clean.Core 7.0.5 home-page render during the manual E2E gate and may replace
/// the seed; Story 1.4 expands the catalogue (BlockGrid cards, nested content, tables).
/// Drift in <c>expected.md</c> (regenerated from the live extractor) MUST be reviewed
/// by hand before commit — that's the gate's whole point.
/// </para>
/// </summary>
[TestFixture]
public class ExtractionQualityBenchmarkTests
{
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
                    .SetName($"Extraction_{Path.GetFileName(dir)}");
            }
        }
    }

    [TestCaseSource(nameof(FixturePairs))]
    public async Task FixtureBytesEqualExpected(string scenarioName, string inputPath, string expectedPath)
    {
        var html = await File.ReadAllTextAsync(inputPath);
        var expected = (await File.ReadAllTextAsync(expectedPath)).ReplaceLineEndings("\n");

        var extractor = BuildExtractor();
        var meta = new ContentMetadata(
            Title: "Welcome to Clean.Core",
            AbsoluteUrl: "https://example.test/home",
            UpdatedUtc: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            ContentKey: Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Culture: "en-GB");

        var result = await extractor.ExtractFromHtmlAsync(
            html,
            new Uri("https://example.test/home"),
            meta,
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found),
            $"Fixture {scenarioName} did not extract — status was {result.Status}: {result.Error?.Message}");

        var actual = result.Markdown!.ReplaceLineEndings("\n");
        Assert.That(actual, Is.EqualTo(expected),
            $"Fixture {scenarioName} drifted from expected.md. " +
            $"If this drift is intentional (improved extractor or refreshed Clean.Core capture), " +
            $"regenerate expected.md from the live output; otherwise the extractor regressed.");
    }

    private static DefaultMarkdownContentExtractor BuildExtractor()
    {
        var settings = new LlmsTxtSettings { MainContentSelectors = Array.Empty<string>() };
        var options = new StubOptionsSnapshot<LlmsTxtSettings>(settings);

        return new DefaultMarkdownContentExtractor(
            pageRenderer: null!, // exercised via the public path; benchmark uses the internal seam
            regionSelector: new DefaultContentRegionSelector(NullLogger<DefaultContentRegionSelector>.Instance),
            converter: new MarkdownConverter(),
            publishedUrlProvider: null!, // unused in ExtractFromHtmlAsync
            httpContextAccessor: null!, // unused in ExtractFromHtmlAsync
            settings: options,
            logger: NullLogger<DefaultMarkdownContentExtractor>.Instance);
    }

    private static string ResolveFixtureRoot()
    {
        // Test binaries live at .../LlmsTxt.Umbraco.Tests/bin/Debug/net10.0/
        // Fixtures live alongside the source at .../LlmsTxt.Umbraco.Tests/Fixtures/Extraction/
        var assemblyDir = Path.GetDirectoryName(typeof(ExtractionQualityBenchmarkTests).Assembly.Location)!;
        var testProjectDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", ".."));
        return Path.Combine(testProjectDir, "Fixtures", "Extraction");
    }

    private sealed class StubOptionsSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public StubOptionsSnapshot(T value) { Value = value; }
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
