using System.Text;
using System.Text.Json;
using Umbraco.Community.AiVisibility.LlmsTxt;
using Umbraco.Community.AiVisibility.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Umbraco.Community.AiVisibility.Tests.ManifestBenchmark;

/// <summary>
/// Story 2.3 AC4 / AC5 — parameterised quality benchmark for
/// <c>/llms.txt</c> + <c>/llms-full.txt</c>. Walks
/// <c>Umbraco.Community.AiVisibility.Tests/Fixtures/Manifests/&lt;scenario&gt;/</c>, loads
/// <c>fixture.json</c>, drives the default builder against stubbed
/// <see cref="IPublishedContent"/> derived from the fixture, and diffs the
/// output against <c>expected-llms.txt</c> (and optionally
/// <c>expected-llms-full.txt</c>) via the existing
/// <see cref="UnifiedDiffFormatter"/>.
/// <para>
/// Pattern mirrors Story 1.4's <c>ExtractionQualityBenchmarkTests</c> — same
/// path-resolution discipline (project-marker walk, NOT bin/&lt;config&gt; arithmetic),
/// same diff helper, same <c>[Category]</c> filter shape.
/// </para>
/// </summary>
[TestFixture]
[Category("ManifestQuality")]
public class ManifestQualityBenchmarkTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Per-scenario test source. Discovers each subdirectory of
    /// <c>Fixtures/Manifests/</c> and produces one test case per scenario.
    /// </summary>
    public static IEnumerable<TestCaseData> EnumerateScenarios()
    {
        var manifestsDir = LocateManifestFixturesDirectory();
        if (manifestsDir is null || !Directory.Exists(manifestsDir))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(manifestsDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var scenarioName = Path.GetFileName(dir);
            yield return new TestCaseData(dir).SetName($"ManifestQuality({scenarioName})");
        }
    }

    /// <summary>
    /// Guard against silent-zero scenario discovery. If <see cref="LocateManifestFixturesDirectory"/>
    /// regresses (e.g. the project-marker walk fails on a future test-runner layout)
    /// or the fixture directory is wiped, <see cref="EnumerateScenarios"/> yields
    /// nothing and the parameterised test runs zero cases — NUnit reports green
    /// and AC4/AC5 coverage is silently disabled. This sanity test asserts the
    /// minimum-fixture-count invariant Spec AC5 mandates (≥ 2 v1 scenarios).
    /// </summary>
    [Test]
    public void EnumerateScenarios_FindsAtLeastTwoScenarios()
    {
        var count = EnumerateScenarios().Count();
        Assert.That(
            count,
            Is.GreaterThanOrEqualTo(2),
            $"AC5 mandates ≥ 2 fixture scenarios under Fixtures/Manifests/; found {count}. "
            + "Either fixtures are missing OR LocateManifestFixturesDirectory failed to walk to the project root.");
    }

    [TestCaseSource(nameof(EnumerateScenarios))]
    public async Task Manifest_MatchesExpectedFixture(string scenarioDir)
    {
        var fixture = LoadFixture(scenarioDir);
        var fb = ManifestFixtureBuilder.From(fixture);

        // /llms.txt — always exercised.
        var expectedLlmsTxtPath = Path.Combine(scenarioDir, "expected-llms.txt");
        if (File.Exists(expectedLlmsTxtPath))
        {
            var builder = new DefaultLlmsTxtBuilder(
                fb.UrlProvider,
                NSubstitute.Substitute.For<global::Umbraco.Cms.Core.Models.PublishedContent.IPublishedValueFallback>(),
                fb.Extractor,
                NullLogger<DefaultLlmsTxtBuilder>.Instance);
            var actual = await builder.BuildAsync(fb.ToLlmsTxtBuilderContext(), CancellationToken.None);
            var expected = await File.ReadAllTextAsync(expectedLlmsTxtPath, Encoding.UTF8);
            AssertByteEqual(expected, actual, $"{Path.GetFileName(scenarioDir)} — /llms.txt");
        }

        // /llms-full.txt — optional per scenario. Story 2.3's two v1 fixtures
        // exercise /llms.txt only; future scenarios may add expected-llms-full.txt.
        var expectedLlmsFullPath = Path.Combine(scenarioDir, "expected-llms-full.txt");
        if (File.Exists(expectedLlmsFullPath))
        {
            var fullBuilder = new DefaultLlmsFullBuilder(
                fb.UrlProvider,
                fb.Extractor,
                NullLogger<DefaultLlmsFullBuilder>.Instance);
            var ctx = new LlmsFullBuilderContext(
                Hostname: fb.Hostname,
                Culture: fb.Culture,
                RootContent: fb.Root,
                Pages: fb.Pages,
                Settings: fb.Settings.ToResolved());
            var actual = await fullBuilder.BuildAsync(ctx, CancellationToken.None);
            var expected = await File.ReadAllTextAsync(expectedLlmsFullPath, Encoding.UTF8);
            AssertByteEqual(expected, actual, $"{Path.GetFileName(scenarioDir)} — /llms-full.txt");
        }
    }

    private static ManifestFixture LoadFixture(string scenarioDir)
    {
        var path = Path.Combine(scenarioDir, "fixture.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Manifest scenario {scenarioDir} is missing fixture.json. "
                + $"Each Fixtures/Manifests/<scenario>/ folder must contain fixture.json + expected-llms.txt + README.md.");
        }
        var json = File.ReadAllText(path, Encoding.UTF8);
        var fixture = JsonSerializer.Deserialize<ManifestFixture>(json, JsonOptions);
        return fixture
            ?? throw new InvalidOperationException($"fixture.json at {path} deserialised to null");
    }

    private static void AssertByteEqual(string expected, string actual, string label)
    {
        // Normalise CRLF → LF on the expected side so cross-platform line-ending
        // committed-file drift doesn't fail the diff. Actual is built in-process
        // and uses LF natively.
        var expectedNormalised = expected.Replace("\r\n", "\n");
        if (string.Equals(expectedNormalised, actual, StringComparison.Ordinal))
        {
            return;
        }
        var diff = UnifiedDiffFormatter.Format(expectedNormalised, actual);
        Assert.Fail($"{label}\n{diff}");
    }

    /// <summary>
    /// Walk up from the running test assembly's directory looking for the
    /// <c>Umbraco.Community.AiVisibility.Tests</c> project marker, then descend into
    /// <c>Fixtures/Manifests/</c>. Avoids the brittle <c>bin/&lt;config&gt;/&lt;tfm&gt;</c>
    /// arithmetic Story 1.4 deferred-work flagged for the extraction fixture
    /// helper.
    /// </summary>
    private static string? LocateManifestFixturesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // The csproj sits in Umbraco.Community.AiVisibility.Tests/; bin output is below it.
            var projectFile = Path.Combine(dir.FullName, "Umbraco.Community.AiVisibility.Tests.csproj");
            if (File.Exists(projectFile))
            {
                var manifestDir = Path.Combine(dir.FullName, "Fixtures", "Manifests");
                return manifestDir;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
