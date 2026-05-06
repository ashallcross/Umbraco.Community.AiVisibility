using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Web.Common.Routing;

namespace Umbraco.Community.AiVisibility.Tests.Routing;

/// <summary>
/// Story 4.1 ACs 1–4 — pins the DiscoverabilityHeaderMiddleware behaviour:
/// happy-path Link + Vary on resolved-content GET; suffix gate on .md; route
/// gate on missing UmbracoRouteValues; kill switch; exclusion gate; URL
/// provider failure modes; trailing-slash → /index.html.md.
/// </summary>
[TestFixture]
public class DiscoverabilityHeaderMiddlewareTests
{
    private static IOptionsMonitor<AiVisibilitySettings> Options(AiVisibilitySettings? settings = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(settings ?? new AiVisibilitySettings());
        return monitor;
    }

    private static IPublishedContent StubPage(string doctypeAlias = "homePage")
    {
        var content = Substitute.For<IPublishedContent>();
        var ct = Substitute.For<IPublishedContentType>();
        ct.Alias.Returns(doctypeAlias);
        content.ContentType.Returns(ct);
        content.Key.Returns(Guid.NewGuid());
        return content;
    }

    private static HttpContext BuildContext(
        string method = "GET",
        string path = "/home",
        string host = "example.com",
        IPublishedContent? routedContent = null,
        string? culture = "en-gb",
        string? acceptHeader = null)
    {
        var ctx = new DefaultHttpContext();
        // Replace the default IHttpResponseFeature with one that captures
        // OnStarting callbacks and exposes them for manual firing — DefaultHttpContext
        // never fires OnStarting on its own, so middleware that defers header writes
        // via OnStarting would otherwise leave headers unwritten in unit tests.
        ctx.Features.Set<IHttpResponseFeature>(new FiringResponseFeature());
        ctx.Request.Method = method;
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString(host);
        ctx.Request.Path = path;
        if (acceptHeader is not null)
        {
            ctx.Request.Headers.Accept = acceptHeader;
        }

        if (routedContent is not null)
        {
            var publishedRequest = Substitute.For<IPublishedRequest>();
            publishedRequest.PublishedContent.Returns(routedContent);
            publishedRequest.Culture.Returns(culture);
            var routeValues = new UmbracoRouteValues(publishedRequest, controllerActionDescriptor: null!);
            ctx.Features.Set(routeValues);
        }

        return ctx;
    }

    /// <summary>
    /// Runs the middleware then fires any OnStarting callbacks the middleware
    /// registered (DefaultHttpContext captures but never fires them). Tests
    /// assert headers AFTER this call.
    /// </summary>
    private static async Task RunAndCommit(DiscoverabilityHeaderMiddleware middleware, HttpContext ctx, RequestDelegate? next = null)
    {
        await middleware.InvokeAsync(ctx, next ?? NoopNext());
        if (ctx.Features.Get<IHttpResponseFeature>() is FiringResponseFeature firing)
        {
            await firing.FireOnStartingAsync();
        }
    }

    private sealed class FiringResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = new();

        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));
        public void OnCompleted(Func<object, Task> callback, object state) { }

        public async Task FireOnStartingAsync()
        {
            // Fire LIFO per ASP.NET Core convention.
            for (var i = _onStarting.Count - 1; i >= 0; i--)
            {
                await _onStarting[i].Callback(_onStarting[i].State);
            }
            HasStarted = true;
        }

        public void SetStatusCode(int statusCode) => StatusCode = statusCode;
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
        evaluator
            .IsExcludedAsync(Arg.Any<IPublishedContent>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        return evaluator;
    }

    private static IExclusionEvaluator Excluded()
    {
        var evaluator = Substitute.For<IExclusionEvaluator>();
        evaluator
            .IsExcludedAsync(Arg.Any<IPublishedContent>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return evaluator;
    }

    private static DiscoverabilityHeaderMiddleware Build(
        IOptionsMonitor<AiVisibilitySettings>? settings = null,
        IExclusionEvaluator? exclusion = null,
        IPublishedUrlProvider? urlProvider = null)
        => new(
            settings ?? Options(),
            exclusion ?? NotExcluded(),
            urlProvider ?? UrlProviderReturning("/home"),
            NullLogger<DiscoverabilityHeaderMiddleware>.Instance);

    private static RequestDelegate NoopNext() => _ => Task.CompletedTask;

    [Test]
    public async Task InvokeAsync_HappyPath_EmitsLinkAndVaryAccept()
    {
        var ctx = BuildContext(routedContent: StubPage());
        var middleware = Build(urlProvider: UrlProviderReturning("/home"));

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers[Constants.HttpHeaders.Link].ToString(),
            Is.EqualTo("</home.md>; rel=\"alternate\"; type=\"text/markdown\""));
        Assert.That(ctx.Response.Headers[Constants.HttpHeaders.Vary].ToString(),
            Does.Contain("Accept"));
    }

    [Test]
    public async Task InvokeAsync_HeadMethod_EmitsLinkHeader()
    {
        var ctx = BuildContext(method: "HEAD", routedContent: StubPage());
        var middleware = Build();

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.True);
    }

    [Test]
    public async Task InvokeAsync_PostMethod_FallsThrough_NoLinkHeader()
    {
        var ctx = BuildContext(method: "POST", routedContent: StubPage());
        var middleware = Build();

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    [Test]
    public async Task InvokeAsync_MdSuffixRequest_FallsThrough_NoLinkHeader()
    {
        var ctx = BuildContext(path: "/home.md", routedContent: StubPage());
        var middleware = Build();

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    [Test]
    public async Task InvokeAsync_IndexHtmlMdRequest_FallsThrough_NoLinkHeader()
    {
        var ctx = BuildContext(path: "/blog/index.html.md", routedContent: StubPage());
        var middleware = Build();

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    [Test]
    public async Task InvokeAsync_KillSwitchOff_NoLinkOrVary()
    {
        var settings = new AiVisibilitySettings
        {
            DiscoverabilityHeader = new DiscoverabilityHeaderSettings { Enabled = false },
        };
        var ctx = BuildContext(routedContent: StubPage());
        var middleware = Build(settings: Options(settings));

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Vary), Is.False);
    }

    [Test]
    public async Task InvokeAsync_NoUmbracoRouteValues_FallsThrough_NoLinkHeader()
    {
        // No routedContent passed → UmbracoRouteValues feature not set.
        var ctx = BuildContext();
        var middleware = Build();

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    [Test]
    public async Task InvokeAsync_PublishedContentNull_FallsThrough_NoLinkHeader()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/home";
        var publishedRequest = Substitute.For<IPublishedRequest>();
        publishedRequest.PublishedContent.Returns((IPublishedContent?)null);
        var routeValues = new UmbracoRouteValues(publishedRequest, controllerActionDescriptor: null!);
        ctx.Features.Set(routeValues);

        var middleware = Build();
        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    [Test]
    public async Task InvokeAsync_ExcludedPage_FallsThrough_NoLinkHeader()
    {
        var ctx = BuildContext(routedContent: StubPage());
        var middleware = Build(exclusion: Excluded());

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    /// <summary>
    /// AC2 symmetry pin — when this middleware exits early because the page is
    /// excluded, it does NOT write Vary: Accept itself (correct — the kill-
    /// switch path is the only one that fully suppresses Vary). The sibling
    /// AcceptHeaderNegotiationMiddleware is the surface that owns Vary on every
    /// published-content HTML response. This test pins THIS middleware's
    /// behaviour on the excluded path: no Link AND no Vary written here. The
    /// production guarantee that Vary still ships on excluded responses is
    /// covered by AcceptHeaderNegotiationMiddlewareTests' Vary-on-OnStarting
    /// path, which fires regardless of exclusion (negotiation middleware uses
    /// a different gate — Markdown content negotiation, not Link emission).
    /// </summary>
    [Test]
    public async Task InvokeAsync_ExcludedPage_DoesNotWriteVaryFromThisMiddleware()
    {
        var ctx = BuildContext(routedContent: StubPage());
        var middleware = Build(exclusion: Excluded());

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Vary), Is.False,
            "Excluded path must not write Vary from this middleware — sibling AcceptHeaderNegotiationMiddleware owns the on-published-content Vary write");
    }

    /// <summary>
    /// D1 patch — downstream rewrites status to 4xx/5xx after middleware
    /// decision. Headers are flushed via OnStarting + StatusCode &lt; 300 guard,
    /// so a downstream filter that flips the response to 500 must NOT carry
    /// the Link header onto the error response.
    /// </summary>
    [Test]
    public async Task InvokeAsync_DownstreamReturns500_DoesNotWriteLink()
    {
        var ctx = BuildContext(routedContent: StubPage());
        var middleware = Build(urlProvider: UrlProviderReturning("/home"));

        // next() simulates a downstream exception handler that converted the
        // response to 500 after this middleware made its routing-time decision.
        await RunAndCommit(middleware, ctx, next: c =>
        {
            c.Response.StatusCode = 500;
            return Task.CompletedTask;
        });

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False,
            "Link header must not ship on a downstream-rewritten 500");
        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Vary), Is.False,
            "Vary from this middleware must not ship on a downstream-rewritten 500");
    }

    /// <summary>
    /// D1 patch corollary — a 3xx redirect should also suppress the Link
    /// header. Status &gt;= 300 catches both error and redirect classes.
    /// </summary>
    [Test]
    public async Task InvokeAsync_DownstreamReturns302_DoesNotWriteLink()
    {
        var ctx = BuildContext(routedContent: StubPage());
        var middleware = Build(urlProvider: UrlProviderReturning("/home"));

        await RunAndCommit(middleware, ctx, next: c =>
        {
            c.Response.StatusCode = 302;
            return Task.CompletedTask;
        });

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    /// <summary>
    /// P1 patch — alternate URL containing CR/LF or &lt;&gt; chars must be
    /// rejected. Header injection guard.
    /// </summary>
    [TestCase("/home\r\nX-Evil: 1")]
    [TestCase("/home\nX-Evil: 1")]
    [TestCase("/home<script>")]
    [TestCase("/home>injection")]
    public async Task InvokeAsync_AdversarialUrl_FallsThrough_NoLinkHeader(string adversarialUrl)
    {
        var ctx = BuildContext(routedContent: StubPage());
        var middleware = Build(urlProvider: UrlProviderReturning(adversarialUrl));

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    /// <summary>
    /// P12 patch — trailing-slash on .md path must hit the suffix gate.
    /// Adopter rewrites occasionally append `/` to paths.
    /// </summary>
    [TestCase("/home.md/")]
    [TestCase("/blog/index.html.md/")]
    public async Task InvokeAsync_MdSuffixWithTrailingSlash_FallsThrough_NoLinkHeader(string path)
    {
        var ctx = BuildContext(path: path, routedContent: StubPage());
        var middleware = Build();

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    [Test]
    public async Task InvokeAsync_UrlProviderThrows_FailsOpen_NoLinkHeader()
    {
        var provider = Substitute.For<IPublishedUrlProvider>();
        provider
            .GetUrl(Arg.Any<IPublishedContent>(), Arg.Any<UrlMode>(), Arg.Any<string?>(), Arg.Any<Uri?>())
            .Throws(new InvalidOperationException("provider glitch"));
        var ctx = BuildContext(routedContent: StubPage());
        var middleware = Build(urlProvider: provider);

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    [Test]
    public async Task InvokeAsync_UrlProviderReturnsHash_FallsThrough_NoLinkHeader()
    {
        var ctx = BuildContext(routedContent: StubPage());
        var middleware = Build(urlProvider: UrlProviderReturning("#"));

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.False);
    }

    [Test]
    public async Task InvokeAsync_TrailingSlashUrl_EmitsIndexHtmlMd()
    {
        var ctx = BuildContext(path: "/blog/", routedContent: StubPage());
        var middleware = Build(urlProvider: UrlProviderReturning("/blog/"));

        await RunAndCommit(middleware, ctx);

        Assert.That(ctx.Response.Headers[Constants.HttpHeaders.Link].ToString(),
            Is.EqualTo("</blog/index.html.md>; rel=\"alternate\"; type=\"text/markdown\""));
    }

    [Test]
    public async Task InvokeAsync_VaryAlreadyContainsAccept_DoesNotDuplicate()
    {
        var ctx = BuildContext(routedContent: StubPage());
        ctx.Response.Headers[Constants.HttpHeaders.Vary] = "Accept-Encoding, Accept";
        var middleware = Build();

        await RunAndCommit(middleware, ctx);

        var vary = ctx.Response.Headers[Constants.HttpHeaders.Vary].ToString();
        // Pre-existing tokens MUST survive (Accept-Encoding from upstream
        // ResponseCompression, etc.) AND the middleware's Accept write must
        // dedup against the existing token.
        Assert.That(vary, Does.Contain("Accept-Encoding"),
            "Pre-existing Vary tokens must survive append-not-overwrite (this regression-pins VaryHeaderHelper.AppendAccept's contract)");
        var acceptTokens = vary
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Count(t => string.Equals(t, "Accept", StringComparison.OrdinalIgnoreCase));
        Assert.That(acceptTokens, Is.EqualTo(1),
            $"Vary header should contain exactly one Accept token but was '{vary}'");
        // Pin that the middleware actually ran the Vary write (Link header
        // present proves the OnStarting callback fired and the helper was
        // invoked — without this the test would pass even if the middleware
        // never appended).
        Assert.That(ctx.Response.Headers.ContainsKey(Constants.HttpHeaders.Link), Is.True,
            "Link header must be present — proves the OnStarting callback fired and Vary write was reached");
    }
}
