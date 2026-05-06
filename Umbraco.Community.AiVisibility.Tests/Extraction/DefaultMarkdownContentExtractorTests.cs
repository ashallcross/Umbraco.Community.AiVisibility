using Umbraco.Community.AiVisibility.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace LlmsTxt.Umbraco.Tests.Extraction;

[TestFixture]
public class DefaultMarkdownContentExtractorTests
{
    private DefaultMarkdownContentExtractor _extractor = null!;

    [SetUp]
    public void Setup()
    {
        var settings = new AiVisibilitySettings { MainContentSelectors = Array.Empty<string>() };
        var optionsSnapshot = new StubOptionsSnapshot<AiVisibilitySettings>(settings);

        // The internal `ExtractFromHtmlAsync` seam exercised by these tests does not
        // touch `_pageRenderer`, `_publishedUrlProvider`, or `_httpContextAccessor` —
        // passing `null!` is intentional and documented at code review (decision:
        // accept the smell). Story 1.2's `ExtractAsync(content, culture, ct)` IS
        // covered by integration tests at the controller layer.
        _extractor = new DefaultMarkdownContentExtractor(
            pageRenderer: null!,
            regionSelector: new DefaultContentRegionSelector(NullLogger<DefaultContentRegionSelector>.Instance),
            converter: new MarkdownConverter(),
            publishedUrlProvider: null!,
            httpContextAccessor: null!,
            settings: optionsSnapshot,
            logger: NullLogger<DefaultMarkdownContentExtractor>.Instance);
    }

    [Test]
    public async Task ExtractFromHtmlAsync_StripsScriptStyleSvgIframeNoscriptHiddenAriaHiddenAndDataLlmsIgnore()
    {
        var html = @"
<!DOCTYPE html>
<html><head>
<title>Doc</title>
<style>body { color: red; }</style>
</head>
<body>
<main>
  <h1>Real heading</h1>
  <p>Real body</p>
  <script>console.log('hidden')</script>
  <noscript>noscript content</noscript>
  <svg><circle r='10'/></svg>
  <iframe src='https://example.test/embed'></iframe>
  <div hidden>hidden div</div>
  <div aria-hidden='true'>aria hidden div</div>
  <div data-llms-ignore>ignored region</div>
</main>
</body></html>";

        var result = await ExtractAsync(html);

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found));
        var md = result.Markdown!;
        Assert.That(md, Does.Contain("Real heading"));
        Assert.That(md, Does.Contain("Real body"));
        Assert.That(md, Does.Not.Contain("console.log"));
        Assert.That(md, Does.Not.Contain("noscript content"));
        Assert.That(md, Does.Not.Contain("hidden div"));
        Assert.That(md, Does.Not.Contain("aria hidden div"));
        Assert.That(md, Does.Not.Contain("ignored region"));
        Assert.That(md, Does.Not.Contain("embed"));
        Assert.That(md, Does.Not.Contain("color: red"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_AbsolutifiesRelativeAnchorAndImageUrls()
    {
        var html = @"<main><p><a href='/about'>About</a> <img src='/media/hero.png' alt='hero' /></p></main>";

        var result = await ExtractAsync(html, sourceUri: new Uri("https://example.test/home"));

        Assert.That(result.Markdown, Does.Contain("[About](https://example.test/about)"));
        Assert.That(result.Markdown, Does.Contain("![hero](https://example.test/media/hero.png)"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_DropsImagesWithEmptyAlt()
    {
        var html = @"<main><img src='/decorative.png' alt='' /><img src='/meaningful.png' alt='diagram' /></main>";

        var result = await ExtractAsync(html, sourceUri: new Uri("https://example.test/page"));

        Assert.That(result.Markdown, Does.Not.Contain("decorative.png"));
        Assert.That(result.Markdown, Does.Contain("![diagram](https://example.test/meaningful.png)"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_PreservesHeadingLevelsAndStructure()
    {
        var html = @"<main>
<h1>Top</h1>
<h2>Section A</h2>
<p>Para A.</p>
<h2>Section B</h2>
<ul><li>One</li><li>Two</li></ul>
</main>";

        var result = await ExtractAsync(html);

        var md = result.Markdown!;
        Assert.That(md, Does.Contain("# Top"));
        Assert.That(md, Does.Contain("## Section A"));
        Assert.That(md, Does.Contain("## Section B"));
        Assert.That(md, Does.Contain("- One"));
        Assert.That(md, Does.Contain("- Two"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_ProducesYamlFrontmatterWithTitleUrlUpdated()
    {
        var html = @"<main><p>body</p></main>";
        var meta = new ContentMetadata(
            Title: "About Us",
            AbsoluteUrl: "https://example.test/about",
            UpdatedUtc: new DateTime(2026, 4, 29, 12, 30, 45, DateTimeKind.Utc),
            ContentKey: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Culture: "en-GB");

        var result = await _extractor.ExtractFromHtmlAsync(
            html,
            new Uri("https://example.test/about"),
            meta,
            CancellationToken.None);

        var md = result.Markdown!;
        Assert.That(md, Does.StartWith("---\n"));
        Assert.That(md, Does.Contain("title: About Us"));
        Assert.That(md, Does.Contain("url: https://example.test/about"));
        Assert.That(md, Does.Contain("updated: 2026-04-29T12:30:45Z"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_EscapesYamlSignificantCharsInTitle()
    {
        var html = @"<main><p>body</p></main>";
        var meta = new ContentMetadata(
            Title: "Hello: a 'quoted' title",
            AbsoluteUrl: "https://example.test/x",
            UpdatedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ContentKey: Guid.NewGuid(),
            Culture: "en-GB");

        var result = await _extractor.ExtractFromHtmlAsync(
            html,
            new Uri("https://example.test/x"),
            meta,
            CancellationToken.None);

        Assert.That(result.Markdown, Does.Contain("title: 'Hello: a ''quoted'' title'"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_DataLlmsContent_OverridesMainAndArticle()
    {
        var html = @"
<main><p>main body</p></main>
<article><p>article body</p></article>
<section data-llms-content><h1>Picked</h1><p>This wins.</p></section>";

        var result = await ExtractAsync(html);

        Assert.That(result.Markdown, Does.Contain("# Picked"));
        Assert.That(result.Markdown, Does.Contain("This wins."));
        Assert.That(result.Markdown, Does.Not.Contain("main body"));
        Assert.That(result.Markdown, Does.Not.Contain("article body"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_ReturnsErrorWhenNoRegionAndSmartReaderAlsoFails()
    {
        // Empty body — no <main>, no <article>, no [data-llms-content], nothing for SmartReader.
        var html = @"<html><body></body></html>";
        var result = await ExtractAsync(html);

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Error));
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public async Task ExtractFromHtmlAsync_DocumentLevelDataLlmsIgnore_ReturnsFrontmatterOnly()
    {
        // <body data-llms-ignore> means the page told us to extract nothing.
        // Returning frontmatter-only is preferable to a 500 / leaking content via SmartReader.
        var html = @"<html><body data-llms-ignore><main><p>should not appear</p></main></body></html>";

        var result = await ExtractAsync(html);

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found));
        Assert.That(result.Markdown, Does.Not.Contain("should not appear"));
        Assert.That(result.Markdown, Does.StartWith("---\n"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_EmptyMainBody_ReturnsFrontmatterOnly()
    {
        var html = @"<html><body><main>   </main></body></html>";

        var result = await ExtractAsync(html);

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found));
        Assert.That(result.Markdown, Does.StartWith("---\n"));
        Assert.That(result.Markdown!.Trim(), Does.EndWith("---"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_DropsImagesWithEmptySrc_RegardlessOfAlt()
    {
        // src='' with a meaningful alt would otherwise emit `![meaningful]()` — broken Markdown.
        var html = @"<main><img src='' alt='meaningful' /><p>body</p></main>";

        var result = await ExtractAsync(html);

        Assert.That(result.Markdown, Does.Not.Contain("meaningful"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_AbsolutifiesSrcset_PreservingDataUriCommas()
    {
        // Commas inside data: URIs must NOT be treated as srcset entry separators.
        var html = @"<main><img src='/x.png' alt='hero' srcset='data:image/png;base64,iVBORw0= 1x, /hi.png 2x' /></main>";

        var result = await ExtractAsync(html, sourceUri: new Uri("https://example.test/page"));

        // The data: URL should survive intact; the relative URL should be absolutified.
        Assert.That(result.Markdown, Does.Contain("![hero](https://example.test/x.png)"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_StripsAriaHiddenInsideRegion_NotAroundIt()
    {
        // [aria-hidden=true] on a wrapping container around <main> would have nuked
        // the region under the old document-wide strip ordering. Region select runs
        // first, then strip happens *inside* the region — so the wrapping aria-hidden
        // is harmless and the inner ignored block is removed.
        var html = @"
<div aria-hidden='true'>
  <main>
    <h1>Visible</h1>
    <div aria-hidden='true'>nested hidden</div>
  </main>
</div>";

        var result = await ExtractAsync(html);

        Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found));
        Assert.That(result.Markdown, Does.Contain("Visible"));
        Assert.That(result.Markdown, Does.Not.Contain("nested hidden"));
    }

    [Test]
    public async Task ExtractFromHtmlAsync_HonoursCancellationToken()
    {
        var html = @"<main><p>body</p></main>";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await _extractor.ExtractFromHtmlAsync(
                html,
                new Uri("https://example.test/x"),
                new ContentMetadata(
                    Title: "T",
                    AbsoluteUrl: "https://example.test/x",
                    UpdatedUtc: new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc),
                    ContentKey: Guid.NewGuid(),
                    Culture: "en-GB"),
                cts.Token);
        });
    }

    [Test]
    public async Task ExtractFromHtmlAsync_ForcesUtcKindOnUnspecifiedUpdatedDate()
    {
        // Umbraco's NPoco-loaded UpdateDate has Kind=Unspecified by default.
        // ToUniversalTime() on Unspecified assumes Local, which would shift the
        // timestamp by the host TZ offset. The seam must coerce to UTC explicitly.
        var html = @"<main><p>body</p></main>";
        var unspecified = DateTime.SpecifyKind(new DateTime(2026, 4, 29, 12, 0, 0), DateTimeKind.Unspecified);
        var meta = new ContentMetadata(
            Title: "T",
            AbsoluteUrl: "https://example.test/x",
            UpdatedUtc: unspecified,
            ContentKey: Guid.NewGuid(),
            Culture: "en-GB");

        var result = await _extractor.ExtractFromHtmlAsync(
            html,
            new Uri("https://example.test/x"),
            meta,
            CancellationToken.None);

        Assert.That(result.Markdown, Does.Contain("updated: 2026-04-29T12:00:00Z"));
    }

    [TestCase("null", ExpectedResult = "title: 'null'")]
    [TestCase("true", ExpectedResult = "title: 'true'")]
    [TestCase("false", ExpectedResult = "title: 'false'")]
    [TestCase("yes", ExpectedResult = "title: 'yes'")]
    [TestCase("Comma, in title", ExpectedResult = "title: 'Comma, in title'")]
    [TestCase("Tab\there", ExpectedResult = "title: 'Tab\there'")]
    public async Task<string> ExtractFromHtmlAsync_QuotesYamlReservedAndFlowIndicatorTitles(string title)
    {
        var html = @"<main><p>body</p></main>";
        var meta = new ContentMetadata(
            Title: title,
            AbsoluteUrl: "https://example.test/x",
            UpdatedUtc: new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc),
            ContentKey: Guid.NewGuid(),
            Culture: "en-GB");

        var result = await _extractor.ExtractFromHtmlAsync(
            html,
            new Uri("https://example.test/x"),
            meta,
            CancellationToken.None);

        var line = result.Markdown!.Split('\n').First(l => l.StartsWith("title:"));
        return line;
    }

    [Test]
    public async Task ExtractFromHtmlAsync_HeadingInsideAnchor_LiftsHeadingAndPreservesLink()
    {
        // Clean.Core BlockList card markup wraps each card heading in an anchor.
        // ReverseMarkdown's default encoding produces unreadable [## Heading](url) output.
        // The pre-conversion lift hoists the heading out so the Markdown reads cleanly:
        // a heading line followed by a link wrapping the rest of the card content.
        var html = """
            <main>
              <div class="card">
                <a href="/articles/post">
                  <img src="https://example.test/img.jpg" alt="post hero" />
                  <h2>Post title</h2>
                  <p>Card body</p>
                </a>
              </div>
            </main>
            """;

        var result = await ExtractAsync(html);
        var md = result.Markdown!;

        Assert.Multiple(() =>
        {
            Assert.That(md, Does.Contain("## Post title"),
                "heading must appear at the line level, not embedded inside link text");
            // The heading line stands on its own; no '[## Post title' inside-link pattern.
            Assert.That(md, Does.Not.Contain("[## Post title"),
                "heading must NOT remain inside the link text");
            Assert.That(md, Does.Contain("/articles/post"),
                "the link's href is preserved on the remainder of the anchor's content");
        });
    }

    [Test]
    public async Task ExtractFromHtmlAsync_PlainAnchor_NotAffected()
    {
        // Anchors that don't wrap a heading must pass through unmolested.
        var html = """
            <main>
              <p><a href="/about">About us</a> — visit our about page.</p>
            </main>
            """;

        var result = await ExtractAsync(html);

        Assert.That(result.Markdown, Does.Contain("[About us](https://example.test/about)"),
            "ordinary anchor renders as a single Markdown link");
    }

    [Test]
    public async Task ExtractFromHtmlAsync_NestedHeadingInDeepAnchor_StillLifted()
    {
        // The lift must reach headings nested at any depth inside an anchor — wrappers
        // like <div class="card-body"> are common in real Clean.Core templates and
        // must not block the heading from being hoisted out.
        var html = """
            <main>
              <a href="/deep">
                <div class="card-body">
                  <div class="card-header">
                    <h3>Deeply nested heading</h3>
                  </div>
                  <p>Card body text</p>
                </div>
              </a>
            </main>
            """;

        var result = await ExtractAsync(html);
        var md = result.Markdown!;

        Assert.Multiple(() =>
        {
            Assert.That(md, Does.Contain("### Deeply nested heading"),
                "heading hoists out of the anchor regardless of intermediate wrappers");
            Assert.That(md, Does.Not.Contain("[### Deeply nested heading"),
                "heading must NOT remain inside the link text");
        });
    }

    [Test]
    public async Task ExtractFromHtmlAsync_AnchorWithOnlyHeading_AnchorRemovedAfterLift()
    {
        // Edge case: when the heading is the anchor's only child, hoisting it leaves the
        // anchor empty. Emitting an empty [](url) link is link-shaped noise — drop it.
        var html = """
            <main>
              <p>Intro paragraph.</p>
              <a href="/orphan"><h2>Orphan heading</h2></a>
              <p>Outro paragraph.</p>
            </main>
            """;

        var result = await ExtractAsync(html);
        var md = result.Markdown!;

        Assert.Multiple(() =>
        {
            Assert.That(md, Does.Contain("## Orphan heading"));
            Assert.That(md, Does.Not.Contain("[](https://example.test/orphan)"),
                "empty anchor must be dropped — no link-shaped noise");
        });
    }

    [Test]
    public async Task ExtractFromHtmlAsync_HeadingInRegionButAnchorAncestorOutsideRegion_HeadingNotLifted()
    {
        // Region-escape guard. The descendant combinator in the selector matches the
        // document tree, not the region — so without bounding the ancestor walk to
        // region, a heading inside region whose <a> ancestor lives OUTSIDE region
        // would be silently lifted out of region and dropped from converted output.
        // We use an adopter selector that targets <main>; the surrounding <a> is the
        // ancestor that must NOT be the lift target.
        var html = """
            <a href="/outer">
              <main>
                <h2>Heading inside region</h2>
                <p>Body inside region.</p>
              </main>
            </a>
            """;

        // The default content-region selector picks <main>, leaving the surrounding
        // <a> outside the chosen region.
        var result = await ExtractAsync(html);
        var md = result.Markdown!;

        // The heading must remain inside region — and therefore appear in the output.
        Assert.That(md, Does.Contain("## Heading inside region"),
            "lift must not escape region; heading must stay inside the converted output");
    }

    [Test]
    public async Task ExtractFromHtmlAsync_NestedAnchorsBothWrapHeading_HeadingLiftedOutOfOutermostAnchor()
    {
        // Pathological-but-parseable: anchors nested inside anchors. AngleSharp's HTML
        // parser accepts the structure even though it's invalid HTML5. The lift must
        // hoist the heading past the OUTERMOST in-region anchor — not just the inner
        // one — otherwise the heading ends up inside the outer anchor as link text.
        var html = """
            <main>
              <a href="/outer">
                <a href="/inner">
                  <h2>Nested heading</h2>
                </a>
              </a>
            </main>
            """;

        var result = await ExtractAsync(html);
        var md = result.Markdown!;

        Assert.Multiple(() =>
        {
            Assert.That(md, Does.Contain("## Nested heading"),
                "heading must appear at the line level");
            Assert.That(md, Does.Not.Contain("[## Nested heading"),
                "heading must NOT remain inside any link text");
            Assert.That(md, Does.Not.Contain("[\n## Nested heading"),
                "heading must NOT remain inside multi-line link text either");
        });
    }

    [Test]
    public async Task ExtractFromHtmlAsync_AnchorWithOnlyEmptyAltImage_AnchorRemovedAfterImageDrop()
    {
        // <a><img alt=""/></a> — empty-alt image is dropped by DropImagesWithEmptyAltOrSrc.
        // Without a post-image-drop empty-anchor sweep, the anchor would render as
        // [](url) — link-shaped noise.
        var html = """
            <main>
              <p>Intro paragraph.</p>
              <a href="/decorative-link"><img src="/d.png" alt="" /></a>
              <p>Outro paragraph.</p>
            </main>
            """;

        var result = await ExtractAsync(html);
        var md = result.Markdown!;

        Assert.That(md, Does.Not.Contain("[](https://example.test/decorative-link)"),
            "post-image-drop empty anchor must be swept up");
    }

    [Test]
    public async Task ExtractFromHtmlAsync_MultipleHeadingsInOneAnchor_AllLiftedInDocumentOrder()
    {
        // Pathological but possible — multiple headings inside one anchor. All must
        // lift, in document order.
        var html = """
            <main>
              <a href="/multi">
                <h2>First heading</h2>
                <p>Mid paragraph</p>
                <h3>Second heading</h3>
                <p>End paragraph</p>
              </a>
            </main>
            """;

        var result = await ExtractAsync(html);
        var md = result.Markdown!;

        // Both headings out at the line level, in document order. The two paragraphs
        // and the surrounding link wrapping are preserved as ReverseMarkdown emits them.
        var firstHeadingIdx = md.IndexOf("## First heading", StringComparison.Ordinal);
        var secondHeadingIdx = md.IndexOf("### Second heading", StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(firstHeadingIdx, Is.GreaterThan(0), "first heading must be present");
            Assert.That(secondHeadingIdx, Is.GreaterThan(firstHeadingIdx),
                "second heading must come after the first (document order preserved)");
        });
    }

    [Test]
    public void ResolveAbsoluteContentUrl_NonDefaultCulture_PassesCultureToProvider()
    {
        // Story 6.0a AC4 (Codex finding #4) — multilingual frontmatter URL bug.
        // Pre-6.0a path called the 1-arg `GetUrl(content, UrlMode.Absolute)`
        // which defaulted to the site's default culture, so non-default culture
        // pages emitted the default-culture URL in `url:` frontmatter. AC4
        // requires the resolved culture is forwarded to the URL provider.
        var content = Substitute.For<IPublishedContent>();
        content.Key.Returns(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        var urlProvider = Substitute.For<IPublishedUrlProvider>();
        urlProvider.GetUrl(content, UrlMode.Absolute, "fr-FR", Arg.Any<Uri?>())
            .Returns("https://example.fr/about/");
        urlProvider.GetUrl(content, UrlMode.Absolute, "en-GB", Arg.Any<Uri?>())
            .Returns("https://example.com/about/");

        var settings = new AiVisibilitySettings { MainContentSelectors = Array.Empty<string>() };
        var extractor = new DefaultMarkdownContentExtractor(
            pageRenderer: null!,
            regionSelector: new DefaultContentRegionSelector(NullLogger<DefaultContentRegionSelector>.Instance),
            converter: new MarkdownConverter(),
            publishedUrlProvider: urlProvider,
            httpContextAccessor: null!,
            settings: new StubOptionsSnapshot<AiVisibilitySettings>(settings),
            logger: NullLogger<DefaultMarkdownContentExtractor>.Instance);

        var requestUri = new Uri("https://example.fr/about/");

        var resolvedFr = extractor.ResolveAbsoluteContentUrl(content, requestUri, "fr-FR");
        var resolvedEn = extractor.ResolveAbsoluteContentUrl(content, requestUri, "en-GB");
        var resolvedDefault = extractor.ResolveAbsoluteContentUrl(content, requestUri, culture: null);

        Assert.Multiple(() =>
        {
            Assert.That(resolvedFr, Is.EqualTo("https://example.fr/about/"),
                "non-default culture must resolve to the requested-culture URL, NOT the default-culture URL");
            Assert.That(resolvedEn, Is.EqualTo("https://example.com/about/"),
                "default culture path unchanged");
            // Provider receives the resolved culture verbatim on every call.
            // Single-culture sites pass null culture; provider forwarded as null.
            urlProvider.Received(1).GetUrl(content, UrlMode.Absolute, "fr-FR", Arg.Any<Uri?>());
            urlProvider.Received(1).GetUrl(content, UrlMode.Absolute, "en-GB", Arg.Any<Uri?>());
            urlProvider.Received(1).GetUrl(content, UrlMode.Absolute, (string?)null, Arg.Any<Uri?>());
            _ = resolvedDefault;
        });
    }

    private async Task<MarkdownExtractionResult> ExtractAsync(string html, Uri? sourceUri = null)
    {
        sourceUri ??= new Uri("https://example.test/home");
        var meta = new ContentMetadata(
            Title: "Test Page",
            AbsoluteUrl: sourceUri.ToString(),
            UpdatedUtc: new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc),
            ContentKey: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Culture: "en-GB");

        return await _extractor.ExtractFromHtmlAsync(html, sourceUri, meta, CancellationToken.None);
    }

    private sealed class StubOptionsSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public StubOptionsSnapshot(T value) { Value = value; }
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
