using LlmsTxt.Umbraco.Builders;
using Umbraco.Community.AiVisibility.Caching;
using Umbraco.Community.AiVisibility.Configuration;
using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace LlmsTxt.Umbraco.Tests.Builders;

[TestFixture]
public class DefaultLlmsFullBuilderTests
{
    private const string Host = "sitea.example";
    private const string Culture = "en-gb";

    private IPublishedUrlProvider _publishedUrlProvider = null!;
    private IMarkdownContentExtractor _extractor = null!;
    private ILogger<DefaultLlmsFullBuilder> _logger = null!;

    [SetUp]
    public void Setup()
    {
        _publishedUrlProvider = Substitute.For<IPublishedUrlProvider>();
        _extractor = Substitute.For<IMarkdownContentExtractor>();
        _logger = NullLogger<DefaultLlmsFullBuilder>.Instance;
    }

    private DefaultLlmsFullBuilder MakeBuilder()
        => new(_publishedUrlProvider, _extractor, _logger);

    // ────────────────────────────────────────────────────────────────────────
    // AC1 — body shape
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_DefaultShape_EmitsHeaderSourceAndBodyPerPage()
    {
        var a = StubPage("Page A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        var b = StubPage("Page B", "contentPage", "/b", absolute: "https://sitea.example/b", relativeUrl: "/b");
        StubExtractorReturnsBody(a, body: "Body of A.");
        StubExtractorReturnsBody(b, body: "Body of B.");
        var ctx = MakeContext(new[] { a, b });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.StartWith("# Page A\n\n_Source: https://sitea.example/a_\n\nBody of A."),
                "first page emits H1 + _Source: + body");
            Assert.That(manifest, Does.Contain("\n\n---\n\n# Page B\n\n_Source: https://sitea.example/b_\n\nBody of B."),
                "second page is preceded by separator and has the same shape");
        });
    }

    [Test]
    public async Task BuildAsync_FrontmatterStrippedFromEachPage()
    {
        var a = StubPage("A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        StubExtractorReturnsBody(a, body: "# Real Body Heading\n\nBody text.");
        var ctx = MakeContext(new[] { a });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Not.Contain("---\ntitle:"),
                "per-page YAML frontmatter is stripped before concatenation");
            Assert.That(manifest, Does.Contain("# Real Body Heading"),
                "extraction body content is preserved verbatim after stripping frontmatter");
        });
    }

    [Test]
    public async Task BuildAsync_TitleWithMarkdownSpecials_Escaped()
    {
        var a = StubPage("Foo [bar] (baz) `code`", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        StubExtractorReturnsBody(a, body: "Body.");
        var ctx = MakeContext(new[] { a });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.StartWith("# Foo \\[bar\\] \\(baz\\) \\`code\\`\n"),
            "ATX heading text escapes [, ], (, ), backslash, and backtick per CommonMark");
    }

    [Test]
    public async Task BuildAsync_TitleWithControlChars_ReplacedWithSpaces()
    {
        // A page Name carrying a stray newline (or tab, or other control char)
        // would split the surrounding ATX heading and orphan the rest as a
        // paragraph. EscapeMarkdownLinkText replaces control chars with a single
        // space so the H1 stays a single line.
        var a = StubPage("Foo\nBar\tBaz", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        StubExtractorReturnsBody(a, body: "Body.");
        var ctx = MakeContext(new[] { a });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.StartWith("# Foo Bar Baz\n"),
                "control chars (\\n, \\t) replaced with spaces — H1 stays a single line");
            Assert.That(manifest, Does.Not.Contain("# Foo\nBar"),
                "no orphaned paragraph from a heading split by an embedded newline");
        });
    }

    [Test]
    public async Task BuildAsync_NoHostnameAndNoAbsoluteUrl_EmitsAboutBlankPlaceholder()
    {
        // When IPublishedUrlProvider returns no absolute URL AND the context carries
        // no hostname, the fallback emits about:blank rather than poisoning the
        // off-site dump with an unreachable https://localhost/... link.
        var a = StubPage("A", "contentPage", "/a", absolute: null, relativeUrl: "/a");
        StubExtractorReturnsBody(a, body: "Body.");
        var ctx = new LlmsFullBuilderContext(
            Hostname: AiVisibilityCacheKeys.NormaliseHost(null),
            Culture: Culture,
            RootContent: a,
            Pages: new[] { a },
            Settings: new AiVisibilitySettings().ToResolved());

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("_Source: about:blank_"),
                "about:blank placeholder used when no usable URL is available");
            Assert.That(manifest, Does.Not.Contain("https://localhost/"),
                "must not poison the off-site dump with localhost links");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // AC4 — ordering
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Story 3.3 AC3 — zero-config (no `LlmsTxt:` section in appsettings,
    /// no Settings doctype edits) emits each page in tree-order with no
    /// truncation footer (the 5 MB default cap is far above the test fixtures'
    /// few hundred bytes). Pins the contract together: TreeOrder default +
    /// 5120 KB cap. The default values themselves are pinned by
    /// <c>LlmsTxtSettingsDefaultsTests</c>; this test pins the builder's
    /// behaviour when those defaults are in effect.
    /// </summary>
    [Test]
    public async Task BuildAsync_NoSettingsAtAll_EmitsAllPagesInTreeOrder_NoTruncation()
    {
        var a = StubPage("Page A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        var b = StubPage("Page B", "contentPage", "/b", absolute: "https://sitea.example/b", relativeUrl: "/b");
        var c = StubPage("Page C", "contentPage", "/c", absolute: "https://sitea.example/c", relativeUrl: "/c");
        StubExtractorReturnsBody(a, "Body of A.");
        StubExtractorReturnsBody(b, "Body of B.");
        StubExtractorReturnsBody(c, "Body of C.");
        // MakeContext uses the in-code default AiVisibilitySettings — no override
        var ctx = MakeContext(new[] { a, b, c });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(IndexOf(manifest, "# Page A"), Is.LessThan(IndexOf(manifest, "# Page B")),
                "tree-order is the default — A before B");
            Assert.That(IndexOf(manifest, "# Page B"), Is.LessThan(IndexOf(manifest, "# Page C")),
                "tree-order is the default — B before C");
            Assert.That(manifest, Does.Not.Contain("_Truncated:"),
                "no truncation footer when body fits in the 5 MB default cap");
            Assert.That(manifest, Does.Contain("Body of A."));
            Assert.That(manifest, Does.Contain("Body of B."));
            Assert.That(manifest, Does.Contain("Body of C."));
        });
    }

    [Test]
    public async Task BuildAsync_TreeOrder_PreservesControllerOrder()
    {
        var a = StubPage("Apple", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        var b = StubPage("Cherry", "contentPage", "/b", absolute: "https://sitea.example/b", relativeUrl: "/b");
        var c = StubPage("Banana", "contentPage", "/c", absolute: "https://sitea.example/c", relativeUrl: "/c");
        StubExtractorReturnsBody(a, "A");
        StubExtractorReturnsBody(b, "B");
        StubExtractorReturnsBody(c, "C");
        var ctx = MakeContext(new[] { a, b, c }); // tree-order is default

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(IndexOf(manifest, "# Apple"), Is.LessThan(IndexOf(manifest, "# Cherry")));
        Assert.That(IndexOf(manifest, "# Cherry"), Is.LessThan(IndexOf(manifest, "# Banana")),
            "tree-order preserves controller-supplied order regardless of titles");
    }

    [Test]
    public async Task BuildAsync_Alphabetical_SortsCaseInsensitive()
    {
        var banana = StubPage("banana", "contentPage", "/banana", absolute: "https://sitea.example/banana", relativeUrl: "/banana");
        var apple = StubPage("Apple", "contentPage", "/apple", absolute: "https://sitea.example/apple", relativeUrl: "/apple");
        var cherry = StubPage("cherry", "contentPage", "/cherry", absolute: "https://sitea.example/cherry", relativeUrl: "/cherry");
        StubExtractorReturnsBody(banana, "x");
        StubExtractorReturnsBody(apple, "x");
        StubExtractorReturnsBody(cherry, "x");
        var settings = new AiVisibilitySettings { LlmsFullBuilder = new LlmsFullBuilderSettings { Order = LlmsFullOrder.Alphabetical } };
        var ctx = MakeContext(new[] { banana, apple, cherry }, settings);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(IndexOf(manifest, "# Apple"), Is.LessThan(IndexOf(manifest, "# banana")));
        Assert.That(IndexOf(manifest, "# banana"), Is.LessThan(IndexOf(manifest, "# cherry")),
            "alphabetical sort is case-insensitive ascending");
    }

    [Test]
    public async Task BuildAsync_RecentFirst_SortsByUpdateDateDescending()
    {
        var older = StubPageWithUpdateDate("Older", "/older", new DateTime(2026, 1, 1));
        var newest = StubPageWithUpdateDate("Newest", "/newest", new DateTime(2026, 4, 1));
        var middle = StubPageWithUpdateDate("Middle", "/middle", new DateTime(2026, 2, 15));
        StubExtractorReturnsBody(older, "x");
        StubExtractorReturnsBody(newest, "x");
        StubExtractorReturnsBody(middle, "x");
        var settings = new AiVisibilitySettings { LlmsFullBuilder = new LlmsFullBuilderSettings { Order = LlmsFullOrder.RecentFirst } };
        var ctx = MakeContext(new[] { older, newest, middle }, settings);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(IndexOf(manifest, "# Newest"), Is.LessThan(IndexOf(manifest, "# Middle")));
        Assert.That(IndexOf(manifest, "# Middle"), Is.LessThan(IndexOf(manifest, "# Older")),
            "recent-first puts newer UpdateDate ahead of older");
    }

    [Test]
    public async Task BuildAsync_RecentFirst_TiesBrokenByTreeIndex()
    {
        var sameDate = new DateTime(2026, 3, 1);
        var first = StubPageWithUpdateDate("First", "/first", sameDate);
        var second = StubPageWithUpdateDate("Second", "/second", sameDate);
        StubExtractorReturnsBody(first, "x");
        StubExtractorReturnsBody(second, "x");
        var settings = new AiVisibilitySettings { LlmsFullBuilder = new LlmsFullBuilderSettings { Order = LlmsFullOrder.RecentFirst } };
        var ctx = MakeContext(new[] { first, second }, settings);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(IndexOf(manifest, "# First"), Is.LessThan(IndexOf(manifest, "# Second")),
            "ties on UpdateDate broken by original (tree) index");
    }

    // ────────────────────────────────────────────────────────────────────────
    // _Source: URL — absolute + fallback
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_AbsoluteUrlInSourceLine()
    {
        var a = StubPage("A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        StubExtractorReturnsBody(a, "x");
        var ctx = MakeContext(new[] { a });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.Contain("_Source: https://sitea.example/a_"),
            "absolute URL from IPublishedUrlProvider lands in _Source: line");
    }

    [Test]
    public async Task BuildAsync_AbsoluteUrlNullOrEmpty_FallsBackToHostnamePrefix()
    {
        var a = StubPage("A", "contentPage", "/a", absolute: null, relativeUrl: "/a");
        StubExtractorReturnsBody(a, "x");
        var ctx = MakeContext(new[] { a });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.Contain("_Source: https://sitea.example/a_"),
            "fallback constructs https://{Hostname}{relativeUrl} when Absolute returns null");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Extraction errors
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_ExtractionThrows_EmitsCommentAndContinues()
    {
        var a = StubPage("A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        var b = StubPage("B", "contentPage", "/b", absolute: "https://sitea.example/b", relativeUrl: "/b");
        var c = StubPage("C", "contentPage", "/c", absolute: "https://sitea.example/c", relativeUrl: "/c");
        StubExtractorReturnsBody(a, "Body A");
        _extractor
            .ExtractAsync(b, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<MarkdownExtractionResult>>(_ => throw new InvalidOperationException("boom"));
        StubExtractorReturnsBody(c, "Body C");
        var ctx = MakeContext(new[] { a, b, c });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("# A"), "page A emitted normally");
            Assert.That(manifest, Does.Contain("<!-- LlmsTxt: skipped B due to extraction error -->"),
                "page B's extraction failure produces a placeholder comment in B's slot");
            Assert.That(manifest, Does.Contain("# C"), "page C emitted normally after the failure");
            Assert.That(IndexOf(manifest, "# A"), Is.LessThan(IndexOf(manifest, "skipped B")));
            Assert.That(IndexOf(manifest, "skipped B"), Is.LessThan(IndexOf(manifest, "# C")));
        });
    }

    [Test]
    public void BuildAsync_OperationCanceledException_Rethrown()
    {
        var a = StubPage("A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        _extractor
            .ExtractAsync(a, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<MarkdownExtractionResult>>(_ => throw new OperationCanceledException());
        var ctx = MakeContext(new[] { a });

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await MakeBuilder().BuildAsync(ctx, CancellationToken.None));
    }

    [Test]
    public async Task BuildAsync_ExtractionStatusError_EmitsCommentPlaceholder()
    {
        var a = StubPage("A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        var aKey = a.Key;
        var failed = MarkdownExtractionResult.Failed(
            error: new InvalidOperationException(),
            sourceUrl: "/a",
            contentKey: aKey);
        _extractor
            .ExtractAsync(a, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(failed));
        var ctx = MakeContext(new[] { a });

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Does.Contain("<!-- LlmsTxt: skipped A due to extraction error -->"),
            "Error status (without exception) produces the same skip placeholder");
    }

    // ────────────────────────────────────────────────────────────────────────
    // AC5 — size cap
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_CapHit_TruncatesAndAppendsFooter()
    {
        // Cap = 1 KB. Two pages, each ~700 bytes when rendered.
        // Expect: page 1 fits, page 2 doesn't, footer says "Showing 1 of 2 pages."
        var a = StubPage("A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        var b = StubPage("B", "contentPage", "/b", absolute: "https://sitea.example/b", relativeUrl: "/b");
        var bigBody = new string('x', 600);
        StubExtractorReturnsBody(a, bigBody);
        StubExtractorReturnsBody(b, bigBody);
        var settings = new AiVisibilitySettings { MaxLlmsFullSizeKb = 1 };
        var ctx = MakeContext(new[] { a, b }, settings);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("# A"), "page A fits and is emitted");
            Assert.That(manifest, Does.Not.Contain("# B"), "page B exceeds the cap and is skipped");
            Assert.That(manifest, Does.Contain("_Truncated: site exceeds the configured 1 KB cap. Showing 1 of 2 pages._"),
                "truncation footer documents how many pages emitted of the total");
        });
    }

    [Test]
    public async Task BuildAsync_CapNonPositive_TreatedAsUnlimited()
    {
        var a = StubPage("A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        var b = StubPage("B", "contentPage", "/b", absolute: "https://sitea.example/b", relativeUrl: "/b");
        StubExtractorReturnsBody(a, new string('x', 5000));
        StubExtractorReturnsBody(b, new string('x', 5000));
        var settings = new AiVisibilitySettings { MaxLlmsFullSizeKb = 0 };
        var ctx = MakeContext(new[] { a, b }, settings);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.Contain("# A"));
            Assert.That(manifest, Does.Contain("# B"), "with cap=0 (unlimited fallback), all pages emit");
            Assert.That(manifest, Does.Not.Contain("_Truncated:"), "no truncation footer when cap is unlimited");
        });
    }

    [Test]
    public async Task BuildAsync_SinglePageExceedsCap_EmitsTruncatedPageWithMarker()
    {
        // Cap = 1 KB but a single page weighs ~5 KB. Expected behaviour: emit the
        // page truncated mid-content + inline marker. The standard site-level
        // footer is suppressed on this branch — the inline marker is the truncation
        // signal here, and a standard "Showing 0 of 1 pages._" footer alongside
        // would emit two consecutive _Truncated:_ blocks with conflicting
        // pagesEmitted semantics.
        var a = StubPage("A", "contentPage", "/a", absolute: "https://sitea.example/a", relativeUrl: "/a");
        StubExtractorReturnsBody(a, new string('y', 5000));
        var settings = new AiVisibilitySettings { MaxLlmsFullSizeKb = 1 };
        var ctx = MakeContext(new[] { a }, settings);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest, Does.StartWith("# A"), "first page emits even though it busts the cap");
            Assert.That(manifest, Does.Contain("_Truncated: page content exceeds the 1 KB cap._"),
                "inline marker after the cut documents the page-level truncation");
            Assert.That(manifest, Does.Not.Contain("_Truncated: site exceeds"),
                "standard site-level footer suppressed on the single-page-overflow branch");
            Assert.That(manifest, Does.Not.Contain("Showing 0 of 1 pages._"),
                "no zero-pages-emitted footer when content WAS emitted (truncated)");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // Empty input
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_EmptyPagesList_EmitsEmptyBody()
    {
        var root = StubPage("X", "contentPage", "/x", absolute: "https://sitea.example/x", relativeUrl: "/x");
        var ctx = MakeContext(Array.Empty<IPublishedContent>(), root: root);

        var manifest = await MakeBuilder().BuildAsync(ctx, CancellationToken.None);

        Assert.That(manifest, Is.EqualTo(string.Empty),
            "empty scope-filtered list produces empty body (caller decides 200-vs-404)");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static int IndexOf(string haystack, string needle) => haystack.IndexOf(needle, StringComparison.Ordinal);

    private LlmsFullBuilderContext MakeContext(
        IReadOnlyList<IPublishedContent> pages,
        AiVisibilitySettings? settings = null,
        IPublishedContent? root = null)
    {
        var rootOrFirst = root
                          ?? (pages.Count > 0
                              ? pages[0]
                              : StubPage("Root", "homePage", "/", absolute: "https://sitea.example/", relativeUrl: "/"));
        return new LlmsFullBuilderContext(
            Hostname: Host,
            Culture: Culture,
            RootContent: rootOrFirst,
            Pages: pages,
            Settings: (settings ?? new AiVisibilitySettings()).ToResolved());
    }

    private IPublishedContent StubPage(
        string? name,
        string contentTypeAlias,
        string identityForKey,
        string? absolute,
        string? relativeUrl)
    {
        var content = Substitute.For<IPublishedContent>();
        content.Key.Returns(DeterministicGuid(identityForKey));
        content.Name.Returns(name);
        content.UpdateDate.Returns(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns(contentTypeAlias);
        content.ContentType.Returns(contentType);
        content.GetProperty(Arg.Any<string>()).Returns((IPublishedProperty?)null);

        _publishedUrlProvider
            .GetUrl(content, UrlMode.Absolute, Arg.Any<string?>(), Arg.Any<Uri?>())
            .Returns(absolute);
        _publishedUrlProvider
            .GetUrl(content, UrlMode.Relative, Arg.Any<string?>(), Arg.Any<Uri?>())
            .Returns(relativeUrl);
        return content;
    }

    private IPublishedContent StubPageWithUpdateDate(
        string name,
        string identityForKey,
        DateTime updateDate)
    {
        var content = StubPage(name, "contentPage", identityForKey, absolute: $"https://sitea.example{identityForKey}", relativeUrl: identityForKey);
        content.UpdateDate.Returns(updateDate);
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
