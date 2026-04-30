using LlmsTxt.Umbraco.Builders;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace LlmsTxt.Umbraco.Tests.Builders;

[TestFixture]
public class DefaultLlmsTxtBuilderTests
{
    private const string Host = "sitea.example";
    private const string Culture = "en-gb";

    private IPublishedUrlProvider _publishedUrlProvider = null!;
    private IPublishedValueFallback _publishedValueFallback = null!;
    private IMarkdownContentExtractor _extractor = null!;
    private ILogger<DefaultLlmsTxtBuilder> _logger = null!;

    [SetUp]
    public void Setup()
    {
        _publishedUrlProvider = Substitute.For<IPublishedUrlProvider>();
        _publishedValueFallback = Substitute.For<IPublishedValueFallback>();
        _extractor = Substitute.For<IMarkdownContentExtractor>();
        _logger = NullLogger<DefaultLlmsTxtBuilder>.Instance;
    }

    private DefaultLlmsTxtBuilder MakeBuilder()
        => new(_publishedUrlProvider, _publishedValueFallback, _extractor, _logger);

    // ────────────────────────────────────────────────────────────────────────
    // AC1 — body shape
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_DefaultCase_EmitsH1BlockquoteAndPagesSection()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        var about = StubPage("About", "contentPage", "/about", relativeUrl: "/about");
        StubExtractorReturnsBody(about, body: "About body content for the summary fallback.");
        var ctx = MakeContext(root, new[] { root, about });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.StartWith("# Acme\n"), "H1 from root.Name when SiteName is null");
            Assert.That(manifest, Does.Contain("\n> \n"), "blockquote present even when SiteSummary is null");
            Assert.That(manifest, Does.Contain("\n## Pages\n"), "default Pages section emitted");
            Assert.That(manifest, Does.Contain("- [Acme](/index.html.md)"), "trailing-slash root produces /index.html.md");
            Assert.That(manifest, Does.Contain("- [About](/about.md)"), "non-root pages produce /{path}.md");
        });
    }

    [Test]
    public async Task BuildAsync_SiteNameAndSummaryFromSettings_OverridesDefaults()
    {
        var root = StubPage("RootName", "homePage", "/", relativeUrl: "/");
        var settings = new LlmsTxtSettings
        {
            SiteName = "Acme Docs",
            SiteSummary = "Acme product documentation",
        };
        var ctx = MakeContext(root, new[] { root }, settings);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.StartWith("# Acme Docs\n"));
            Assert.That(manifest, Does.Contain("> Acme product documentation\n"));
        });
    }

    [Test]
    public async Task BuildAsync_RootNameMissing_DefaultsToSiteLiteral()
    {
        var root = StubPage(name: null, "homePage", "/", relativeUrl: "/");
        var ctx = MakeContext(root, new[] { root });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.StartWith("# Site\n"));
    }

    [Test]
    public async Task EmptySite_EmitsHeaderOnly()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        // Pages list is empty — controller passed in zero pages (fallback root not yielded by snapshot).
        var ctx = MakeContext(root, Array.Empty<IPublishedContent>());

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.StartWith("# Acme\n"));
            Assert.That(manifest, Does.Contain("> \n"));
            Assert.That(manifest, Does.Not.Contain("##"), "no H2 sections when no pages");
            Assert.That(manifest, Does.Not.Contain("- ["), "no bulleted links when no pages");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // AC4 — section grouping
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_SectionGrouping_RespectsConfiguredOrder()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        var article = StubPage("Article 1", "article", "/articles/1", relativeUrl: "/articles/1");
        var doc = StubPage("Doc 1", "docPage", "/docs/1", relativeUrl: "/docs/1");
        var settings = new LlmsTxtSettings
        {
            LlmsTxtBuilder = new LlmsTxtBuilderSettings
            {
                SectionGrouping = new[]
                {
                    new SectionGroupingEntry { Title = "Articles", DocTypeAliases = new[] { "article" } },
                    new SectionGroupingEntry { Title = "Documentation", DocTypeAliases = new[] { "docPage" } },
                },
            },
        };
        var ctx = MakeContext(root, new[] { root, article, doc }, settings);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        var articlesIdx = manifest.IndexOf("## Articles", StringComparison.Ordinal);
        var docsIdx = manifest.IndexOf("## Documentation", StringComparison.Ordinal);
        var pagesIdx = manifest.IndexOf("## Pages", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(articlesIdx, Is.GreaterThan(0), "Articles section emitted");
            Assert.That(docsIdx, Is.GreaterThan(articlesIdx), "Documentation section follows Articles");
            Assert.That(pagesIdx, Is.GreaterThan(docsIdx), "default Pages section is last");
            Assert.That(manifest, Does.Contain("- [Article 1](/articles/1.md)"));
            Assert.That(manifest, Does.Contain("- [Doc 1](/docs/1.md)"));
            Assert.That(manifest, Does.Contain("- [Acme](/index.html.md)"));
        });
    }

    [Test]
    public async Task SectionGrouping_UnknownDocType_OmitsSection()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        var article = StubPage("Article 1", "article", "/articles/1", relativeUrl: "/articles/1");
        var settings = new LlmsTxtSettings
        {
            LlmsTxtBuilder = new LlmsTxtBuilderSettings
            {
                SectionGrouping = new[]
                {
                    new SectionGroupingEntry { Title = "Articles", DocTypeAliases = new[] { "article" } },
                    new SectionGroupingEntry { Title = "Phantom", DocTypeAliases = new[] { "nonExistentDoc" } },
                },
            },
        };
        var ctx = MakeContext(root, new[] { root, article }, settings);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("## Articles"));
            Assert.That(manifest, Does.Not.Contain("## Phantom"), "section with no matching pages must be omitted");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // AC5 — page summary resolution
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_PageSummary_FromMetaDescriptionWhenPropertyHasValue()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        var about = StubPageWithMetaDescription(
            "About",
            "contentPage",
            "/about",
            relativeUrl: "/about",
            metaDescription: "Our story in one sentence.");
        var ctx = MakeContext(root, new[] { root, about });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.Contain("- [About](/about.md): Our story in one sentence."));
        // Body extractor should NOT be invoked when the property fills in.
        await _extractor.DidNotReceive().ExtractAsync(about, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BuildAsync_PageSummary_BodyFallbackWhenMetaDescriptionMissing()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        var about = StubPage("About", "contentPage", "/about", relativeUrl: "/about");
        StubExtractorReturnsBody(about, body: "Short body content.");
        var ctx = MakeContext(root, new[] { root, about });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.Contain("- [About](/about.md): Short body content."));
    }

    [Test]
    public async Task BuildAsync_PageSummary_BodyFallbackTruncatesAtWordBoundaryWithEllipsis()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        var article = StubPage("Article", "contentPage", "/article", relativeUrl: "/article");
        // 200-char body, all single-character words separated by spaces — easy to count.
        var body = string.Join(" ", Enumerable.Repeat("word", 50));
        StubExtractorReturnsBody(article, body: body);
        var ctx = MakeContext(root, new[] { root, article });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        var linkLine = manifest.Split('\n')
            .First(l => l.StartsWith("- [Article]", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(linkLine, Does.EndWith("…"), "ellipsis appended on truncation");
            // Extract just the summary portion after ": "
            var summaryStart = linkLine.IndexOf("): ", StringComparison.Ordinal) + 3;
            var summary = linkLine[summaryStart..];
            // Strip the trailing ellipsis for the word-boundary check.
            var beforeEllipsis = summary[..^1];
            Assert.That(beforeEllipsis, Does.Not.EndWith("wor"), "word-boundary truncation, no mid-word cut");
            Assert.That(beforeEllipsis, Does.EndWith("word"));
            Assert.That(summary.Length, Is.LessThanOrEqualTo(161), "summary <= 160 chars + ellipsis");
        });
    }

    [Test]
    public async Task SummaryFallback_ExtractorThrows_LinkEmittedWithoutSummary()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        var about = StubPage("About", "contentPage", "/about", relativeUrl: "/about");
        _extractor
            .ExtractAsync(about, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<MarkdownExtractionResult>>(_ => throw new InvalidOperationException("boom"));
        var ctx = MakeContext(root, new[] { root, about });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("- [About](/about.md)\n"), "link emitted without ': summary'");
            Assert.That(manifest, Does.Not.Contain("- [About](/about.md):"), "no trailing summary marker on this line");
        });
    }

    [Test]
    public async Task BuildAsync_PageSummary_ExtractorErrorResult_LinkEmittedWithoutSummary()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        var about = StubPage("About", "contentPage", "/about", relativeUrl: "/about");
        var aboutKey = about.Key;
        var failedResult = MarkdownExtractionResult.Failed(
            new InvalidOperationException("render fail"),
            sourceUrl: "/about",
            contentKey: aboutKey);
        _extractor
            .ExtractAsync(about, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(failedResult));
        var ctx = MakeContext(root, new[] { root, about });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.Contain("- [About](/about.md)\n"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Markdown link-text escaping (CommonMark § 6.6)
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Title_WithMarkdownSpecials_Escaped()
    {
        var root = StubPage("Foo [bar] (baz) \\qux", "homePage", "/", relativeUrl: "/");
        var ctx = MakeContext(root, new[] { root });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain(@"# Foo [bar] (baz) \qux"), "H1 keeps title verbatim (Markdown headings don't need escaping)");
            Assert.That(manifest, Does.Contain(@"- [Foo \[bar\] \(baz\) \\qux](/index.html.md)"), "link text escapes [, ], (, ), \\");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // URL skipping defensive paths
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PageWithoutUrl_SkippedFromManifest()
    {
        var root = StubPage("Acme", "homePage", "/", relativeUrl: "/");
        var orphan = StubPage("Orphan", "contentPage", "/orphan", relativeUrl: null);
        var ctx = MakeContext(root, new[] { root, orphan });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.Not.Contain("Orphan"), "page with no URL is dropped");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 2.3 — hreflang variant suffixes
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_HreflangVariantsNull_BodyMatchesStory21Output()
    {
        // Build with null and with empty separately and compare to a baseline
        // capture taken with null variants. Both must be byte-equal.
        var root = StubPage("Acme", "homePage", "root", relativeUrl: "/");
        var about = StubPage("About", "contentPage", "about", relativeUrl: "/about");
        StubExtractorReturnsBody(about, "About body content");
        var ctx = MakeContext(root, new[] { root, about }, hreflangVariants: null);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.Not.Match(@"\([a-z]{2}-[a-z]{2}:"),
            "no `(culture: ...)` variant suffix when hreflang is null");
    }

    [Test]
    public async Task BuildAsync_HreflangVariantsEmpty_BodyMatchesStory21Output()
    {
        var root = StubPage("Acme", "homePage", "root", relativeUrl: "/");
        var about = StubPage("About", "contentPage", "about", relativeUrl: "/about");
        StubExtractorReturnsBody(about, "About body content");

        // Reference: same setup with null variants.
        var ctxNull = MakeContext(root, new[] { root, about }, hreflangVariants: null);
        var baseline = await MakeBuilder().BuildAsync(ctxNull, CancellationToken.None);

        // Now with an empty dictionary — body must be byte-identical.
        var empty = new Dictionary<Guid, IReadOnlyList<HreflangVariant>>(0);
        var ctxEmpty = MakeContext(root, new[] { root, about }, hreflangVariants: empty);
        var withEmpty = await MakeBuilder().BuildAsync(ctxEmpty, CancellationToken.None);

        Assert.That(withEmpty, Is.EqualTo(baseline),
            "empty dictionary is treated identically to null — byte-equal output");
    }

    [Test]
    public async Task BuildAsync_HreflangVariantsForOnePage_AppendedAfterSummary()
    {
        var root = StubPage("Acme", "homePage", "root", relativeUrl: "/");
        var about = StubPage("About", "contentPage", "about", relativeUrl: "/about");
        StubExtractorReturnsBody(about, "About body content");
        var variants = new Dictionary<Guid, IReadOnlyList<HreflangVariant>>
        {
            [about.Key] = new[]
            {
                new HreflangVariant("nl-nl", "/nl/about.md"),
                new HreflangVariant("fr-fr", "/fr/about.md"),
            },
        };
        var ctx = MakeContext(root, new[] { root, about }, hreflangVariants: variants);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.Contain(
            "- [About](/about.md): About body content (fr-fr: /fr/about.md) (nl-nl: /nl/about.md)"),
            "variants appended after summary, in BCP-47-lexicographic culture order");
    }

    [Test]
    public async Task BuildAsync_HreflangVariantsLexicographicOrder()
    {
        var root = StubPage("Acme", "homePage", "root", relativeUrl: "/");
        StubExtractorReturnsBody(root, "Body");
        var variants = new Dictionary<Guid, IReadOnlyList<HreflangVariant>>
        {
            [root.Key] = new[]
            {
                new HreflangVariant("nl-nl", "/nl/index.html.md"),
                new HreflangVariant("fr-fr", "/fr/index.html.md"),
                new HreflangVariant("de-de", "/de/index.html.md"),
            },
        };
        var ctx = MakeContext(root, new[] { root }, hreflangVariants: variants);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        var deIdx = manifest.IndexOf("(de-de:", StringComparison.Ordinal);
        var frIdx = manifest.IndexOf("(fr-fr:", StringComparison.Ordinal);
        var nlIdx = manifest.IndexOf("(nl-nl:", StringComparison.Ordinal);

        Assert.That(deIdx, Is.LessThan(frIdx));
        Assert.That(frIdx, Is.LessThan(nlIdx));
    }

    [Test]
    public async Task BuildAsync_HreflangVariantsForSomePagesOnly_OthersHaveNoSuffix()
    {
        var root = StubPage("Acme", "homePage", "root", relativeUrl: "/");
        var about = StubPage("About", "contentPage", "about", relativeUrl: "/about");
        var contact = StubPage("Contact", "contentPage", "contact", relativeUrl: "/contact");
        StubExtractorReturnsBody(about, "About");
        StubExtractorReturnsBody(contact, "Contact");
        var variants = new Dictionary<Guid, IReadOnlyList<HreflangVariant>>
        {
            [about.Key] = new[] { new HreflangVariant("fr-fr", "/fr/about.md") },
        };
        var ctx = MakeContext(root, new[] { root, about, contact }, hreflangVariants: variants);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("(fr-fr: /fr/about.md)"),
                "About's variant emitted");
            // Contact's link line ends without a variant suffix — assert by
            // looking at the trailing newline immediately after Contact's URL.
            Assert.That(manifest, Does.Contain("- [Contact](/contact.md): Contact\n"),
                "Contact has no variant suffix — line ends after summary");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private LlmsTxtBuilderContext MakeContext(
        IPublishedContent root,
        IReadOnlyList<IPublishedContent> pages,
        LlmsTxtSettings? settings = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>? hreflangVariants = null)
        => new(
            Hostname: Host,
            Culture: Culture,
            RootContent: root,
            Pages: pages,
            Settings: settings ?? new LlmsTxtSettings(),
            HreflangVariants: hreflangVariants);

    private IPublishedContent StubPage(
        string? name,
        string contentTypeAlias,
        string identityForKey,
        string? relativeUrl)
    {
        var content = Substitute.For<IPublishedContent>();
        content.Key.Returns(DeterministicGuid(identityForKey));
        content.Name.Returns(name);
        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns(contentTypeAlias);
        content.ContentType.Returns(contentType);
        // GetProperty returns null by default — triggers body fallback.
        content.GetProperty(Arg.Any<string>()).Returns((IPublishedProperty?)null);

        _publishedUrlProvider
            .GetUrl(content, Arg.Any<UrlMode>(), Arg.Any<string?>(), Arg.Any<Uri?>())
            .Returns(relativeUrl);
        return content;
    }

    private IPublishedContent StubPageWithMetaDescription(
        string name,
        string contentTypeAlias,
        string identityForKey,
        string? relativeUrl,
        string metaDescription)
    {
        var content = StubPage(name, contentTypeAlias, identityForKey, relativeUrl);
        var prop = Substitute.For<IPublishedProperty>();
        prop.HasValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns(true);
        prop.GetValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns(metaDescription);
        content.GetProperty("metaDescription").Returns(prop);
        return content;
    }

    private void StubExtractorReturnsBody(IPublishedContent page, string body)
    {
        var result = MarkdownExtractionResult.Found(
            markdown: $"---\ntitle: {page.Name}\nurl: /\nupdated: 2026-01-01T00:00:00Z\n---\n\n{body}\n",
            contentKey: page.Key,
            culture: Culture,
            updatedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            sourceUrl: "/");
        _extractor
            .ExtractAsync(page, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
    }

    private static Guid DeterministicGuid(string seed)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return new Guid(bytes.AsSpan(0, 16).ToArray());
    }
}
