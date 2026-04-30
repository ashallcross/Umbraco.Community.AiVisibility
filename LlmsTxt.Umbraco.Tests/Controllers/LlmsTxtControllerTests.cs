using System.Text;
using LlmsTxt.Umbraco.Builders;
using LlmsTxt.Umbraco.Caching;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace LlmsTxt.Umbraco.Tests.Controllers;

[TestFixture]
public class LlmsTxtControllerTests
{
    private const string Host = "sitea.example";
    private const string Culture = "en-gb";

    private ILlmsTxtBuilder _builder = null!;
    private IHostnameRootResolver _resolver = null!;
    private IUmbracoContextFactory _umbracoContextFactory = null!;
    private IDocumentNavigationQueryService _navigation = null!;
    private AppCaches _appCaches = null!;
    private IOptionsMonitor<LlmsTxtSettings> _settings = null!;
    private LlmsTxtSettings _currentSettings = null!;
    private IUmbracoContext _umbracoContext = null!;
    private IPublishedContentCache _publishedSnapshot = null!;

    [SetUp]
    public void Setup()
    {
        _builder = Substitute.For<ILlmsTxtBuilder>();
        _resolver = Substitute.For<IHostnameRootResolver>();
        _umbracoContextFactory = Substitute.For<IUmbracoContextFactory>();
        _navigation = Substitute.For<IDocumentNavigationQueryService>();
        _appCaches = new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache()));

        _currentSettings = new LlmsTxtSettings
        {
            LlmsTxtBuilder = new LlmsTxtBuilderSettings { CachePolicySeconds = 300 },
        };
        _settings = Substitute.For<IOptionsMonitor<LlmsTxtSettings>>();
        _settings.CurrentValue.Returns(_ => _currentSettings);

        _umbracoContext = Substitute.For<IUmbracoContext>();
        _publishedSnapshot = Substitute.For<IPublishedContentCache>();
        _umbracoContext.Content.Returns(_publishedSnapshot);
        var accessor = Substitute.For<IUmbracoContextAccessor>();
        _umbracoContextFactory
            .EnsureUmbracoContext()
            .Returns(_ => new UmbracoContextReference(_umbracoContext, isRoot: false, accessor));

        _navigation.TryGetDescendantsKeys(Arg.Any<Guid>(), out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[1] = Array.Empty<Guid>();
            return true;
        });
    }

    [TearDown]
    public void TearDown()
    {
        _appCaches.Dispose();
        _umbracoContext.Dispose();
    }

    private LlmsTxtController MakeController(string requestHost = Host)
    {
        var ctrl = new LlmsTxtController(
            _builder,
            _resolver,
            _umbracoContextFactory,
            _navigation,
            _appCaches,
            _settings,
            NullLogger<LlmsTxtController>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Scheme = "https";
        http.Request.Host = new HostString(requestHost);
        http.Request.Path = "/llms.txt";
        http.Response.Body = new MemoryStream();
        ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };
        return ctrl;
    }

    [Test]
    public async Task Render_NoResolvableRoot_Returns404ProblemDetails()
    {
        _resolver.Resolve(Arg.Any<string>(), _umbracoContext)
            .Returns(HostnameRootResolution.NotFound());
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var problem = (ObjectResult)result;
        Assert.That(problem.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        await _builder.DidNotReceive().BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Render_ResolvedRoot_BuildsManifest_AndSets200WithMarkdownContentType()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<EmptyResult>());
            Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(ctrl.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType));
            Assert.That(ctrl.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
            Assert.That(
                ctrl.Response.Headers["Cache-Control"].ToString(),
                Is.EqualTo("public, max-age=300"));
        });

        ctrl.Response.Body.Position = 0;
        var body = await new StreamReader(ctrl.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(body, Does.Contain("# Acme"));
    }

    [Test]
    public async Task Response_DoesNotEmitETagInThisStory()
    {
        // Per § Failure & Edge Cases: ETag/304/single-flight hardening is owned by
        // Story 2.3. Story 2.1 deliberately ships no ETag so clients don't issue
        // If-None-Match revalidations the server can't satisfy.
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController();

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.Headers.ContainsKey("ETag"), Is.False,
            "ETag is deferred to Story 2.3");
    }

    [Test]
    public async Task Render_CacheHit_BuilderNotInvoked()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _appCaches.RuntimeCache.Insert(
            LlmsCacheKeys.LlmsTxt(Host, Culture),
            () => "# CachedAcme\n> \n",
            TimeSpan.FromMinutes(5));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        await _builder.DidNotReceive().BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>());
        ctrl.Response.Body.Position = 0;
        var body = await new StreamReader(ctrl.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(body, Does.Contain("# CachedAcme"));
    }

    [Test]
    public async Task Render_BuilderThrows_Returns500()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var problem = (ObjectResult)result;
        Assert.That(problem.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
    }

    [Test]
    public async Task Render_BuilderReturnsEmpty_Returns500()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(string.Empty));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var problem = (ObjectResult)result;
        Assert.That(problem.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
    }

    [Test]
    public async Task Render_HeadRequest_WritesHeadersButNoBody()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController();
        ctrl.Request.Method = "HEAD";

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctrl.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType));
        Assert.That(ctrl.Response.Body.Length, Is.Zero, "HEAD must not write a body");
    }

    [Test]
    public async Task Render_AcceptHtmlOnly_StillReturnsMarkdown()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController();
        ctrl.Request.Headers["Accept"] = "text/html";

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        Assert.That(ctrl.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType),
            "/llms.txt resource type is fixed; Accept is advisory only");
        Assert.That(ctrl.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"),
            "Vary: Accept must be present even when Accept doesn't match the resource type, "
            + "so caches downstream don't serve a wrong-type entry to the next request");
    }

    [Test]
    public async Task Render_PassesPreCollectedPagesIntoBuilderContext()
    {
        var root = StubContent("Acme");
        var rootKey = root.Key;
        var childKey = Guid.NewGuid();
        var child = StubContent("Child");
        child.Key.Returns(childKey);
        _publishedSnapshot.GetById(childKey).Returns(child);
        _navigation.TryGetDescendantsKeys(rootKey, out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[1] = new[] { childKey };
            return true;
        });
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));

        LlmsTxtBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsTxtBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController();

        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Pages, Has.Count.EqualTo(2), "root + descendant");
        Assert.That(observed.Pages[0], Is.SameAs(root), "root first");
        Assert.That(observed.Pages[1], Is.SameAs(child));
        Assert.That(observed.Hostname, Is.EqualTo(Host));
        Assert.That(observed.Culture, Is.EqualTo(Culture));
    }

    private static IPublishedContent StubContent(string name)
    {
        var c = Substitute.For<IPublishedContent>();
        c.Name.Returns(name);
        c.Key.Returns(Guid.NewGuid());
        return c;
    }
}
