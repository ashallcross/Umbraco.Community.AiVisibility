// Temporary capture helper — used once to seed Fixtures/Extraction/clean-core-home/expected.md.
// Delete after the seed fixture exists. Lives behind a deliberately-skipped test so it never
// runs in CI.

using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmsTxt.Umbraco.Tests.Extraction;

[TestFixture, Explicit("Capture helper — run manually to regenerate expected.md")]
public class FixtureCaptureHelper
{
    [Test]
    public async Task CaptureCleanCoreHomeFixture()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(FixtureCaptureHelper).Assembly.Location)!;
        var testProjectDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", ".."));
        var fixtureDir = Path.Combine(testProjectDir, "Fixtures", "Extraction", "clean-core-home");
        var inputPath = Path.Combine(fixtureDir, "input.html");
        var expectedPath = Path.Combine(fixtureDir, "expected.md");

        var html = await File.ReadAllTextAsync(inputPath);

        var settings = new LlmsTxtSettings { MainContentSelectors = Array.Empty<string>() };
        var options = new StubOptions<LlmsTxtSettings>(settings);

        var extractor = new DefaultMarkdownContentExtractor(
            pageRenderer: null!,
            regionSelector: new DefaultContentRegionSelector(NullLogger<DefaultContentRegionSelector>.Instance),
            converter: new MarkdownConverter(),
            publishedUrlProvider: null!,
            settings: options,
            logger: NullLogger<DefaultMarkdownContentExtractor>.Instance);

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

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found));

        var normalised = result.Markdown!.ReplaceLineEndings("\n");
        await File.WriteAllTextAsync(expectedPath, normalised);

        TestContext.WriteLine($"Wrote {expectedPath} ({normalised.Length} bytes)");
    }

    private sealed class StubOptions<T> : IOptionsSnapshot<T> where T : class
    {
        public StubOptions(T value) { Value = value; }
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
