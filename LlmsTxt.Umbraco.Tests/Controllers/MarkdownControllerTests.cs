using System.Text;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Controllers;
using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Routing;
using LlmsTxt.Umbraco.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace LlmsTxt.Umbraco.Tests.Controllers;

[TestFixture]
public class MarkdownControllerTests
{
    private static readonly Guid HomeKey = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime HomeUpdated = new(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Render_PathResolves_Returns200_WithMarkdownContentType()
    {
        var extractor = new StubExtractor(BuildFound("# Home\n"));
        var resolver = MakeStubResolver(content: BuildContent(), culture: "en-GB");
        var body = new MemoryStream();
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/home.md", body: body);

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        // Story 1.3: response is now written through IMarkdownResponseWriter; controller
        // returns EmptyResult after the writer has flushed headers + body to the response.
        Assert.That(result, Is.InstanceOf<EmptyResult>());
        Assert.That(controller.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(controller.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType));

        body.Position = 0;
        var rendered = await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(rendered, Does.Contain("# Home"));

        Assert.That(controller.Response.Headers.ContainsKey(Constants.HttpHeaders.XMarkdownTokens), Is.True);
        // Story 1.2 — Vary, Cache-Control, ETag emitted via the response writer.
        Assert.That(controller.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
        Assert.That(controller.Response.Headers["Cache-Control"].ToString(),
            Does.StartWith("public, max-age="));
        Assert.That(controller.Response.Headers["ETag"].ToString(), Does.StartWith("\""));
        // AC9 from Story 1.1 — no rel=canonical link.
        Assert.That(controller.Response.Headers.ContainsKey("Link"), Is.False, "AC9 — no rel=canonical link header");
    }

    [Test]
    public async Task Render_NotFound_Returns404()
    {
        var extractor = new StubExtractor(BuildFound("never reached"));
        var resolver = StubResolverNotFound();
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/does-not-exist.md");

        var result = await controller.Render(path: "/does-not-exist.md", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
        Assert.That(extractor.WasCalled, Is.False, "extractor must not run when route doesn't resolve");
    }

    [Test]
    public async Task Render_ExtractorError_Returns500ProblemDetails()
    {
        var extractor = new StubExtractor(MarkdownExtractionResult.Failed(
            new InvalidOperationException("boom"),
            sourceUrl: "https://example.test/buggy",
            contentKey: Guid.Parse("22222222-2222-2222-2222-222222222222")));
        var resolver = MakeStubResolver(content: BuildContent(), culture: null);
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/buggy.md");

        var result = await controller.Render(path: "/buggy.md", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var problem = (ObjectResult)result;
        Assert.That(problem.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
        Assert.That(problem.Value, Is.InstanceOf<ProblemDetails>());
    }

    [Test]
    public async Task Render_MalformedPath_Returns404()
    {
        // Defensive: even if the route constraint slips up, malformed input must not 500.
        var extractor = new StubExtractor(BuildFound("never reached"));
        var resolver = StubResolverNotFound();
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/something.html");

        var result = await controller.Render(path: "/something.html", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
        Assert.That(extractor.WasCalled, Is.False, "extractor must not be invoked when path normaliser rejects input");
        Assert.That(resolver.CallCount, Is.EqualTo(0), "resolver must not be invoked on a malformed path");
    }

    [Test]
    public async Task Render_BuildsAbsoluteUriFromRequestSchemeAndHost()
    {
        Uri? capturedUri = null;
        var resolver = new StubResolver(MarkdownRouteResolution.Found(BuildContent(), null))
        {
            OnCalled = uri => capturedUri = uri,
        };
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n")),
            resolver,
            "GET", "https", "siteA.example", "/about.md");

        await controller.Render(path: "/about.md", CancellationToken.None);

        Assert.That(capturedUri, Is.Not.Null);
        // Note: the `Uri` ctor lowercases the authority component — `SiteA.Example`
        // becomes `sitea.example` automatically. We're not normalising case ourselves.
        Assert.That(capturedUri!.AbsoluteUri, Is.EqualTo("https://sitea.example/about"));
    }

    [Test]
    public async Task Render_HonoursCancellationToken()
    {
        var extractor = new StubExtractor(BuildFound("# x\n"));
        var resolver = MakeStubResolver(content: BuildContent(), culture: null);
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/home.md");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await controller.Render(path: "/home.md", cts.Token));
    }

    [Test]
    public async Task Render_MalformedHostHeader_Returns400()
    {
        var extractor = new StubExtractor(BuildFound("never reached"));
        var resolver = MakeStubResolver(content: BuildContent(), culture: null);
        // A double-dotted host produces UriFormatException in the `Uri` ctor; should be 400 not 500.
        var controller = MakeController(extractor, resolver, "GET", "https", "my..invalid..host", "/home.md");

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestResult>());
        Assert.That(extractor.WasCalled, Is.False, "extractor must not run when URI cannot be built");
    }

    [Test]
    public void Render_OnlyAcceptsGetAndHead()
    {
        // The controller's HTTP-method binding is route-attribute-driven; verify the
        // attributes are present so MVC's action selector returns 405 for POST/PUT/etc.
        var renderMethod = typeof(MarkdownController).GetMethod(nameof(MarkdownController.Render))!;
        var attrs = renderMethod.GetCustomAttributes(inherit: true)
            .Select(a => a.GetType().Name)
            .ToArray();
        Assert.That(attrs, Does.Contain("HttpGetAttribute"));
        Assert.That(attrs, Does.Contain("HttpHeadAttribute"));
        Assert.That(attrs, Does.Not.Contain("HttpPostAttribute"));
        Assert.That(attrs, Does.Not.Contain("HttpPutAttribute"));
        Assert.That(attrs, Does.Not.Contain("HttpDeleteAttribute"));
    }

    [TestCase("/docs/index.html.md", ExpectedResult = "https://example.test/docs/")]
    [TestCase("/docs/.md", ExpectedResult = "https://example.test/docs/")]
    [TestCase("/docs.md", ExpectedResult = "https://example.test/docs")]
    public async Task<string> Render_NormalisesAllAcceptedSuffixForms(string capturedPath)
    {
        Uri? capturedUri = null;
        var resolver = new StubResolver(MarkdownRouteResolution.Found(BuildContent(), null))
        {
            OnCalled = uri => capturedUri = uri,
        };
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n")),
            resolver,
            "GET", "https", "example.test", capturedPath);

        await controller.Render(path: capturedPath, CancellationToken.None);

        return capturedUri!.AbsoluteUri;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 1.2 — ETag, Cache-Control, Vary, If-None-Match → 304
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_Found_SetsCacheControlPublicMaxAge()
    {
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n")),
            MakeStubResolver(content: BuildContent(), culture: null),
            "GET", "https", "example.test", "/home.md",
            settings: new LlmsTxtSettings { CachePolicySeconds = 120 });

        await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(controller.Response.Headers["Cache-Control"].ToString(),
            Is.EqualTo("public, max-age=120"));
    }

    [Test]
    public async Task Render_Found_SetsVaryAcceptHeader()
    {
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n")),
            MakeStubResolver(content: BuildContent(), culture: null),
            "GET", "https", "example.test", "/home.md");

        await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(controller.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
    }

    [Test]
    public async Task Render_Found_SetsETagHeader_QuotedStrong()
    {
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n")),
            MakeStubResolver(content: BuildContent(), culture: null),
            "GET", "https", "example.test", "/home.md");

        await controller.Render(path: "/home.md", CancellationToken.None);

        var etag = controller.Response.Headers["ETag"].ToString();
        Assert.That(etag, Does.StartWith("\""));
        Assert.That(etag, Does.EndWith("\""));
        Assert.That(etag, Does.Not.StartWith("W/"), "Story 1.2 emits strong validators only");
    }

    [Test]
    public async Task Render_Found_SameInputs_ProducesSameETag()
    {
        var first = await RunAndCaptureETag("/home.md", "en-GB", HomeUpdated);
        var second = await RunAndCaptureETag("/home.md", "en-GB", HomeUpdated);
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public async Task Render_Found_DifferentCulture_DifferentETag()
    {
        var en = await RunAndCaptureETag("/home.md", "en-GB", HomeUpdated);
        var fr = await RunAndCaptureETag("/home.md", "fr-FR", HomeUpdated);
        Assert.That(fr, Is.Not.EqualTo(en));
    }

    [Test]
    public async Task Render_Found_DifferentUpdateDate_DifferentETag()
    {
        var t1 = await RunAndCaptureETag("/home.md", "en-GB", HomeUpdated);
        var t2 = await RunAndCaptureETag("/home.md", "en-GB", HomeUpdated.AddSeconds(1));
        Assert.That(t2, Is.Not.EqualTo(t1));
    }

    [Test]
    public async Task Render_Found_NullCulture_SameETag_AsEmptyCultureString()
    {
        var nullCult = await RunAndCaptureETag("/home.md", null, HomeUpdated);
        var emptyCult = await RunAndCaptureETag("/home.md", string.Empty, HomeUpdated);
        Assert.That(emptyCult, Is.EqualTo(nullCult));
    }

    [Test]
    public async Task Render_IfNoneMatch_Matches_Returns304_NoBody()
    {
        var etag = await RunAndCaptureETag("/home.md", "en-GB", HomeUpdated);

        var body = new MemoryStream();
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n", culture: "en-GB", updatedUtc: HomeUpdated)),
            MakeStubResolver(content: BuildContent(), culture: "en-GB"),
            "GET", "https", "example.test", "/home.md", body: body);
        controller.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = etag;

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        Assert.That(controller.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
        Assert.That(body.Length, Is.EqualTo(0), "304 must have no body");
        // RFC 7232 § 4.1 — ETag, Cache-Control, Vary preserved on 304.
        Assert.That(controller.Response.Headers["ETag"].ToString(), Is.EqualTo(etag));
        Assert.That(controller.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
        Assert.That(controller.Response.Headers["Cache-Control"].ToString(), Does.StartWith("public, max-age="));
        // No Content-Type on 304; X-Markdown-Tokens is a 200-only header.
        Assert.That(controller.Response.ContentType, Is.Null);
        Assert.That(controller.Response.Headers.ContainsKey(Constants.HttpHeaders.XMarkdownTokens), Is.False);
    }

    [Test]
    public async Task Render_IfNoneMatch_Mismatch_Returns200()
    {
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n")),
            MakeStubResolver(content: BuildContent(), culture: null),
            "GET", "https", "example.test", "/home.md");
        controller.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = "\"some-other-tag\"";

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        Assert.That(controller.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task Render_IfNoneMatch_Malformed_Returns200()
    {
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n")),
            MakeStubResolver(content: BuildContent(), culture: null),
            "GET", "https", "example.test", "/home.md");
        controller.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = "garbage-no-quotes";

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        Assert.That(controller.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task Render_IfNoneMatch_Wildcard_Returns304()
    {
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n")),
            MakeStubResolver(content: BuildContent(), culture: null),
            "GET", "https", "example.test", "/home.md");
        controller.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = "*";

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        Assert.That(controller.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
    }

    [Test]
    public async Task Render_IfNoneMatch_AcceptsWeakValidatorPrefix()
    {
        // Strong-vs-weak comparison aside (RFC 7232 § 2.3.2), client-sent W/" prefix
        // is tolerated by stripping it for comparison — we only emit strong validators
        // ourselves, and matching loosely on input is the conservative-on-input rule.
        var etag = await RunAndCaptureETag("/home.md", "en-GB", HomeUpdated);
        var weakened = "W/" + etag;

        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n", culture: "en-GB", updatedUtc: HomeUpdated)),
            MakeStubResolver(content: BuildContent(), culture: "en-GB"),
            "GET", "https", "example.test", "/home.md");
        controller.Request.Headers[Constants.HttpHeaders.IfNoneMatch] = weakened;

        await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(controller.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
    }

    [Test]
    public async Task Render_CachePolicySeconds_Zero_StillEmitsCacheControl()
    {
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n")),
            MakeStubResolver(content: BuildContent(), culture: null),
            "GET", "https", "example.test", "/home.md",
            settings: new LlmsTxtSettings { CachePolicySeconds = 0 });

        await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(controller.Response.Headers["Cache-Control"].ToString(),
            Is.EqualTo("public, max-age=0"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private async Task<string> RunAndCaptureETag(string path, string? culture, DateTime updatedUtc)
    {
        var controller = MakeController(
            new StubExtractor(BuildFound("# x\n", culture: culture, updatedUtc: updatedUtc)),
            MakeStubResolver(content: BuildContent(), culture: culture),
            "GET", "https", "example.test", path);

        await controller.Render(path: path, CancellationToken.None);

        return controller.Response.Headers["ETag"].ToString();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 3.1 — exclusion check
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_ExcludedByDoctypeAlias_Returns404_ExtractorNotInvoked()
    {
        // Story 3.1 AC4 — page's ContentType.Alias is in resolved exclusion list
        // → 404 + extractor never runs.
        var extractor = new StubExtractor(BuildFound("never reached"));
        var resolver = MakeStubResolver(content: BuildContent(contentTypeAlias: "redirectPage"), culture: "en-GB");
        var settingsResolver = BuildSettingsResolverWithExcludedAliases("redirectPage");
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/redirected.md",
            settingsResolverOverride: settingsResolver);

        var result = await controller.Render(path: "/redirected.md", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFoundResult>(),
                "excluded doctype must produce 404");
            Assert.That(extractor.WasCalled, Is.False,
                "extractor must not run when page's doctype is excluded");
        });
    }

    [Test]
    public async Task Render_ExcludedByPerPageBoolean_Returns404_ExtractorNotInvoked()
    {
        // Story 3.1 AC4 — page's excludeFromLlmExports composition property
        // is true → 404 + extractor never runs (regardless of resolved aliases).
        var extractor = new StubExtractor(BuildFound("never reached"));
        var resolver = MakeStubResolver(
            content: BuildContent(contentTypeAlias: "homePage", excludeFromLlmExports: true),
            culture: "en-GB");
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/home.md");

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFoundResult>(),
                "excludeFromLlmExports=true must produce 404");
            Assert.That(extractor.WasCalled, Is.False,
                "extractor must not run when page is excluded by per-page bool");
        });
    }

    [Test]
    public async Task Render_ExcludedAliasCaseInsensitive_Excluded()
    {
        // Story 3.1 AC4 — alias matching is case-insensitive. The resolved
        // exclusion list is a HashSet<string>(StringComparer.OrdinalIgnoreCase),
        // but MarkdownController consumes it via Enumerable.Contains with an
        // explicit OrdinalIgnoreCase argument — pin that the explicit comparer
        // is wired so a future refactor that drops the comparer arg breaks the
        // test (and not silently regresses casing).
        var extractor = new StubExtractor(BuildFound("never reached"));
        var resolver = MakeStubResolver(content: BuildContent(contentTypeAlias: "blogPost"), culture: "en-GB");
        // Resolved set carries the alias in DIFFERENT casing than the page's
        // ContentType.Alias. Editor entered "BlogPost"; doctype alias is "blogPost".
        var settingsResolver = BuildSettingsResolverWithExcludedAliases("BlogPost");
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/blog/post-1.md",
            settingsResolverOverride: settingsResolver);

        var result = await controller.Render(path: "/blog/post-1.md", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFoundResult>(),
                "case-insensitive alias match must produce 404");
            Assert.That(extractor.WasCalled, Is.False,
                "extractor must not run when alias matches case-insensitively");
        });
    }

    [Test]
    public async Task Render_ResolverThrows_FailsOpenAndExtracts()
    {
        // Story 3.1 § Failure & Edge Cases — `ILlmsSettingsResolver.ResolveAsync`
        // throwing must NOT 500 the route. MarkdownController catches and treats
        // the page as not-excluded (fail-open) so a transient resolver fault
        // doesn't blackhole every Markdown request.
        var extractor = new StubExtractor(BuildFound("# Home\n"));
        var resolver = MakeStubResolver(content: BuildContent(contentTypeAlias: "homePage"), culture: "en-GB");
        var throwingResolver = Substitute.For<ILlmsSettingsResolver>();
        throwingResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResolvedLlmsSettings>>(_ => throw new InvalidOperationException("simulated resolver fault"));
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/home.md",
            settingsResolverOverride: throwingResolver);

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<EmptyResult>(),
                "resolver throw must fail-open: page extracts as if not excluded");
            Assert.That(extractor.WasCalled, Is.True,
                "extractor must run when resolver throws (fail-open)");
            Assert.That(controller.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        });
    }

    [Test]
    public async Task Render_PageWithoutCompositionProperty_NotExcluded_ContinuesToExtract()
    {
        // Defensive: pages whose doctype DOES NOT include the composition return
        // null from GetProperty("excludeFromLlmExports"). Treat as not-excluded.
        var extractor = new StubExtractor(BuildFound("# Home\n"));
        var resolver = MakeStubResolver(
            content: BuildContent(contentTypeAlias: "homePage", excludeFromLlmExports: false),
            culture: "en-GB");
        var body = new MemoryStream();
        var controller = MakeController(extractor, resolver, "GET", "https", "example.test", "/home.md", body: body);

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<EmptyResult>(),
                "page without composition property must continue to extraction");
            Assert.That(extractor.WasCalled, Is.True,
                "extractor must run when page is not excluded");
            Assert.That(controller.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        });
    }

    private static MarkdownExtractionResult BuildFound(
        string body,
        string? culture = "en-GB",
        DateTime? updatedUtc = null)
        => MarkdownExtractionResult.Found(
            markdown: "---\ntitle: Home\nurl: https://example.test/home\nupdated: 2026-04-29T00:00:00Z\n---\n\n" + body,
            contentKey: HomeKey,
            culture: culture ?? string.Empty,
            updatedUtc: updatedUtc ?? HomeUpdated,
            sourceUrl: "https://example.test/home");

    private static IPublishedContent BuildContent(string contentTypeAlias = "homePage", bool excludeFromLlmExports = false)
    {
        var content = Substitute.For<IPublishedContent>();
        content.Key.Returns(HomeKey);
        content.Name.Returns("Home");
        content.UpdateDate.Returns(HomeUpdated);
        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns(contentTypeAlias);
        content.ContentType.Returns(contentType);

        if (excludeFromLlmExports)
        {
            var prop = Substitute.For<IPublishedProperty>();
            prop.HasValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns(true);
            prop.GetValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns(true);
            content.GetProperty("excludeFromLlmExports").Returns(prop);
        }
        else
        {
            content.GetProperty(Arg.Any<string>()).Returns((IPublishedProperty?)null);
        }
        return content;
    }

    private static StubResolver MakeStubResolver(IPublishedContent content, string? culture)
        => new(MarkdownRouteResolution.Found(content, culture));

    private static StubResolver StubResolverNotFound()
        => new(MarkdownRouteResolution.NotFound());

    private static ILlmsSettingsResolver BuildDefaultSettingsResolver(LlmsTxtSettings settings)
    {
        var sub = Substitute.For<ILlmsSettingsResolver>();
        sub.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(settings.ToResolved()));
        return sub;
    }

    private static ILlmsSettingsResolver BuildSettingsResolverWithExcludedAliases(params string[] aliases)
    {
        var resolved = new ResolvedLlmsSettings(
            SiteName: null,
            SiteSummary: null,
            ExcludedDoctypeAliases: new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase),
            BaseSettings: new LlmsTxtSettings());
        var sub = Substitute.For<ILlmsSettingsResolver>();
        sub.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(resolved));
        return sub;
    }

    private static MarkdownController MakeController(
        IMarkdownContentExtractor extractor,
        IMarkdownRouteResolver resolver,
        string method,
        string scheme,
        string host,
        string path,
        LlmsTxtSettings? settings = null,
        MemoryStream? body = null,
        ILlmsSettingsResolver? settingsResolverOverride = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString(host);
        httpContext.Request.Path = path;
        if (body is not null)
        {
            httpContext.Response.Body = body;
        }

        var resolvedSettings = settings ?? new LlmsTxtSettings();
        var optionsMonitor = Substitute.For<IOptionsMonitor<LlmsTxtSettings>>();
        optionsMonitor.CurrentValue.Returns(resolvedSettings);
        // Real writer — Story 1.3 keeps the .md route + Accept-negotiation paths in
        // sync by routing both through the same response writer; controller tests
        // exercise the writer end-to-end rather than mocking it.
        var writer = new MarkdownResponseWriter(optionsMonitor);


        // Story 3.1 — settings resolver substitute returns appsettings-only
        // overlay so existing tests stay green; tests that exercise exclusion
        // pass an override via settingsResolverOverride.
        var settingsResolver = settingsResolverOverride ?? BuildDefaultSettingsResolver(resolvedSettings);

        var controller = new MarkdownController(
            extractor,
            resolver,
            writer,
            settingsResolver,
            NullLogger<MarkdownController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
        return controller;
    }

    private sealed class StubExtractor : IMarkdownContentExtractor
    {
        private readonly MarkdownExtractionResult _result;
        public bool WasCalled { get; private set; }
        public Action<IPublishedContent, string?>? OnCalled { get; init; }

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
            OnCalled?.Invoke(content, culture);
            return Task.FromResult(_result);
        }
    }

    private sealed class StubResolver : IMarkdownRouteResolver
    {
        private readonly MarkdownRouteResolution _result;
        public int CallCount { get; private set; }
        public Action<Uri>? OnCalled { get; init; }

        public StubResolver(MarkdownRouteResolution result)
        {
            _result = result;
        }

        public Task<MarkdownRouteResolution> ResolveAsync(Uri absoluteUri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            OnCalled?.Invoke(absoluteUri);
            return Task.FromResult(_result);
        }
    }
}
