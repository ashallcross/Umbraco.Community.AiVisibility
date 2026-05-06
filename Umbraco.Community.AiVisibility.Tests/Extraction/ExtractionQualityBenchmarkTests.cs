using Umbraco.Community.AiVisibility.Configuration;
using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Tests.TestHelpers;
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
[Category("ExtractionQuality")]
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
        var sourceUri = new Uri($"https://example.test/{scenarioName}");
        var meta = new ContentMetadata(
            Title: FixtureMetadata.TitleFor(scenarioName),
            AbsoluteUrl: sourceUri.ToString(),
            UpdatedUtc: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            ContentKey: FixtureMetadata.ContentKeyFor(scenarioName),
            Culture: "en-GB");

        var result = await extractor.ExtractFromHtmlAsync(
            html,
            sourceUri,
            meta,
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found),
            $"Fixture {scenarioName} did not extract — status was {result.Status}: {result.Error?.Message}");

        var actual = result.Markdown!.ReplaceLineEndings("\n");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            var diff = UnifiedDiffFormatter.Format(expected, actual, contextLines: 3, maxOutputLines: 200);
            Assert.Fail(
                $"Fixture {scenarioName} drifted from expected.md.\n" +
                $"If this drift is intentional (improved extractor or refreshed Clean.Core capture), " +
                $"regenerate expected.md via FixtureCaptureHelper; otherwise the extractor regressed.\n\n" +
                diff);
        }
    }

    /// <summary>
    /// AC4 names the four required scenarios explicitly; assert by identity, not just
    /// count. A "renamed-fixture-still-passes" regression would otherwise be silent.
    /// </summary>
    private static readonly string[] RequiredScenarios =
    {
        "clean-core-home",
        "clean-core-blog-list",
        "clean-core-blockgrid-cards",
        "clean-core-nested-tables-images",
    };

    /// <summary>
    /// AC4 — every required scenario must appear in the discovered fixture set. A
    /// rename or accidental delete would otherwise pass the count-based check.
    /// </summary>
    [Test]
    public void FixtureDiscovery_AllRequiredScenariosPresent()
    {
        var discovered = FixturePairs()
            .Select(p => (string)p.Arguments[0]!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = RequiredScenarios
            .Where(r => !discovered.Contains(r))
            .ToArray();

        Assert.That(missing, Is.Empty,
            "Story 1.4 § AC4 requires the BlockList (clean-core-blog-list), BlockGrid " +
            "(clean-core-blockgrid-cards), and nested+tables+images " +
            "(clean-core-nested-tables-images) fixtures alongside the seed " +
            "(clean-core-home). Missing: " + string.Join(", ", missing) + ". " +
            "Fixtures live at LlmsTxt.Umbraco.Tests/Fixtures/Extraction/<scenario>/ " +
            "with both input.html and expected.md.");
    }

    /// <summary>
    /// Half-added fixtures (an <c>input.html</c> without <c>expected.md</c>, or vice
    /// versa) are silently dropped by <see cref="FixturePairs"/> — the contributor
    /// wouldn't see a failure even though their fixture isn't being exercised. Fail
    /// loudly when any directory has only one half.
    /// </summary>
    [Test]
    public void FixtureDirectories_AllHaveBothInputAndExpected()
    {
        var root = ResolveFixtureRoot();
        if (!Directory.Exists(root))
        {
            Assert.Fail($"Fixture root directory not found: {root}.");
            return;
        }

        var partial = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var hasInput = File.Exists(Path.Combine(dir, "input.html"));
            var hasExpected = File.Exists(Path.Combine(dir, "expected.md"));
            if (hasInput && !hasExpected)
            {
                partial.Add($"{name}: input.html present but expected.md missing — run FixtureCaptureHelper to generate it");
            }
            else if (hasExpected && !hasInput)
            {
                partial.Add($"{name}: expected.md present but input.html missing — add the captured Clean.Core HTML");
            }
        }

        Assert.That(partial, Is.Empty,
            "Half-added fixture directories detected:\n" + string.Join("\n", partial));
    }

    private static DefaultMarkdownContentExtractor BuildExtractor()
    {
        var settings = new AiVisibilitySettings { MainContentSelectors = Array.Empty<string>() };
        var options = new StubOptionsSnapshot<AiVisibilitySettings>(settings);

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
