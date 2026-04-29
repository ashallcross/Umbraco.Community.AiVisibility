using LlmsTxt.Umbraco.Controllers;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmsTxt.Umbraco.Tests.Controllers;

[TestFixture]
public class MarkdownControllerTests
{
    [Test]
    public async Task Render_PathResolves_Returns200_WithMarkdownContentType()
    {
        var extractor = new StubExtractor(MarkdownExtractionResult.Found(
            markdown: "---\ntitle: Home\nurl: https://example.test/home\nupdated: 2026-04-29T00:00:00Z\n---\n\n# Home\n",
            contentKey: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            culture: "en-GB",
            updatedUtc: new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc),
            sourceUrl: "https://example.test/home"));

        var controller = MakeController(extractor, "GET", "https", "example.test", "/home.md");

        var result = await controller.Render(path: "/home.md", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ContentResult>());
        var content = (ContentResult)result;
        Assert.That(content.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(content.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType));
        Assert.That(content.Content, Does.Contain("# Home"));

        Assert.That(controller.Response.Headers.ContainsKey(Constants.HttpHeaders.XMarkdownTokens), Is.True);
        // No Vary, Cache-Control, ETag, or Link header — all deferred to later stories.
        Assert.That(controller.Response.Headers.ContainsKey("Vary"), Is.False);
        Assert.That(controller.Response.Headers.ContainsKey("Cache-Control"), Is.False);
        Assert.That(controller.Response.Headers.ContainsKey("ETag"), Is.False);
        Assert.That(controller.Response.Headers.ContainsKey("Link"), Is.False, "AC9 — no rel=canonical link header");
    }

    [Test]
    public async Task Render_NotFound_Returns404()
    {
        var extractor = new StubExtractor(MarkdownExtractionResult.NotFound("/does-not-exist"));
        var controller = MakeController(extractor, "GET", "https", "example.test", "/does-not-exist.md");

        var result = await controller.Render(path: "/does-not-exist.md", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Render_ExtractorError_Returns500ProblemDetails()
    {
        var extractor = new StubExtractor(MarkdownExtractionResult.Failed(
            new InvalidOperationException("boom"),
            sourceUrl: "https://example.test/buggy",
            contentKey: Guid.Parse("22222222-2222-2222-2222-222222222222")));

        var controller = MakeController(extractor, "GET", "https", "example.test", "/buggy.md");

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
        var extractor = new StubExtractor(MarkdownExtractionResult.NotFound("never called"));
        var controller = MakeController(extractor, "GET", "https", "example.test", "/something.html");

        var result = await controller.Render(path: "/something.html", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
        Assert.That(extractor.WasCalled, Is.False, "extractor must not be invoked when path normaliser rejects input");
    }

    [Test]
    public async Task Render_BuildsAbsoluteUriFromRequestSchemeAndHost()
    {
        Uri? capturedUri = null;
        var extractor = new StubExtractor(MarkdownExtractionResult.NotFound("/x"))
        {
            OnCalled = uri => capturedUri = uri,
        };
        var controller = MakeController(extractor, "GET", "https", "siteA.example", "/about.md");

        await controller.Render(path: "/about.md", CancellationToken.None);

        Assert.That(capturedUri, Is.Not.Null);
        // Note: the `Uri` ctor lowercases the authority component — `SiteA.Example`
        // becomes `sitea.example` automatically. We're not normalising case ourselves.
        Assert.That(capturedUri!.AbsoluteUri, Is.EqualTo("https://sitea.example/about"));
    }

    [Test]
    public async Task Render_HonoursCancellationToken()
    {
        var extractor = new StubExtractor(MarkdownExtractionResult.NotFound("/x"));
        var controller = MakeController(extractor, "GET", "https", "example.test", "/home.md");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await controller.Render(path: "/home.md", cts.Token));
    }

    [Test]
    public async Task Render_MalformedHostHeader_Returns400()
    {
        var extractor = new StubExtractor(MarkdownExtractionResult.NotFound("/x"));
        // A double-dotted host produces UriFormatException in the `Uri` ctor; should be 400 not 500.
        var controller = MakeController(extractor, "GET", "https", "my..invalid..host", "/home.md");

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
        var extractor = new StubExtractor(MarkdownExtractionResult.NotFound("/x"))
        {
            OnCalled = uri => capturedUri = uri,
        };
        var controller = MakeController(extractor, "GET", "https", "example.test", capturedPath);

        await controller.Render(path: capturedPath, CancellationToken.None);

        return capturedUri!.AbsoluteUri;
    }

    private static MarkdownController MakeController(
        IMarkdownContentExtractor extractor,
        string method,
        string scheme,
        string host,
        string path)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString(host);
        httpContext.Request.Path = path;

        var controller = new MarkdownController(extractor, NullLogger<MarkdownController>.Instance);
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = httpContext,
        };
        return controller;
    }

    private sealed class StubExtractor : IMarkdownContentExtractor
    {
        private readonly MarkdownExtractionResult _result;
        public bool WasCalled { get; private set; }
        public Action<Uri>? OnCalled { get; init; }

        public StubExtractor(MarkdownExtractionResult result)
        {
            _result = result;
        }

        public Task<MarkdownExtractionResult> ExtractAsync(Uri absoluteUri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            OnCalled?.Invoke(absoluteUri);
            return Task.FromResult(_result);
        }
    }
}
