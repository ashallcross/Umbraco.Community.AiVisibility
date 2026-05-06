// Capture helper — used to seed Fixtures/Extraction/<scenario>/expected.md from the
// live extractor against a captured input.html. Lives behind a deliberately-skipped
// test fixture so it never runs in CI.
//
// Usage:
//   1. Place a captured `input.html` under `Fixtures/Extraction/<scenario>/`
//   2. Add the scenario name to the [TestCase] list below
//   3. Run the explicit test from your IDE / CLI:
//        dotnet test LlmsTxt.Umbraco.slnx \
//          --filter "TestCategory=ExtractionFixtureCapture"
//      (Or run via the IDE's "Run with explicit-fixtures" toggle.)
//   4. Hand-diff the produced `expected.md` against the captured live output and
//      curate as documented in `Fixtures/Extraction/README.md`.

using Umbraco.Community.AiVisibility.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmsTxt.Umbraco.Tests.Extraction;

[TestFixture, Explicit("Capture helper — run manually to regenerate expected.md")]
[Category("ExtractionFixtureCapture")]
public class FixtureCaptureHelper
{
    [TestCase("clean-core-home")]
    [TestCase("clean-core-blog-list")]
    [TestCase("clean-core-blockgrid-cards")]
    [TestCase("clean-core-nested-tables-images")]
    public async Task CaptureFixture(string scenario)
    {
        var assemblyDir = Path.GetDirectoryName(typeof(FixtureCaptureHelper).Assembly.Location)!;
        var testProjectDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", ".."));
        var fixtureDir = Path.Combine(testProjectDir, "Fixtures", "Extraction", scenario);
        var inputPath = Path.Combine(fixtureDir, "input.html");
        var expectedPath = Path.Combine(fixtureDir, "expected.md");

        if (!File.Exists(inputPath))
        {
            Assert.Fail(
                $"Cannot capture {scenario}: input.html not found at {inputPath}. " +
                $"Capture the rendered HTML from the live TestSite first " +
                $"(see Fixtures/Extraction/README.md § 'Adding a new fixture').");
        }

        var html = await File.ReadAllTextAsync(inputPath);

        var settings = new AiVisibilitySettings { MainContentSelectors = Array.Empty<string>() };
        var options = new StubOptions<AiVisibilitySettings>(settings);

        var extractor = new DefaultMarkdownContentExtractor(
            pageRenderer: null!,
            regionSelector: new DefaultContentRegionSelector(NullLogger<DefaultContentRegionSelector>.Instance),
            converter: new MarkdownConverter(),
            publishedUrlProvider: null!,
            httpContextAccessor: null!,
            settings: options,
            logger: NullLogger<DefaultMarkdownContentExtractor>.Instance);

        var meta = new ContentMetadata(
            Title: FixtureMetadata.TitleFor(scenario),
            AbsoluteUrl: $"https://example.test/{scenario}",
            UpdatedUtc: new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc),
            ContentKey: FixtureMetadata.ContentKeyFor(scenario),
            Culture: "en-GB");

        var result = await extractor.ExtractFromHtmlAsync(
            html,
            new Uri($"https://example.test/{scenario}"),
            meta,
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found),
            $"Extraction did not produce a Found result for {scenario}: {result.Error?.Message}");

        var normalised = result.Markdown!.ReplaceLineEndings("\n");
        await File.WriteAllTextAsync(expectedPath, normalised);

        TestContext.Out.WriteLine($"Wrote {expectedPath} ({normalised.Length} bytes)");
    }

    private sealed class StubOptions<T> : IOptionsSnapshot<T> where T : class
    {
        public StubOptions(T value) { Value = value; }
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
