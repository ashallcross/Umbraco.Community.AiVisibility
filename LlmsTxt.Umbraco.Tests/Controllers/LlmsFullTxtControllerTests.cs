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
public class LlmsFullTxtControllerTests
{
    private const string Host = "sitea.example";
    private const string Culture = "en-gb";

    private ILlmsFullBuilder _builder = null!;
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
        _builder = Substitute.For<ILlmsFullBuilder>();
        _resolver = Substitute.For<IHostnameRootResolver>();
        _umbracoContextFactory = Substitute.For<IUmbracoContextFactory>();
        _navigation = Substitute.For<IDocumentNavigationQueryService>();
        _appCaches = new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache()));

        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = new LlmsFullBuilderSettings { CachePolicySeconds = 300 },
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

    private LlmsFullTxtController MakeController(string requestHost = Host)
    {
        var ctrl = new LlmsFullTxtController(
            _builder,
            _resolver,
            _umbracoContextFactory,
            _navigation,
            _appCaches,
            _settings,
            NullLogger<LlmsFullTxtController>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Scheme = "https";
        http.Request.Host = new HostString(requestHost);
        http.Request.Path = "/llms-full.txt";
        http.Response.Body = new MemoryStream();
        ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };
        return ctrl;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 404 / resolver fallback / cache / response shape
    // ────────────────────────────────────────────────────────────────────────

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
        await _builder.DidNotReceive().BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Render_ResolvedRoot_BuildsManifest_AndSets200WithMarkdownContentType()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: https://sitea.example/_\n\nBody."));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<EmptyResult>());
            Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(ctrl.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType));
            Assert.That(ctrl.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
            Assert.That(ctrl.Response.Headers["Cache-Control"].ToString(), Is.EqualTo("public, max-age=300"));
        });

        ctrl.Response.Body.Position = 0;
        var body = await new StreamReader(ctrl.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(body, Does.Contain("# Acme"));
    }

    [Test]
    public async Task Render_DoesNotEmitETagInThisStory()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));
        var ctrl = MakeController();

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.Headers.ContainsKey("ETag"), Is.False,
            "ETag is deferred to Story 2.3");
    }

    [Test]
    public async Task Render_CacheHit_BuilderNotInvoked()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _appCaches.RuntimeCache.Insert(
            LlmsCacheKeys.LlmsFull(Host, Culture),
            () => "# CachedAcme\n\n_Source: x_\n\nBody.",
            TimeSpan.FromMinutes(5));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        await _builder.DidNotReceive().BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>());
        ctrl.Response.Body.Position = 0;
        var body = await new StreamReader(ctrl.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(body, Does.Contain("# CachedAcme"));
    }

    [Test]
    public async Task Render_CacheMiss_BuilderInvokedOnce_BodyCached()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));

        var ctrl1 = MakeController();
        await ctrl1.Render(CancellationToken.None);
        var ctrl2 = MakeController();
        await ctrl2.Render(CancellationToken.None);

        await _builder.Received(1).BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Render_BuilderThrows_Returns500()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var problem = (ObjectResult)result;
        Assert.That(problem.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
    }

    [Test]
    public async Task Render_HeadRequest_WritesHeadersButNoBody()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));
        var ctrl = MakeController();
        ctrl.Request.Method = "HEAD";

        await ctrl.Render(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(ctrl.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType));
            Assert.That(ctrl.Response.Body.Length, Is.Zero, "HEAD must not write a body");
        });
    }

    [Test]
    public async Task Render_AcceptHtmlOnly_StillReturnsMarkdown()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));
        var ctrl = MakeController();
        ctrl.Request.Headers["Accept"] = "text/html";

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType),
            "/llms-full.txt resource type is fixed; Accept is advisory only");
        Assert.That(ctrl.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
    }

    [Test]
    public async Task Render_EmptyScope_Returns200WithEmptyBody()
    {
        // Builder may legitimately return an empty string when scope filtering
        // rejected every page (e.g. IncludedDocTypeAliases matches nothing). The
        // 404 path is reserved for resolver-level failures.
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(string.Empty));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        ctrl.Response.Body.Position = 0;
        var body = await new StreamReader(ctrl.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(body, Is.EqualTo(string.Empty));
    }

    [Test]
    public async Task Render_EmptyManifest_NotCached_BuilderInvokedEachRequest()
    {
        // Empty manifest usually signals a misconfig (include/exclude collision,
        // alias typo). Caching it for the full TTL would force operators to wait
        // for recovery after a fix; instead, the controller clears the cache key
        // and logs Warning so the next request re-runs the builder.
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(string.Empty));

        var ctrl1 = MakeController();
        await ctrl1.Render(CancellationToken.None);
        var ctrl2 = MakeController();
        await ctrl2.Render(CancellationToken.None);

        await _builder.Received(2).BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>());
        Assert.That(_appCaches.RuntimeCache.Get(LlmsCacheKeys.LlmsFull(Host, Culture)), Is.Null,
            "empty manifest must not persist in the runtime cache");
    }

    [Test]
    public async Task Render_CachePolicySecondsZero_BypassesCache_BuilderInvokedEachRequest()
    {
        // CachePolicySeconds = 0 disables the manifest cache entirely (matches the
        // LlmsTxtSettings.CachePolicySeconds xmldoc — "0 effectively disables
        // caching"). Builder is invoked on every request; cache stays empty.
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = new LlmsFullBuilderSettings { CachePolicySeconds = 0 },
        };
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));

        var ctrl1 = MakeController();
        await ctrl1.Render(CancellationToken.None);
        var ctrl2 = MakeController();
        await ctrl2.Render(CancellationToken.None);

        await _builder.Received(2).BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>());
        Assert.That(_appCaches.RuntimeCache.Get(LlmsCacheKeys.LlmsFull(Host, Culture)), Is.Null,
            "CachePolicySeconds = 0 must not persist anything in the runtime cache");
    }

    [Test]
    public async Task Render_CachePolicySecondsNegative_TreatedAsZero_BypassesCache()
    {
        // Negative is an operator typo; treat as 0 (cache disabled) and rely on
        // logged Warning to surface it. Mirrors MaxLlmsFullSizeKb's "≤ 0 →
        // unlimited + Warning" defensive policy (consistent settings policy).
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = new LlmsFullBuilderSettings { CachePolicySeconds = -10 },
        };
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));

        var ctrl1 = MakeController();
        await ctrl1.Render(CancellationToken.None);
        var ctrl2 = MakeController();
        await ctrl2.Render(CancellationToken.None);

        await _builder.Received(2).BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────────────────────────
    // AC2 + AC3 — scope filter (controller-side)
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_DefaultScope_BuilderInvokedWithAllPages()
    {
        var root = StubContent("Root", "homePage");
        var blog = StubContent("Blog", "blogPost");
        var article = StubContent("Article", "article");
        SeedDescendants(root.Key, blog, article);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));

        LlmsFullBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsFullBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Stub\n"));
        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Pages.Select(p => p.Name), Is.EqualTo(new[] { "Root", "Blog", "Article" }),
            "default scope = root + every descendant in tree-order, no doctype filter");
    }

    [Test]
    public async Task Render_RootContentTypeAlias_ScopeNarrows()
    {
        var root = StubContent("Root", "homePage");
        var blogLanding = StubContent("BlogLanding", "blogLanding");
        var blogPost1 = StubContent("Post 1", "blogPost");
        var aboutPage = StubContent("About", "contentPage");
        // Hostname root sees three top-level descendants. Once narrowed to
        // blogLanding, the controller restarts the descendant walk from there
        // and only sees blogPost1.
        SeedDescendants(root.Key, blogLanding, blogPost1, aboutPage);
        SeedDescendants(blogLanding.Key, blogPost1);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = _currentSettings.LlmsFullBuilder,
            LlmsFullScope = new LlmsFullScopeSettings { RootContentTypeAlias = "blogLanding" },
        };

        LlmsFullBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsFullBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Stub\n"));
        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Pages.Select(p => p.Name), Is.EqualTo(new[] { "BlogLanding", "Post 1" }),
            "narrowing to blogLanding starts the descendant walk at the BlogLanding node");
    }

    [Test]
    public async Task Render_RootContentTypeAlias_NoMatch_FallsBackToHostnameRoot()
    {
        var root = StubContent("Root", "homePage");
        var blog = StubContent("Blog", "blogPost");
        SeedDescendants(root.Key, blog);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = _currentSettings.LlmsFullBuilder,
            LlmsFullScope = new LlmsFullScopeSettings { RootContentTypeAlias = "nonExistent" },
        };

        LlmsFullBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsFullBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Stub\n"));
        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Pages.Select(p => p.Name), Is.EqualTo(new[] { "Root", "Blog" }),
            "alias matching no descendant → fall back to hostname root for the descendant walk");
    }

    [Test]
    public async Task Render_RootContentTypeAlias_Whitespace_TreatedAsNoNarrowing()
    {
        // Operator typo: alias is whitespace-only ("   "). Empty-string is the
        // documented "no narrowing" form; whitespace-only used to silently match
        // that path with no diagnostic. Now logs Warning + falls through to the
        // hostname root walk so the operator sees the misconfig.
        var root = StubContent("Root", "homePage");
        var blog = StubContent("Blog", "blogPost");
        SeedDescendants(root.Key, blog);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = _currentSettings.LlmsFullBuilder,
            LlmsFullScope = new LlmsFullScopeSettings { RootContentTypeAlias = "   " },
        };

        LlmsFullBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsFullBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Stub\n"));
        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Pages.Select(p => p.Name), Is.EqualTo(new[] { "Root", "Blog" }),
            "whitespace alias falls back to the hostname root walk (with Warning logged)");
    }

    [Test]
    public async Task Render_IncludedDocTypeAliases_FilterApplied()
    {
        var root = StubContent("Root", "homePage");
        var blog = StubContent("Blog", "blogPost");
        var about = StubContent("About", "contentPage");
        SeedDescendants(root.Key, blog, about);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = _currentSettings.LlmsFullBuilder,
            LlmsFullScope = new LlmsFullScopeSettings
            {
                IncludedDocTypeAliases = new[] { "blogPost" },
                ExcludedDocTypeAliases = Array.Empty<string>(),
            },
        };

        LlmsFullBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsFullBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Stub\n"));
        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Pages.Select(p => p.Name), Is.EqualTo(new[] { "Blog" }),
            "only blogPost survives the include filter (Root and About are dropped)");
    }

    [Test]
    public async Task Render_ExcludedDocTypeAliases_AlwaysApplied()
    {
        var root = StubContent("Root", "homePage");
        var error = StubContent("404", "errorPage");
        var about = StubContent("About", "contentPage");
        SeedDescendants(root.Key, error, about);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        // Default ExcludedDocTypeAliases includes errorPage.

        LlmsFullBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsFullBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Stub\n"));
        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Pages.Select(p => p.Name), Is.EqualTo(new[] { "Root", "About" }),
            "errorPage is dropped by the default exclude list");
    }

    [Test]
    public async Task Render_IncludedAndExcludedOverlap_ExcludedWins()
    {
        var root = StubContent("Root", "homePage");
        var error = StubContent("404", "errorPage");
        SeedDescendants(root.Key, error);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = _currentSettings.LlmsFullBuilder,
            LlmsFullScope = new LlmsFullScopeSettings
            {
                IncludedDocTypeAliases = new[] { "errorPage", "homePage" },
                ExcludedDocTypeAliases = new[] { "errorPage" },
            },
        };

        LlmsFullBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsFullBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Stub\n"));
        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.Pages.Select(p => p.Name), Is.EqualTo(new[] { "Root" }),
            "exclusion wins over inclusion when the same alias appears in both lists");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private void SeedDescendants(Guid rootKey, params IPublishedContent[] descendants)
    {
        var keys = descendants.Select(d => d.Key).ToArray();
        foreach (var d in descendants)
        {
            _publishedSnapshot.GetById(d.Key).Returns(d);
        }
        _navigation.TryGetDescendantsKeys(rootKey, out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[1] = keys;
            return true;
        });
    }

    private static IPublishedContent StubContent(string name, string contentTypeAlias)
    {
        var c = Substitute.For<IPublishedContent>();
        c.Name.Returns(name);
        c.Key.Returns(Guid.NewGuid());
        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns(contentTypeAlias);
        c.ContentType.Returns(contentType);
        return c;
    }
}
