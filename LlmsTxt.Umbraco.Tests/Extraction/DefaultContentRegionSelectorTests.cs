using AngleSharp;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmsTxt.Umbraco.Tests.Extraction;

[TestFixture]
public class DefaultContentRegionSelectorTests
{
    private readonly DefaultContentRegionSelector _selector = new(NullLogger<DefaultContentRegionSelector>.Instance);

    [Test]
    public async Task SelectRegion_DataLlmsContent_WinsOverMainAndArticle()
    {
        var doc = await ParseAsync(
            "<html><body>" +
            "<main><p>main content</p></main>" +
            "<article><p>article content</p></article>" +
            "<section data-llms-content><p>marked content</p></section>" +
            "</body></html>");

        var region = _selector.SelectRegion(doc, Array.Empty<string>());

        Assert.That(region, Is.Not.Null);
        Assert.That(region!.TextContent.Trim(), Is.EqualTo("marked content"));
    }

    [Test]
    public async Task SelectRegion_MainWinsOverArticle_WhenNoDataLlmsContent()
    {
        var doc = await ParseAsync(
            "<html><body>" +
            "<main><p>main content</p></main>" +
            "<article><p>article content</p></article>" +
            "</body></html>");

        var region = _selector.SelectRegion(doc, Array.Empty<string>());

        Assert.That(region, Is.Not.Null);
        Assert.That(region!.TagName.ToLowerInvariant(), Is.EqualTo("main"));
    }

    [Test]
    public async Task SelectRegion_ArticleWins_WhenNoDataLlmsContentNoMain()
    {
        var doc = await ParseAsync(
            "<html><body>" +
            "<article><p>article content</p></article>" +
            "</body></html>");

        var region = _selector.SelectRegion(doc, Array.Empty<string>());

        Assert.That(region, Is.Not.Null);
        Assert.That(region!.TagName.ToLowerInvariant(), Is.EqualTo("article"));
    }

    [Test]
    public async Task SelectRegion_ConfiguredSelector_UsedAfterBuiltInChainExhausted()
    {
        var doc = await ParseAsync(
            "<html><body>" +
            "<div class=\"page-body\"><p>configured target</p></div>" +
            "</body></html>");

        var region = _selector.SelectRegion(doc, new[] { "div.page-body" });

        Assert.That(region, Is.Not.Null);
        Assert.That(region!.TextContent.Trim(), Is.EqualTo("configured target"));
    }

    [Test]
    public async Task SelectRegion_NoMatch_ReturnsNullForSmartReaderFallback()
    {
        var doc = await ParseAsync(
            "<html><body>" +
            "<div><p>nothing semantic</p></div>" +
            "</body></html>");

        var region = _selector.SelectRegion(doc, Array.Empty<string>());

        Assert.That(region, Is.Null);
    }

    [Test]
    public async Task SelectRegion_MalformedConfiguredSelector_DoesNotThrow()
    {
        var doc = await ParseAsync("<html><body><div><p>x</p></div></body></html>");

        // ":::not a selector:::" is invalid CSS — must not bubble out of the selector.
        var region = _selector.SelectRegion(doc, new[] { ":::not a selector:::" });

        Assert.That(region, Is.Null);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html)
    {
        var ctx = BrowsingContext.New(AngleSharp.Configuration.Default);
        return await ctx.OpenAsync(req => req.Content(html));
    }
}
