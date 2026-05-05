using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.TagHelpers;
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

namespace LlmsTxt.Umbraco.Tests.TagHelpers;

/// <summary>
/// Story 4.1 AC6 — pins the &lt;llms-hint /&gt; gating + emission shape.
/// Visually-hidden via CSS class hook (NOT inline style); body anchor is
/// relative; same gating as &lt;llms-link /&gt;.
/// </summary>
[TestFixture]
public class LlmsHintTagHelperTests
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
            "llms-hint",
            new TagHelperAttributeList(),
            (useCachedResult, encoder) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        return (ctx, output);
    }

    private static IPublishedUrlProvider UrlProviderReturning(string url)
    {
        var provider = Substitute.For<IPublishedUrlProvider>();
        provider.GetUrl(Arg.Any<IPublishedContent>(), Arg.Any<UrlMode>(), Arg.Any<string?>(), Arg.Any<Uri?>())
            .Returns(url);
        return provider;
    }

    private static ILlmsExclusionEvaluator NotExcluded()
    {
        var evaluator = Substitute.For<ILlmsExclusionEvaluator>();
        evaluator.IsExcludedAsync(
            Arg.Any<IPublishedContent>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        return evaluator;
    }

    [Test]
    public async Task ProcessAsync_HappyPath_EmitsVisuallyHiddenDiv_WithRelativeMdAnchor()
    {
        var helper = new LlmsHintTagHelper(
            UrlProviderReturning("/home"),
            NotExcluded(),
            NullLogger<LlmsHintTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage()),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.Multiple(() =>
        {
            Assert.That(output.TagName, Is.EqualTo("div"));
            Assert.That(output.TagMode, Is.EqualTo(TagMode.StartTagAndEndTag));
            Assert.That(output.Attributes["class"]?.Value?.ToString(), Is.EqualTo("llms-hint"),
                "must use class hook (no inline styles — CSP-safe)");
            Assert.That(output.Attributes["role"]?.Value?.ToString(), Is.EqualTo("note"));
            // No inline style attribute leaked.
            Assert.That(output.Attributes.ContainsName("style"), Is.False,
                "no inline style — strict CSP `style-src 'self'` adopters must not be affected");
            var content = output.Content.GetContent();
            Assert.That(content, Does.Contain("/home.md"),
                "body anchor must point at the relative .md URL");
            // P-D patch — pin AC6's exact body shape, not just attribute substrings.
            // Without these, a regression that emits rel/type/href on the <div>
            // directly (no <a>) or drops the lead-in copy would silently pass.
            Assert.That(content, Does.Contain("<a "),
                "AC6 — body must contain an <a> element wrapping the URL");
            Assert.That(content, Does.Contain("This page is also available as Markdown at"),
                "AC6 — body lead-in copy must be present verbatim");
            Assert.That(content, Does.Contain("rel=\"alternate\""));
            Assert.That(content, Does.Contain("type=\"text/markdown\""));
            Assert.That(content, Does.Not.Contain("https://example.com"),
                "anchor must be relative (Story 4.1 DoD bullet 3)");
        });
    }

    /// <summary>
    /// P-E patch — pins the second AC6 gating branch: UmbracoRouteValues set
    /// but PublishedContent is null. Mirrors LlmsLinkTagHelperTests.
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

        var helper = new LlmsHintTagHelper(
            UrlProviderReturning("/home"),
            NotExcluded(),
            NullLogger<LlmsHintTagHelper>.Instance)
        {
            ViewContext = viewContext,
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.TagName, Is.Null);
        Assert.That(output.Content.GetContent(), Is.Empty);
    }

    /// <summary>
    /// P-G patch — strengthen suppression assertion: tag null AND content
    /// not modified AND no class/role attributes leaked.
    /// </summary>
    [Test]
    public async Task ProcessAsync_PageExcluded_SuppressesOutputCleanly()
    {
        var excluded = Substitute.For<ILlmsExclusionEvaluator>();
        excluded.IsExcludedAsync(
            Arg.Any<IPublishedContent>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var helper = new LlmsHintTagHelper(
            UrlProviderReturning("/home"),
            excluded,
            NullLogger<LlmsHintTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage()),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.Multiple(() =>
        {
            Assert.That(output.TagName, Is.Null);
            Assert.That(output.Content.GetContent(), Is.Empty);
            Assert.That(output.Attributes.ContainsName("class"), Is.False);
            Assert.That(output.Attributes.ContainsName("role"), Is.False);
        });
    }

    [Test]
    public async Task ProcessAsync_TrailingSlashUrl_EmitsIndexHtmlMdAnchor()
    {
        var helper = new LlmsHintTagHelper(
            UrlProviderReturning("/blog/"),
            NotExcluded(),
            NullLogger<LlmsHintTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage(), path: "/blog/"),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.Content.GetContent(), Does.Contain("/blog/index.html.md"));
    }

    [Test]
    public async Task ProcessAsync_NoUmbracoRouteValues_SuppressesOutput()
    {
        var helper = new LlmsHintTagHelper(
            UrlProviderReturning("/home"),
            NotExcluded(),
            NullLogger<LlmsHintTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(routedContent: null),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.TagName, Is.Null);
    }

    [Test]
    public async Task ProcessAsync_PageExcluded_SuppressesOutput()
    {
        var excluded = Substitute.For<ILlmsExclusionEvaluator>();
        excluded.IsExcludedAsync(
            Arg.Any<IPublishedContent>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var helper = new LlmsHintTagHelper(
            UrlProviderReturning("/home"),
            excluded,
            NullLogger<LlmsHintTagHelper>.Instance)
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
        var helper = new LlmsHintTagHelper(provider, NotExcluded(), NullLogger<LlmsHintTagHelper>.Instance)
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
        var helper = new LlmsHintTagHelper(
            UrlProviderReturning("#"),
            NotExcluded(),
            NullLogger<LlmsHintTagHelper>.Instance)
        {
            ViewContext = BuildViewContext(StubPage()),
        };

        var (ctx, output) = BuildOutput();
        await helper.ProcessAsync(ctx, output);

        Assert.That(output.TagName, Is.Null);
    }
}
