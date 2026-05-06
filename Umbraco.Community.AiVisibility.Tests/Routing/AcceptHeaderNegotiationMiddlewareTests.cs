using System.Text;
using Umbraco.Community.AiVisibility.Configuration;
using LlmsTxt.Umbraco.Controllers;
using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Routing;
using LlmsTxt.Umbraco.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Web.Common.Routing;

namespace LlmsTxt.Umbraco.Tests.Routing;

[TestFixture]
public class AcceptHeaderNegotiationMiddlewareTests
{
    private static readonly Guid HomeKey = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime HomeUpdated = new(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc);

    // ────────────────────────────────────────────────────────────────────────
    // ClientPrefersMarkdown — pure helper coverage
    // ────────────────────────────────────────────────────────────────────────

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase(" ", false)]
    [TestCase("text/markdown", true)]
    [TestCase("text/html", false)]
    [TestCase("application/json", false)]
    [TestCase("*/*", false)]
    [TestCase("text/markdown,text/html;q=0.9", true)]
    [TestCase("text/html,text/markdown;q=0.9", false)]
    [TestCase("text/markdown;q=0.5,text/html", false)]
    [TestCase("text/markdown,text/html", true)] // q-tied → first listed wins
    [TestCase("text/Markdown", true)]            // case-insensitive
    [TestCase("Text/markdown", true)]
    [TestCase("application/vnd.markdown", false)] // only text/markdown is supported
    [TestCase("text/markdown;q=0,text/html", false)] // explicit refusal
    [TestCase("text/markdown;q=0", false)] // RFC 7231 § 5.3.1 — q=0 alone is "not acceptable"
    [TestCase("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", false)] // browser default
    public void ClientPrefersMarkdown_QValueResolution(string? accept, bool expected)
    {
        Assert.That(AcceptHeaderNegotiationMiddleware.ClientPrefersMarkdown(accept), Is.EqualTo(expected));
    }

    [Test]
    public void ClientPrefersMarkdown_Garbage_ReturnsFalse()
    {
        // Totally malformed input → parser returns false → middleware treats as no
        // preference. Real-world AI crawlers should never send this; defensive only.
        Assert.That(AcceptHeaderNegotiationMiddleware.ClientPrefersMarkdown("not-a-mediatype"),
            Is.False);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Middleware behaviour — gates and short-circuit logic
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Invoke_NotGet_DoesNotIntervene_CallsNext()
    {
        var harness = NewHarness(method: "POST", accept: "text/markdown", path: "/home", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True);
        Assert.That(harness.Extractor.WasCalled, Is.False);
    }

    [TestCase("PUT")]
    [TestCase("DELETE")]
    [TestCase("PATCH")]
    public async Task Invoke_OtherWriteMethods_DoNotIntervene(string method)
    {
        var harness = NewHarness(method: method, accept: "text/markdown", path: "/home", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True);
        Assert.That(harness.Extractor.WasCalled, Is.False);
    }

    [Test]
    public async Task Invoke_HeadMethod_RespectsAccept()
    {
        var harness = NewHarness(method: "HEAD", accept: "text/markdown", path: "/home", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.False);
        Assert.That(harness.Extractor.WasCalled, Is.True);
        Assert.That(harness.Ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task Invoke_PathEndsInMd_DoesNotIntervene_CallsNext()
    {
        var harness = NewHarness(method: "GET", accept: "text/markdown", path: "/home.md", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True);
        Assert.That(harness.Extractor.WasCalled, Is.False,
            ".md suffix is owned by the LlmsPipelineFilter Endpoints route → MarkdownController");
    }

    [Test]
    public async Task Invoke_NoUmbracoRouteValues_DoesNotIntervene_CallsNext()
    {
        var harness = NewHarness(method: "GET", accept: "text/markdown", path: "/umbraco/login", routeValues: null);

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True);
        Assert.That(harness.Extractor.WasCalled, Is.False);
    }

    [Test]
    public async Task Invoke_PublishedContentNull_DoesNotIntervene_CallsNext()
    {
        var routeValues = BuildRouteValues(nullPublishedContent: true);
        var harness = NewHarness(method: "GET", accept: "text/markdown", path: "/missing", routeValues: routeValues);

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True);
        Assert.That(harness.Extractor.WasCalled, Is.False);
    }

    [Test]
    public async Task Invoke_AcceptHtml_CallsNext_NoExtractorInvocation()
    {
        var harness = NewHarness(method: "GET", accept: "text/html", path: "/home", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True);
        Assert.That(harness.Extractor.WasCalled, Is.False);
    }

    [Test]
    public async Task Invoke_AcceptStarStar_CallsNext_NoExtractorInvocation()
    {
        var harness = NewHarness(method: "GET", accept: "*/*", path: "/home", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True);
        Assert.That(harness.Extractor.WasCalled, Is.False);
    }

    [Test]
    public async Task Invoke_NoAcceptHeader_CallsNext_NoExtractorInvocation()
    {
        var harness = NewHarness(method: "GET", accept: null, path: "/home", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True);
        Assert.That(harness.Extractor.WasCalled, Is.False);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Middleware behaviour — divert to Markdown
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Invoke_AcceptMarkdown_InvokesExtractor_AndWriter()
    {
        var harness = NewHarness(method: "GET", accept: "text/markdown", path: "/home", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.False);
        Assert.That(harness.Extractor.WasCalled, Is.True);
        Assert.That(harness.Ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(harness.Ctx.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType));
    }

    [Test]
    public async Task Invoke_AcceptMarkdown_PassesPublishedContentAndCultureToExtractor()
    {
        var content = BuildContent();
        var routeValues = BuildRouteValues(publishedContent: content, culture: "en-GB");
        var harness = NewHarness(method: "GET", accept: "text/markdown", path: "/home", routeValues: routeValues);

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.Extractor.LastContent, Is.SameAs(content));
        Assert.That(harness.Extractor.LastCulture, Is.EqualTo("en-GB"));
    }

    [Test]
    public async Task Invoke_AcceptMarkdown_CanonicalPath_IsRequestPathValue()
    {
        // The canonical path the middleware sees is whatever Umbraco resolved — no
        // `.md` suffix appended; the path the user requested IS the canonical.
        var harness = NewHarness(
            method: "GET", accept: "text/markdown",
            path: "/about-us", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        // Body was written by the writer; the writer hashes route into ETag.
        var etag = harness.Ctx.Response.Headers["ETag"].ToString();
        Assert.That(etag, Does.StartWith("\""));
        // A different path should produce a different ETag — sanity check the writer
        // got the request path through.
        var other = NewHarness(method: "GET", accept: "text/markdown", path: "/contact", routeValues: BuildRouteValues());
        await other.Middleware.InvokeAsync(other.Ctx, other.Next);
        Assert.That(other.Ctx.Response.Headers["ETag"].ToString(), Is.Not.EqualTo(etag));
    }

    [Test]
    public async Task Negotiation_OnExcludedPage_Returns404()
    {
        // Story 3.1 § Failure & Edge Cases line 463 — `Accept: text/markdown`
        // on a canonical URL whose page is in the resolved exclusion list must
        // yield 404, NOT 200 + Markdown body. Without this gate, an editor's
        // exclusion configuration would only fire on the `.md` route, leaving
        // the negotiation path as a silent bypass.
        var content = BuildContentWithDoctype("redirectPage");
        var routeValues = BuildRouteValues(publishedContent: content, culture: "en-GB");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("example.test");
        ctx.Request.Path = "/old-page";
        ctx.Request.Headers.Accept = "text/markdown";
        ctx.Features.Set(routeValues);
        ctx.Response.Body = new MemoryStream();

        var extractor = new StubExtractor(BuildFound("# never reached"));
        var optionsMonitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        optionsMonitor.CurrentValue.Returns(new AiVisibilitySettings());
        var writer = new MarkdownResponseWriter(optionsMonitor);
        var logger = new RecordingLogger<AcceptHeaderNegotiationMiddleware>();

        // Resolver returns "redirectPage" in the exclusion set.
        var settingsResolver = Substitute.For<ISettingsResolver>();
        settingsResolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new ResolvedLlmsSettings(
                SiteName: null,
                SiteSummary: null,
                ExcludedDoctypeAliases: new HashSet<string>(new[] { "redirectPage" }, StringComparer.OrdinalIgnoreCase),
                BaseSettings: new AiVisibilitySettings())));

        var exclusionEvaluator = new DefaultExclusionEvaluator(
            settingsResolver,
            NullLogger<DefaultExclusionEvaluator>.Instance);
        var middleware = new AcceptHeaderNegotiationMiddleware(
            extractor, writer, exclusionEvaluator, optionsMonitor,
            Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>(),
            logger);
        var nextCalled = false;
        await middleware.InvokeAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; });

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound),
                "excluded page on negotiation path must return 404");
            Assert.That(extractor.WasCalled, Is.False,
                "extractor must not run when page is excluded");
            Assert.That(nextCalled, Is.False,
                "must not fall through to HTML — exclusion is a hard 404");
            Assert.That(ctx.Response.ContentType, Does.StartWith("application/problem+json"));
        });
    }

    private static IPublishedContent BuildContentWithDoctype(string contentTypeAlias)
    {
        var content = Substitute.For<IPublishedContent>();
        content.Key.Returns(HomeKey);
        content.Name.Returns("Home");
        content.UpdateDate.Returns(HomeUpdated);
        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns(contentTypeAlias);
        content.ContentType.Returns(contentType);
        content.GetProperty(Arg.Any<string>()).Returns((IPublishedProperty?)null);
        return content;
    }

    [Test]
    public async Task Invoke_AcceptMarkdown_ExtractorReturnsError_WritesProblemJson_500()
    {
        var error = MarkdownExtractionResult.Failed(
            new InvalidOperationException("boom"),
            sourceUrl: "https://example.test/home",
            contentKey: HomeKey);
        var harness = NewHarness(
            method: "GET", accept: "text/markdown",
            path: "/home", routeValues: BuildRouteValues(),
            extractorResult: error);

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.False);
        Assert.That(harness.Ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
        Assert.That(harness.Ctx.Response.ContentType, Does.StartWith("application/problem+json"));
        harness.Body.Position = 0;
        var body = await new StreamReader(harness.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(body, Does.Contain("Markdown extraction failed"));
    }

    [Test]
    public async Task Invoke_AcceptMarkdown_ExtractorReturnsFoundWithEmptyBody_FallsThroughToHtml_LogsWarning()
    {
        var empty = MarkdownExtractionResult.Found(
            markdown: string.Empty,
            contentKey: HomeKey,
            culture: "en-GB",
            updatedUtc: HomeUpdated,
            sourceUrl: "https://example.test/home");
        var harness = NewHarness(
            method: "GET", accept: "text/markdown",
            path: "/home", routeValues: BuildRouteValues(),
            extractorResult: empty);

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True,
            "empty Found body falls through — better HTML than a 500 with no body");
        // The defensive log line is part of the contract: it's the diagnostic
        // surface for a buggy extractor producing zero bytes. Without it, a
        // future refactor could silently drop the warning and the regression
        // wouldn't be caught.
        Assert.That(
            harness.Logger.Entries.Any(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("empty body", StringComparison.OrdinalIgnoreCase)),
            Is.True,
            "empty-body fall-through must emit a Warning log");
    }

    // OnStarting firing is brittle on DefaultHttpContext (the test response body
    // feature doesn't always invoke registered callbacks the way the real Kestrel
    // pipeline does), so the AppendVaryAcceptHeader helper is exercised directly.
    // The OnStarting hook itself is verified by the manual E2E gate in Task 7.

    [Test]
    public void AppendVaryAcceptHeader_NoExisting_SetsAccept()
    {
        var ctx = new DefaultHttpContext();
        AcceptHeaderNegotiationMiddleware.AppendVaryAcceptHeader(ctx);
        Assert.That(ctx.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
    }

    [Test]
    public void AppendVaryAcceptHeader_DifferentVary_AppendsAccept()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Headers["Vary"] = "User-Agent";
        AcceptHeaderNegotiationMiddleware.AppendVaryAcceptHeader(ctx);
        Assert.That(ctx.Response.Headers["Vary"].ToString(), Is.EqualTo("User-Agent, Accept"));
    }

    [Test]
    public void AppendVaryAcceptHeader_AcceptAlreadyPresent_NoDuplicate()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Headers["Vary"] = "Accept";
        AcceptHeaderNegotiationMiddleware.AppendVaryAcceptHeader(ctx);
        Assert.That(ctx.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"),
            "must not duplicate to `Accept, Accept`");
    }

    [Test]
    public void AppendVaryAcceptHeader_AcceptAmongOthers_NoDuplicate()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Headers["Vary"] = "User-Agent, Accept, Cookie";
        AcceptHeaderNegotiationMiddleware.AppendVaryAcceptHeader(ctx);
        Assert.That(ctx.Response.Headers["Vary"].ToString(), Is.EqualTo("User-Agent, Accept, Cookie"));
    }

    [Test]
    public void AppendVaryAcceptHeader_AcceptCaseInsensitive_NoDuplicate()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Headers["Vary"] = "accept";
        AcceptHeaderNegotiationMiddleware.AppendVaryAcceptHeader(ctx);
        // Existing `accept` is preserved; we just don't double-add ours.
        Assert.That(ctx.Response.Headers["Vary"].ToString(), Is.EqualTo("accept"));
    }

    [Test]
    public async Task Invoke_AcceptMarkdown_HonoursCancellation()
    {
        var harness = NewHarness(
            method: "GET", accept: "text/markdown",
            path: "/home", routeValues: BuildRouteValues());
        harness.Ctx.RequestAborted = new CancellationToken(canceled: true);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 5.1 — publication-site pinning (Task 9.5)
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DivertSuccess_PublishesMarkdownPageNotification()
    {
        // 200 path on the Accept-negotiated divert: middleware writes the
        // Markdown body via MarkdownResponseWriter, then publishes the
        // notification (StatusCode == 200 guard).
        var harness = NewHarness(
            method: "GET", accept: "text/markdown",
            path: "/home", routeValues: BuildRouteValues(culture: "en-GB"));

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        await harness.Publisher.Received(1).PublishMarkdownPageAsync(
            Arg.Any<HttpContext>(),
            Arg.Is<string>(p => p == "/home"),
            Arg.Any<Guid>(),
            Arg.Is<string?>(c => c == "en-GB"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExtractorErrorOnDivert_DoesNotPublish()
    {
        var harness = NewHarness(
            method: "GET", accept: "text/markdown",
            path: "/buggy", routeValues: BuildRouteValues(),
            extractorResult: MarkdownExtractionResult.Failed(
                new InvalidOperationException("boom"),
                sourceUrl: "https://example.test/buggy",
                contentKey: HomeKey));

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.Ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
        await harness.Publisher.DidNotReceiveWithAnyArgs().PublishMarkdownPageAsync(
            default!, default!, default, default, default);
    }

    [Test]
    public async Task EmptyBodyFallthroughOnDivert_DoesNotPublish()
    {
        // Empty Markdown → middleware falls through to the HTML pipeline.
        // No notification published — the Markdown route did not serve a body.
        // BuildFound prepends YAML front-matter; for a truly empty body we
        // construct the Found result inline (mirrors the sibling
        // Invoke_AcceptMarkdown_ExtractorReturnsFoundWithEmptyBody_FallsThroughToHtml_LogsWarning).
        var empty = MarkdownExtractionResult.Found(
            markdown: string.Empty,
            contentKey: HomeKey,
            culture: "en-GB",
            updatedUtc: HomeUpdated,
            sourceUrl: "https://example.test/home");
        var harness = NewHarness(
            method: "GET", accept: "text/markdown",
            path: "/empty", routeValues: BuildRouteValues(),
            extractorResult: empty);

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        Assert.That(harness.NextWasCalled, Is.True, "empty-body fall-through must continue to HTML pipeline");
        await harness.Publisher.DidNotReceiveWithAnyArgs().PublishMarkdownPageAsync(
            default!, default!, default, default, default);
    }

    [Test]
    public async Task NonGetRequest_DoesNotPublish()
    {
        // POST/PUT/DELETE never enter the divert — middleware short-circuits to next().
        var harness = NewHarness(
            method: "POST", accept: "text/markdown",
            path: "/home", routeValues: BuildRouteValues());

        await harness.Middleware.InvokeAsync(harness.Ctx, harness.Next);

        await harness.Publisher.DidNotReceiveWithAnyArgs().PublishMarkdownPageAsync(
            default!, default!, default, default, default);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Cache-entry sharing — symmetry with .md route
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ControllerAndMiddleware_PassIdenticalExtractorArguments_ProvingCacheKeySymmetry()
    {
        // Spec Task 6 final bullet — drive both surfaces against the SAME content +
        // culture and assert each pipeline calls IMarkdownContentExtractor.ExtractAsync
        // with the same (content, culture) pair. The cache decorator (Story 1.2)
        // composes its key as `llms:page:{nodeKey}:{NormaliseCulture(culture)}`, so
        // identical args → identical cache key → in production traffic the second
        // pipeline hits the cache entry the first one filled. AC1's "same cache
        // entry consulted" guarantee is therefore satisfied by composition; this
        // test pins the contract rather than the runtime cache hit.
        var content = BuildContent();
        const string culture = "en-GB";
        var sentinel = BuildFound("# x\n");

        // Drive .md controller path
        var controllerExtractor = new StubExtractor(sentinel);
        var resolver = new StubMarkdownRouteResolver(content, culture);
        var optionsMonitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        optionsMonitor.CurrentValue.Returns(new AiVisibilitySettings());
        var writer = new MarkdownResponseWriter(optionsMonitor);
        // Story 3.1 — settings resolver substitute returns appsettings-only
        // overlay so this Story-1.3 test stays green without exclusion impact.
        var settingsResolver = Substitute.For<ISettingsResolver>();
        settingsResolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new AiVisibilitySettings().ToResolved()));
        var controllerExclusion = new DefaultExclusionEvaluator(
            settingsResolver,
            NullLogger<DefaultExclusionEvaluator>.Instance);
        var controller = new MarkdownController(
            controllerExtractor,
            resolver,
            writer,
            controllerExclusion,
            optionsMonitor,
            Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>(),
            NullLogger<MarkdownController>.Instance);
        var controllerCtx = new DefaultHttpContext();
        controllerCtx.Request.Method = "GET";
        controllerCtx.Request.Scheme = "https";
        controllerCtx.Request.Host = new HostString("example.test");
        controllerCtx.Request.Path = "/home.md";
        controllerCtx.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = controllerCtx };

        await controller.Render(path: "/home.md", CancellationToken.None);

        // Drive Accept-negotiation middleware path
        var middlewareHarness = NewHarness(
            method: "GET", accept: "text/markdown", path: "/home",
            routeValues: BuildRouteValues(publishedContent: content, culture: culture));
        await middlewareHarness.Middleware.InvokeAsync(middlewareHarness.Ctx, middlewareHarness.Next);

        // Both pipelines invoked the extractor with the same (content, culture) pair —
        // therefore both produce the same cache key under the Story 1.2 decorator.
        Assert.That(controllerExtractor.LastContent, Is.SameAs(content),
            "controller passes UmbracoRouteValues.PublishedRequest.PublishedContent through");
        Assert.That(middlewareHarness.Extractor.LastContent, Is.SameAs(content),
            "middleware passes UmbracoRouteValues.PublishedRequest.PublishedContent through");
        Assert.That(controllerExtractor.LastCulture, Is.EqualTo(culture));
        Assert.That(middlewareHarness.Extractor.LastCulture, Is.EqualTo(culture));
    }

    private sealed class StubMarkdownRouteResolver : IMarkdownRouteResolver
    {
        private readonly IPublishedContent _content;
        private readonly string? _culture;
        public StubMarkdownRouteResolver(IPublishedContent content, string? culture)
        {
            _content = content;
            _culture = culture;
        }
        public Task<MarkdownRouteResolution> ResolveAsync(Uri absoluteUri, CancellationToken cancellationToken)
            => Task.FromResult(MarkdownRouteResolution.Found(_content, _culture));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required AcceptHeaderNegotiationMiddleware Middleware { get; init; }
        public required HttpContext Ctx { get; init; }
        public required StubExtractor Extractor { get; init; }
        public required MemoryStream Body { get; init; }
        public required RecordingLogger<AcceptHeaderNegotiationMiddleware> Logger { get; init; }
        public required LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher Publisher { get; init; }
        public RequestDelegate Next { get; set; } = ctx => Task.CompletedTask;
        public bool NextWasCalled => _nextCalled;
        private bool _nextCalled;

        public Harness()
        {
            Next = ctx => { _nextCalled = true; return Task.CompletedTask; };
        }
    }

    private sealed record RecordedLogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<RecordedLogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new RecordedLogEntry(logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static Harness NewHarness(
        string method,
        string? accept,
        string path,
        UmbracoRouteValues? routeValues,
        MarkdownExtractionResult? extractorResult = null,
        AiVisibilitySettings? settings = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("example.test");
        ctx.Request.Path = path;
        if (accept is not null)
        {
            ctx.Request.Headers.Accept = accept;
        }
        if (routeValues is not null)
        {
            ctx.Features.Set(routeValues);
        }
        var body = new MemoryStream();
        ctx.Response.Body = body;

        var extractor = new StubExtractor(extractorResult ?? BuildFound("# Body\n"));

        var resolvedSettings = settings ?? new AiVisibilitySettings();
        var optionsMonitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        optionsMonitor.CurrentValue.Returns(resolvedSettings);
        var writer = new MarkdownResponseWriter(optionsMonitor);

        var logger = new RecordingLogger<AcceptHeaderNegotiationMiddleware>();

        // Story 3.1 — middleware now consults ISettingsResolver on the
        // divert path so excluded pages return 404 (Failure & Edge Cases line
        // 463). Default substitute returns an empty exclusion list and treats
        // the page as not-excluded — Story-1.3-era tests stay green.
        var settingsResolver = Substitute.For<ISettingsResolver>();
        settingsResolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new AiVisibilitySettings().ToResolved()));

        var exclusionEvaluator = new DefaultExclusionEvaluator(
            settingsResolver,
            NullLogger<DefaultExclusionEvaluator>.Instance);
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        var middleware = new AcceptHeaderNegotiationMiddleware(
            extractor,
            writer,
            exclusionEvaluator,
            optionsMonitor,
            publisher,
            logger);

        return new Harness
        {
            Middleware = middleware,
            Ctx = ctx,
            Extractor = extractor,
            Body = body,
            Logger = logger,
            Publisher = publisher,
        };
    }

    private static UmbracoRouteValues BuildRouteValues(
        IPublishedContent? publishedContent = null,
        string? culture = "en-GB",
        bool nullPublishedContent = false)
    {
        // Materialise content BEFORE the .Returns(...) call so NSubstitute's last-call
        // tracking isn't confused by nested substitute-configuration in BuildContent().
        IPublishedContent? content = nullPublishedContent
            ? null
            : (publishedContent ?? BuildContent());
        var capturedCulture = culture;

        var publishedRequest = Substitute.For<IPublishedRequest>();
        publishedRequest.PublishedContent.Returns(content);
        publishedRequest.Culture.Returns(capturedCulture);
        return new UmbracoRouteValues(
            publishedRequest,
            new ControllerActionDescriptor());
    }

    private static IPublishedContent BuildContent()
    {
        var content = Substitute.For<IPublishedContent>();
        content.Key.Returns(HomeKey);
        content.Name.Returns("Home");
        content.UpdateDate.Returns(HomeUpdated);
        return content;
    }

    private static MarkdownExtractionResult BuildFound(string body)
        => MarkdownExtractionResult.Found(
            markdown: "---\ntitle: Home\nurl: https://example.test/home\nupdated: 2026-04-29T00:00:00Z\n---\n\n" + body,
            contentKey: HomeKey,
            culture: "en-GB",
            updatedUtc: HomeUpdated,
            sourceUrl: "https://example.test/home");

    private sealed class StubExtractor : IMarkdownContentExtractor
    {
        private readonly MarkdownExtractionResult _result;
        public bool WasCalled { get; private set; }
        public IPublishedContent? LastContent { get; private set; }
        public string? LastCulture { get; private set; }

        public StubExtractor(MarkdownExtractionResult result)
        {
            _result = result;
        }

        public Task<MarkdownExtractionResult> ExtractAsync(
            IPublishedContent content,
            string? culture,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            LastContent = content;
            LastCulture = culture;
            return Task.FromResult(_result);
        }
    }
}
