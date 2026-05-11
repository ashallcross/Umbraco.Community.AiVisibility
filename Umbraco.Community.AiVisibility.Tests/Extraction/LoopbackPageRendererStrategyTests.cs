using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Community.AiVisibility;
using Umbraco.Community.AiVisibility.Extraction;

namespace Umbraco.Community.AiVisibility.Tests.Extraction;

/// <summary>
/// Story 7.2 AC3-AC6 + AC11 — unit tests for the loopback page renderer
/// strategy. Pins host-header decoupling, Accept: text/html override,
/// outbound recursion-marker header, 3xx fail-loud diagnostic with Location,
/// non-success failure shape, cancellation propagation, and the
/// resolver-throw-bubbles-out contract.
/// </summary>
[TestFixture]
public class LoopbackPageRendererStrategyTests
{
    /// <summary>
    /// AC3-AC6 happy path — strategy issues an HTTP GET against the
    /// resolver-resolved transport target, sets the Host header to the
    /// published-content authority (from <see cref="IPublishedUrlProvider.GetUrl"/>),
    /// clears + re-adds <c>Accept: text/html</c>, and adds the
    /// <c>X-AiVisibility-Loopback: 1</c> recursion marker. 200 OK with
    /// body returns <see cref="PageRenderResult.Ok"/> with body verbatim.
    /// </summary>
    [Test]
    public async Task RenderAsync_HappyPath_ReturnsOk()
    {
        var content = BuildPublishedContent("homePage");
        var absoluteUri = new Uri("http://incoming.example/about");
        var target = new LoopbackTarget(new Uri("http://127.0.0.1:5000/"), CertBypassEligible: true);

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>loopback body</html>"),
        });

        var (strategy, _) = BuildStrategy(
            handler: handler,
            resolverReturns: target,
            publishedUrl: "http://sitea.example/about");

        var result = await strategy.RenderAsync(content, absoluteUri, culture: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PageRenderStatus.Ok));
            Assert.That(result.Html, Is.EqualTo("<html>loopback body</html>"));
            Assert.That(handler.Captured, Is.Not.Null);
            Assert.That(handler.Captured!.RequestUri, Is.EqualTo(new Uri("http://127.0.0.1:5000/about")),
                "transport target = local binding authority + published path");
            // Read via GetValues("Host") — the production code uses
            // TryAddWithoutValidation so port-including hosts survive (the
            // typed Headers.Host setter rejects values containing ":port").
            Assert.That(handler.Captured.Headers.GetValues("Host").Single(), Is.EqualTo("sitea.example"),
                "Host header preserved from IPublishedUrlProvider — multi-site domain resolution");
            Assert.That(handler.Captured.Headers.Accept.Single().MediaType, Is.EqualTo("text/html"),
                "Accept overridden to text/html — prevents Accept-header negotiation middleware re-routing");
            Assert.That(handler.Captured.Headers.GetValues(Constants.Http.LoopbackMarkerHeaderName).Single(),
                Is.EqualTo("1"),
                "outbound recursion-marker header set");
        });
    }

    /// <summary>
    /// AC4 fallback — when <see cref="IPublishedUrlProvider.GetUrl"/>
    /// returns a relative path (single-site dev install with no IDomain
    /// bound), the strategy falls back to <c>absoluteUri.Authority</c>
    /// for the Host header so the loopback at least matches the inbound
    /// request's host.
    /// </summary>
    [Test]
    public async Task RenderAsync_PublishedUrlReturnsRelative_FallsBackToInboundAuthority()
    {
        var content = BuildPublishedContent("homePage");
        var absoluteUri = new Uri("http://localhost:8080/about");
        var target = new LoopbackTarget(new Uri("http://127.0.0.1:5000/"), CertBypassEligible: true);

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html/>"),
        });

        var (strategy, _) = BuildStrategy(
            handler: handler,
            resolverReturns: target,
            publishedUrl: "/about");

        await strategy.RenderAsync(content, absoluteUri, culture: null, CancellationToken.None);

        // Read via GetValues("Host") rather than the typed Headers.Host
        // getter — the typed getter strips the :port portion (returns IdnHost
        // only). We need the raw header value to confirm the port survived.
        var hostHeader = handler.Captured!.Headers.GetValues("Host").Single();
        Assert.That(hostHeader, Is.EqualTo("localhost:8080"),
            "Host header falls back to absoluteUri.Authority when published URL is relative");
    }

    /// <summary>
    /// AC3 step 6b — non-success status (404) → render failure with
    /// diagnostic including the path. Caller surfaces it via
    /// <see cref="PageRenderResult.Failed"/>.
    /// </summary>
    [Test]
    public async Task RenderAsync_NonSuccessStatus_ReturnsFailedWithDiagnostic()
    {
        var content = BuildPublishedContent("homePage");
        var absoluteUri = new Uri("http://incoming.example/missing");

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var (strategy, _) = BuildStrategy(
            handler: handler,
            resolverReturns: new LoopbackTarget(new Uri("http://127.0.0.1:5000/"), true),
            publishedUrl: "http://sitea.example/missing");

        var result = await strategy.RenderAsync(content, absoluteUri, culture: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PageRenderStatus.Error));
            Assert.That(result.Error, Is.InstanceOf<InvalidOperationException>());
            Assert.That(result.Error!.Message, Does.Contain("404"));
            Assert.That(result.Error!.Message, Does.Contain("/missing"));
        });
    }

    /// <summary>
    /// AC3 step 6a + Failure case 7 — 3xx response → render failure with
    /// diagnostic naming the Location header value. Route resolver already
    /// produced canonical form; redirect signals conflicting middleware.
    /// </summary>
    [Test]
    public async Task RenderAsync_RedirectStatus_ReturnsFailedWithLocationDiagnostic()
    {
        var content = BuildPublishedContent("homePage");
        var absoluteUri = new Uri("http://incoming.example/old-page");

        var handler = new TestHttpMessageHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            resp.Headers.Location = new Uri("/new-page", UriKind.Relative);
            return resp;
        });

        var (strategy, _) = BuildStrategy(
            handler: handler,
            resolverReturns: new LoopbackTarget(new Uri("http://127.0.0.1:5000/"), true),
            publishedUrl: "http://sitea.example/old-page");

        var result = await strategy.RenderAsync(content, absoluteUri, culture: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PageRenderStatus.Error));
            Assert.That(result.Error!.Message, Does.Contain("301"));
            Assert.That(result.Error.Message, Does.Contain("Location: /new-page"),
                "diagnostic must surface the Location header value verbatim");
            Assert.That(result.Error.Message, Does.Contain("conflicting middleware"),
                "diagnostic must explain the canonical-URL-already-resolved invariant");
        });
    }

    /// <summary>
    /// AC3 step 4 — when culture is provided, the strategy sets
    /// <c>Accept-Language</c> on the outbound request so the loopback's
    /// Umbraco pipeline routes culture-prefixed paths correctly in
    /// multi-language sites.
    /// </summary>
    [Test]
    public async Task RenderAsync_CulturePresent_AppliesAcceptLanguage()
    {
        var content = BuildPublishedContent("homePage");
        var absoluteUri = new Uri("http://incoming.example/about");
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html/>"),
        });

        var (strategy, _) = BuildStrategy(
            handler: handler,
            resolverReturns: new LoopbackTarget(new Uri("http://127.0.0.1:5000/"), true),
            publishedUrl: "http://sitea.example/about");

        await strategy.RenderAsync(content, absoluteUri, culture: "fr-fr", CancellationToken.None);

        Assert.That(handler.Captured!.Headers.AcceptLanguage.Single().Value, Is.EqualTo("fr-fr"),
            "Accept-Language must be set when culture is provided");
    }

    /// <summary>
    /// CR7.2 patch — a malformed <c>culture</c> value (CRLF injection,
    /// non-IETF shape) makes <see cref="System.Net.Http.Headers.HttpRequestHeaders.AcceptLanguage"/>
    /// .ParseAdd throw <see cref="FormatException"/>. The strategy's outer
    /// try/catch starts at <c>SendAsync</c>, so the ParseAdd throw would
    /// otherwise escape uncaught. The patch wraps ParseAdd in an inner
    /// try/catch and logs-and-continues — the render proceeds without
    /// Accept-Language rather than failing.
    /// </summary>
    [TestCase("not a valid culture", TestName = "malformed culture skipped")]
    [TestCase("en-us\r\nX-Injected: evil", TestName = "CRLF-injection culture skipped")]
    public async Task RenderAsync_MalformedCulture_SkipsAcceptLanguageAndContinues(string malformedCulture)
    {
        var content = BuildPublishedContent("homePage");
        var absoluteUri = new Uri("http://incoming.example/about");
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html/>"),
        });

        var (strategy, _) = BuildStrategy(
            handler: handler,
            resolverReturns: new LoopbackTarget(new Uri("http://127.0.0.1:5000/"), true),
            publishedUrl: "http://sitea.example/about");

        var result = await strategy.RenderAsync(content, absoluteUri, culture: malformedCulture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PageRenderStatus.Ok),
                "render must succeed without Accept-Language rather than throwing FormatException uncaught");
            Assert.That(handler.Captured!.Headers.AcceptLanguage, Is.Empty,
                "malformed culture must NOT leak into the outbound Accept-Language header");
            // Defence-in-depth: the CRLF-injection variant must NOT have
            // surfaced any custom header on the outbound request.
            Assert.That(handler.Captured.Headers.Contains("X-Injected"), Is.False,
                "CRLF injection must not split the Accept-Language header into a sibling injected header");
        });
    }


    /// <summary>
    /// AC3 cancellation contract — pre-cancelled token throws
    /// <see cref="OperationCanceledException"/> at the strategy entry without
    /// being wrapped into <see cref="PageRenderResult.Failed"/>. Matches
    /// Story 7.1's RazorPageRendererStrategy convention.
    /// </summary>
    [Test]
    public void RenderAsync_CancellationRequested_PropagatesOperationCanceledException()
    {
        var content = BuildPublishedContent("homePage");
        var absoluteUri = new Uri("http://incoming.example/about");
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var (strategy, _) = BuildStrategy(
            handler: handler,
            resolverReturns: new LoopbackTarget(new Uri("http://127.0.0.1:5000/"), true),
            publishedUrl: "http://sitea.example/about");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await strategy.RenderAsync(content, absoluteUri, culture: null, cts.Token));
    }

    /// <summary>
    /// AC3 outer catch — when the resolver throws (environmental failure;
    /// no usable binding), the exception bubbles through the strategy's
    /// outer catch is bypassed (the resolver call sits BEFORE the try/catch
    /// that handles HTTP-related exceptions). Caller (orchestrator → controller)
    /// sees the InvalidOperationException directly.
    /// <para>
    /// <b>Implementation note:</b> the production code calls
    /// <c>_loopbackUrlResolver.Resolve()</c> outside the try block (the
    /// try wraps only <c>SendAsync</c> + the response handling). A resolver
    /// throw therefore bubbles unchanged — environmental misconfiguration
    /// surfaces with full diagnostic, not wrapped to PageRenderResult.Failed.
    /// </para>
    /// </summary>
    [Test]
    public void RenderAsync_ResolverThrows_PropagatesException()
    {
        var content = BuildPublishedContent("homePage");
        var absoluteUri = new Uri("http://incoming.example/about");
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var resolver = Substitute.For<ILoopbackUrlResolver>();
        resolver.Resolve().Returns(_ => throw new InvalidOperationException("no usable binding"));
        var publishedUrlProvider = Substitute.For<IPublishedUrlProvider>();
        publishedUrlProvider
            .GetUrl(Arg.Any<IPublishedContent>(), Arg.Any<UrlMode>(), Arg.Any<string?>(), Arg.Any<Uri?>())
            .Returns("http://sitea.example/about");

        var factory = BuildHttpClientFactory(handler);
        var strategy = new LoopbackPageRendererStrategy(
            factory,
            resolver,
            publishedUrlProvider,
            NullLogger<LoopbackPageRendererStrategy>.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await strategy.RenderAsync(content, absoluteUri, culture: null, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("no usable binding"),
            "resolver's environmental failure bubbles unchanged — NOT wrapped to PageRenderResult.Failed");
    }

    /// <summary>
    /// AC3 outer catch — HttpClient-side failures (connection refused, DNS,
    /// transient network errors) are caught and surfaced as
    /// <see cref="PageRenderResult.Failed"/> with the inner exception
    /// preserved. Caller (DefaultMarkdownContentExtractor) gets full
    /// root-cause stack trace via <c>Error.ToString()</c>.
    /// </summary>
    [Test]
    public async Task RenderAsync_HttpClientThrows_ReturnsFailed()
    {
        var content = BuildPublishedContent("homePage");
        var absoluteUri = new Uri("http://incoming.example/about");
        var inner = new HttpRequestException("connection refused");
        var handler = new TestHttpMessageHandler(_ => throw inner);

        var (strategy, _) = BuildStrategy(
            handler: handler,
            resolverReturns: new LoopbackTarget(new Uri("http://127.0.0.1:5000/"), true),
            publishedUrl: "http://sitea.example/about");

        var result = await strategy.RenderAsync(content, absoluteUri, culture: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PageRenderStatus.Error));
            Assert.That(result.Error, Is.SameAs(inner),
                "inner exception preserved verbatim — root-cause stack trace stays intact");
        });
    }

    private static (LoopbackPageRendererStrategy Strategy, IHttpClientFactory Factory) BuildStrategy(
        TestHttpMessageHandler handler,
        LoopbackTarget resolverReturns,
        string publishedUrl)
    {
        var resolver = Substitute.For<ILoopbackUrlResolver>();
        resolver.Resolve().Returns(resolverReturns);

        var publishedUrlProvider = Substitute.For<IPublishedUrlProvider>();
        publishedUrlProvider
            .GetUrl(Arg.Any<IPublishedContent>(), Arg.Any<UrlMode>(), Arg.Any<string?>(), Arg.Any<Uri?>())
            .Returns(publishedUrl);

        var factory = BuildHttpClientFactory(handler);

        var strategy = new LoopbackPageRendererStrategy(
            factory,
            resolver,
            publishedUrlProvider,
            NullLogger<LoopbackPageRendererStrategy>.Instance);

        return (strategy, factory);
    }

    private static IHttpClientFactory BuildHttpClientFactory(TestHttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Constants.Http.LoopbackHttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    private static IPublishedContent BuildPublishedContent(string contentTypeAlias)
    {
        var content = Substitute.For<IPublishedContent>();
        content.Key.Returns(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        content.Path.Returns("-1,1234");

        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns(contentTypeAlias);
        content.ContentType.Returns(contentType);

        return content;
    }

    /// <summary>
    /// Captures the outbound <see cref="HttpRequestMessage"/> AND returns
    /// a configurable response. The strategy holds an
    /// <see cref="HttpClient"/> backed by this handler; assertions inspect
    /// <see cref="Captured"/> to verify Host/Accept/marker headers per
    /// AC3-AC6.
    /// </summary>
    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Captured = request;
            return Task.FromResult(_respond(request));
        }
    }
}
