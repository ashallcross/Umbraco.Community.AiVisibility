using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.LlmsTxt;
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
using NSubstitute.ExceptionExtensions;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Web.Common.Routing;

namespace Umbraco.Community.AiVisibility.Tests.LlmsTxt;

/// <summary>
/// Story 4.1 AC5 — pins the &lt;llms-link /&gt; gating shape: in-Umbraco-context
/// happy path emits &lt;link rel="alternate"&gt;; out-of-context / excluded /
/// URL-provider failure modes suppress the output.
/// </summary>
[TestFixture]
public class LlmsLinkTagHelperTests
{
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
    public async Task ProcessAsync_TrailingSlashUrl_EmitsIndexHtmlMd()
    {
        var helper = new LlmsLinkTagHelper(
            UrlProviderReturning("/blog/"),
            NotExcluded(),
            NullLogger<LlmsLinkTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage(), path: "/blog/"),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.Attributes["href"]?.Value?.ToString(), Is.EqualTo("/blog/index.html.md"));
    }

    [Test]
    public async Task ProcessAsync_NoUmbracoRouteValues_SuppressesOutput()
    {
        var helper = new LlmsLinkTagHelper(
            UrlProviderReturning("/home"),
            NotExcluded(),
            NullLogger<LlmsLinkTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(routedContent: null), // no UmbracoRouteValues
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.TagName, Is.Null);
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

        Assert.That(output.TagName, Is.Null);
    }

    [Test]
    public async Task ProcessAsync_UrlProviderThrows_SuppressesOutput()
    {
        var provider = Substitute.For<IPublishedUrlProvider>();
        provider.GetUrl(Arg.Any<IPublishedContent>(), Arg.Any<UrlMode>(), Arg.Any<string?>(), Arg.Any<Uri?>())
            .Throws(new InvalidOperationException("provider glitch"));
        var helper = new LlmsLinkTagHelper(provider, NotExcluded(), NullLogger<LlmsLinkTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage()),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.TagName, Is.Null);
    }

    [Test]
    public async Task ProcessAsync_UrlProviderReturnsHash_SuppressesOutput()
    {
        var helper = new LlmsLinkTagHelper(
            UrlProviderReturning("#"),
            NotExcluded(),
            NullLogger<LlmsLinkTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage()),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.TagName, Is.Null);
    }

    /// <summary>
    /// P-E patch — AC5 enumerates "feature null OR PublishedContent null" as
    /// separate gating conditions. This pins the second branch: UmbracoRouteValues
    /// is set on the request but PublishedContent comes back null (e.g. a
    /// surface-controller view rendered against a non-content URL).
    /// </summary>
    [Test]
    public async Task ProcessAsync_PublishedContentNull_SuppressesOutput()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("example.com");
        http.Request.Path = "/home";
        var publishedRequest = Substitute.For<IPublishedRequest>();
        publishedRequest.PublishedContent.Returns((IPublishedContent?)null);
        publishedRequest.Culture.Returns("en-gb");
        var routeValues = new UmbracoRouteValues(publishedRequest, controllerActionDescriptor: null!);
        http.Features.Set(routeValues);

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var viewContext = new ViewContext(
            actionContext,
            Substitute.For<Microsoft.AspNetCore.Mvc.ViewEngines.IView>(),
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            Substitute.For<ITempDataDictionary>(),
            TextWriter.Null,
            new HtmlHelperOptions());

        var helper = new LlmsLinkTagHelper(
            UrlProviderReturning("/home"),
            NotExcluded(),
            NullLogger<LlmsLinkTagHelper>.Instance)
        {
            ViewContext = viewContext,
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.TagName, Is.Null, "TagName must be null when PublishedContent is null");
        Assert.That(output.Content.GetContent(), Is.Empty, "Output content must be empty on suppression");
    }

    /// <summary>
    /// P-F patch — pins idempotent integration: when the URL provider returns
    /// a path already ending in `.md`, the rendered href stays `.md` once
    /// (MarkdownAlternateUrl.Append is idempotent at the helper level too).
    /// </summary>
    [Test]
    public async Task ProcessAsync_AlreadyMdSuffixUrl_PassesThrough()
    {
        var helper = new LlmsLinkTagHelper(
            UrlProviderReturning("/page.md"),
            NotExcluded(),
            NullLogger<LlmsLinkTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage(), path: "/page.md"),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.Attributes["href"]?.Value?.ToString(), Is.EqualTo("/page.md"),
            "/page.md must NOT become /page.md.md — Append is idempotent");
    }

    /// <summary>
    /// P-G patch — strengthen suppression assertion. TagName Is.Null alone
    /// matches both "SuppressOutput called" and "TagName never set" — neither
    /// of which is conclusive on a partial-write regression. Adding
    /// IsContentModified Is.False catches the case where attributes were
    /// applied before a code-path bail.
    /// </summary>
    [Test]
    public async Task ProcessAsync_PageExcluded_SuppressesOutputCleanly()
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

        Assert.Multiple(() =>
        {
            Assert.That(output.TagName, Is.Null);
            Assert.That(output.Content.GetContent(), Is.Empty, "No partial body content should be present after suppression");
            Assert.That(output.Attributes.ContainsName("rel"), Is.False);
            Assert.That(output.Attributes.ContainsName("href"), Is.False);
        });
    }
}
