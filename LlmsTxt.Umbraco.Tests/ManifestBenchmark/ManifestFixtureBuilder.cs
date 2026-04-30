using LlmsTxt.Umbraco.Builders;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace LlmsTxt.Umbraco.Tests.ManifestBenchmark;

/// <summary>
/// Story 2.3 — converts a <see cref="ManifestFixture"/> into the inputs both
/// default builders need (<see cref="LlmsTxtBuilderContext"/> + the stubbed
/// <see cref="IPublishedUrlProvider"/> / <see cref="IMarkdownContentExtractor"/>
/// substitutes). Mirrors the <c>StubPage</c> patterns from
/// <c>DefaultLlmsTxtBuilderTests</c> + <c>DefaultLlmsFullBuilderTests</c> so
/// the benchmark exercises the real builder code path with real-shape inputs.
/// </summary>
internal sealed class ManifestFixtureBuilder
{
    public IPublishedUrlProvider UrlProvider { get; }
    public IMarkdownContentExtractor Extractor { get; }
    public LlmsTxtSettings Settings { get; }
    public IPublishedContent Root { get; }
    public IReadOnlyList<IPublishedContent> Pages { get; }
    public IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>? HreflangVariants { get; }
    public string Hostname { get; }
    public string Culture { get; }

    private ManifestFixtureBuilder(
        IPublishedUrlProvider urlProvider,
        IMarkdownContentExtractor extractor,
        LlmsTxtSettings settings,
        IPublishedContent root,
        IReadOnlyList<IPublishedContent> pages,
        IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>? hreflangVariants,
        string hostname,
        string culture)
    {
        UrlProvider = urlProvider;
        Extractor = extractor;
        Settings = settings;
        Root = root;
        Pages = pages;
        HreflangVariants = hreflangVariants;
        Hostname = hostname;
        Culture = culture;
    }

    public LlmsTxtBuilderContext ToLlmsTxtBuilderContext()
        => new(Hostname, Culture, Root, Pages, Settings, HreflangVariants);

    public static ManifestFixtureBuilder From(ManifestFixture fixture)
    {
        var urlProvider = Substitute.For<IPublishedUrlProvider>();
        var extractor = Substitute.For<IMarkdownContentExtractor>();

        var settings = new LlmsTxtSettings
        {
            SiteName = fixture.Settings.SiteName,
            SiteSummary = fixture.Settings.SiteSummary,
            LlmsTxtBuilder = new LlmsTxtBuilderSettings
            {
                CachePolicySeconds = fixture.Settings.CachePolicySecondsLlmsTxt ?? 300,
            },
            MaxLlmsFullSizeKb = fixture.Settings.MaxLlmsFullSizeKb ?? 5120,
            LlmsFullBuilder = new LlmsFullBuilderSettings
            {
                CachePolicySeconds = fixture.Settings.CachePolicySecondsLlmsFull ?? 300,
            },
            Hreflang = new HreflangSettings { Enabled = fixture.Settings.HreflangEnabled },
        };

        var pages = new List<IPublishedContent>(fixture.Pages.Count);
        var pageByKey = new Dictionary<string, IPublishedContent>();

        foreach (var pageFixture in fixture.Pages)
        {
            var page = StubPage(pageFixture, urlProvider, extractor, fixture.Culture);
            pages.Add(page);
            pageByKey[pageFixture.Key] = page;
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>? hreflang = null;
        if (fixture.Settings.HreflangEnabled
            && fixture.Variants is { Count: > 0 })
        {
            var variantMap = new Dictionary<Guid, IReadOnlyList<HreflangVariant>>(fixture.Variants.Count);
            foreach (var (pageKey, vlist) in fixture.Variants)
            {
                if (!pageByKey.TryGetValue(pageKey, out var page))
                {
                    continue;
                }
                var variants = vlist.Select(v =>
                {
                    var trimmed = v.RelativeUrl.TrimEnd();
                    var withSuffix = trimmed.EndsWith('/')
                        ? trimmed + "index.html.md"
                        : trimmed + LlmsTxt.Umbraco.Constants.Routes.MarkdownSuffix;
                    return new HreflangVariant(v.Culture.ToLowerInvariant(), withSuffix);
                }).ToList();
                variantMap[page.Key] = variants;
            }
            hreflang = variantMap;
        }

        var root = pages[0];

        return new ManifestFixtureBuilder(
            urlProvider, extractor, settings, root, pages, hreflang,
            fixture.Hostname, fixture.Culture);
    }

    private static IPublishedContent StubPage(
        ManifestPageFixture fixture,
        IPublishedUrlProvider urlProvider,
        IMarkdownContentExtractor extractor,
        string culture)
    {
        var content = Substitute.For<IPublishedContent>();
        content.Key.Returns(Guid.Parse(fixture.Key));
        content.Name.Returns(fixture.Name);
        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns(fixture.ContentTypeAlias);
        content.ContentType.Returns(contentType);

        if (fixture.UpdateDate is not null)
        {
            content.UpdateDate.Returns(DateTime.Parse(fixture.UpdateDate, System.Globalization.CultureInfo.InvariantCulture));
        }

        urlProvider
            .GetUrl(content, UrlMode.Relative, Arg.Any<string?>(), Arg.Any<Uri?>())
            .Returns(fixture.RelativeUrl);
        urlProvider
            .GetUrl(content, UrlMode.Absolute, Arg.Any<string?>(), Arg.Any<Uri?>())
            .Returns(fixture.AbsoluteUrl ?? string.Empty);

        // Extractor returns a fixture-defined Markdown body; frontmatter is
        // synthesised so the builder's strip-frontmatter path is exercised.
        var frontmatterBody = $"---\ntitle: {fixture.Name}\nurl: {fixture.RelativeUrl}\n---\n\n{fixture.Body}\n";
        var result = MarkdownExtractionResult.Found(
            markdown: frontmatterBody,
            contentKey: content.Key,
            culture: culture,
            updatedUtc: DateTime.UtcNow,
            sourceUrl: fixture.RelativeUrl);
        extractor
            .ExtractAsync(content, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

        return content;
    }
}
