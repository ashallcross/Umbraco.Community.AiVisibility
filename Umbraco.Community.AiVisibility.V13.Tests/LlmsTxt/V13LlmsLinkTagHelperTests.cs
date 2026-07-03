using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Web.Common.Routing;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.LlmsTxt;

namespace Umbraco.Community.AiVisibility.V13.Tests.LlmsTxt;

/// <summary>
/// Proves the <c>&lt;llms-link /&gt;</c> TagHelper emits the discoverability markup
/// (<c>&lt;link rel="alternate" type="text/markdown" href="…md"&gt;</c>) when compiled
/// into the v13 shell assembly, and suppresses cleanly when the page is excluded. The
/// TagHelper source is compile-linked from the shared shell, so this pins the markup
/// contract on net8 without a live boot — the robust counterpart to the HTTP
/// integration smoke (which asserts the automatic Link response header).
///
/// <para>
/// Pure unit test (no Umbraco boot); runs on the net10 host. Density ceiling: the
/// full happy/suppression matrix is already pinned against the shared source on net10,
/// so this mirrors only the happy path + one suppression branch to prove TFM stability.
/// </para>
/// </summary>
[TestFixture]
public sealed class V13LlmsLinkTagHelperTests
{
    [Test]
    public async Task ProcessAsync_HappyPath_EmitsLinkAlternateMarkdown()
    {
        var helper = new LlmsLinkTagHelper(
            UrlProviderReturning("/home"),
            NotExcluded(),
            NullLogger<LlmsLinkTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage()),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.Multiple(() =>
        {
            Assert.That(output.TagName, Is.EqualTo("link"));
            Assert.That(output.TagMode, Is.EqualTo(TagMode.SelfClosing));
            Assert.That(output.Attributes["rel"]?.Value?.ToString(), Is.EqualTo("alternate"));
            Assert.That(output.Attributes["type"]?.Value?.ToString(), Is.EqualTo("text/markdown"));
            Assert.That(output.Attributes["href"]?.Value?.ToString(), Is.EqualTo("/home.md"));
        });
    }

    [Test]
    public async Task ProcessAsync_PageExcluded_SuppressesOutput()
    {
        var excluded = Substitute.For<IExclusionEvaluator>();
        excluded.IsExcludedAsync(
            Arg.Any<IPublishedContent>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var helper = new LlmsLinkTagHelper(
            UrlProviderReturning("/home"),
            excluded,
            NullLogger<LlmsLinkTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage()),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.TagName, Is.Null, "an excluded page suppresses the alternate markup");
    }

    // ---- helpers (mirrored from the v17 LlmsLinkTagHelperTests) ----

    private static IPublishedContent StubPage(string doctypeAlias = "homePage")
    {
        var content = Substitute.For<IPublishedContent>();
        var ct = Substitute.For<IPublishedContentType>();
        ct.Alias.Returns(doctypeAlias);
        content.ContentType.Returns(ct);
        content.Key.Returns(Guid.NewGuid());
        return content;
    }

    private static ViewContext BuildViewContext(IPublishedContent? routedContent, string path = "/home")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("example.com");
        http.Request.Path = path;

        if (routedContent is not null)
        {
            var publishedRequest = Substitute.For<IPublishedRequest>();
            publishedRequest.PublishedContent.Returns(routedContent);
            publishedRequest.Culture.Returns("en-gb");
            var routeValues = new UmbracoRouteValues(publishedRequest, controllerActionDescriptor: null!);
            http.Features.Set(routeValues);
        }

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ViewContext(
            actionContext,
            Substitute.For<Microsoft.AspNetCore.Mvc.ViewEngines.IView>(),
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            Substitute.For<ITempDataDictionary>(),
            TextWriter.Null,
            new HtmlHelperOptions());
    }

    private static (TagHelperContext ctx, TagHelperOutput output) BuildOutput()
    {
        var ctx = new TagHelperContext(
            new TagHelperAttributeList(),
            new Dictionary<object, object>(),
            uniqueId: Guid.NewGuid().ToString());
        var output = new TagHelperOutput(
            "llms-link",
            new TagHelperAttributeList(),
            (useCachedResult, encoder) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        return (ctx, output);
    }

    private static IPublishedUrlProvider UrlProviderReturning(string url)
    {
        var provider = Substitute.For<IPublishedUrlProvider>();
        provider
            .GetUrl(Arg.Any<IPublishedContent>(), Arg.Any<UrlMode>(), Arg.Any<string?>(), Arg.Any<Uri?>())
            .Returns(url);
        return provider;
    }

    private static IExclusionEvaluator NotExcluded()
    {
        var evaluator = Substitute.For<IExclusionEvaluator>();
        evaluator.IsExcludedAsync(
            Arg.Any<IPublishedContent>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        return evaluator;
    }
}
