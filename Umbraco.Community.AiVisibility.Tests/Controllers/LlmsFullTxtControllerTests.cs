using System.Text;
using LlmsTxt.Umbraco.Builders;
using LlmsTxt.Umbraco.Caching;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Controllers;
using LlmsTxt.Umbraco.Tests.TestHelpers;
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
    private ILlmsSettingsResolver _settingsResolver = null!;
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

        // Story 3.1 — default resolver substitute returns appsettings-only overlay.
        _settingsResolver = Substitute.For<ILlmsSettingsResolver>();
        _settingsResolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_currentSettings.ToResolved()));

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

    private LlmsFullTxtController MakeController(
        string requestHost = Host,
        LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher? notificationPublisher = null,
        string method = "GET")
    {
        var ctrl = new LlmsFullTxtController(
            _builder,
            _resolver,
            _settingsResolver,
            _umbracoContextFactory,
            _navigation,
            _appCaches,
            _settings,
            notificationPublisher ?? Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>(),
            NullLogger<LlmsFullTxtController>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Method = method;
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
    public async Task Render_EmitsETagOnSuccess()
    {
        // Story 2.3 — picks up the ETag work Story 2.2 explicitly deferred.
        // Replaces Story 2.2-era `Render_DoesNotEmitETagInThisStory`.
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n\n_Source: x_\n\nBody.";
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrl = MakeController();

        await ctrl.Render(CancellationToken.None);

        var etag = ctrl.Response.Headers["ETag"].ToString();
        Assert.That(etag, Is.Not.Empty, "ETag header is now emitted (Story 2.3 AC1+AC6)");
        Assert.That(etag, Is.EqualTo(LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body)));
    }

    [Test]
    public async Task Render_CacheHit_BuilderNotInvoked()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# CachedAcme\n\n_Source: x_\n\nBody.";
        _appCaches.RuntimeCache.Insert(
            LlmsCacheKeys.LlmsFull(Host, Culture),
            () => new LlmsTxt.Umbraco.Caching.ManifestCacheEntry(
                body,
                LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body)),
            TimeSpan.FromMinutes(5));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        await _builder.DidNotReceive().BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>());
        ctrl.Response.Body.Position = 0;
        var responseBody = await new StreamReader(ctrl.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(responseBody, Does.Contain("# CachedAcme"));
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

    private static IPublishedContent StubContent(string name, string contentTypeAlias, bool excludeFromLlmExports = false)
    {
        var c = Substitute.For<IPublishedContent>();
        c.Name.Returns(name);
        c.Key.Returns(Guid.NewGuid());
        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns(contentTypeAlias);
        c.ContentType.Returns(contentType);
        if (excludeFromLlmExports)
        {
            var prop = Substitute.For<IPublishedProperty>();
            prop.HasValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns(true);
            prop.GetValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns(true);
            c.GetProperty("excludeFromLlmExports").Returns(prop);
        }
        else
        {
            c.GetProperty(Arg.Any<string>()).Returns((IPublishedProperty?)null);
        }
        return c;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 2.3 — If-None-Match / 304 / hreflang isolation / single-flight
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_IfNoneMatchMatchesCurrentETag_Returns304NoBody()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n\n_Source: x_\n\nBody.";
        var etag = LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body);
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrl = MakeController();
        ctrl.Request.Headers["If-None-Match"] = etag;

        await ctrl.Render(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
            Assert.That(ctrl.Response.ContentType, Is.Null);
            Assert.That(ctrl.Response.Headers["ETag"].ToString(), Is.EqualTo(etag));
            Assert.That(ctrl.Response.Headers["Cache-Control"].ToString(), Is.EqualTo("public, max-age=300"));
            Assert.That(ctrl.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
            Assert.That(ctrl.Response.Body.Length, Is.Zero);
        });
    }

    [Test]
    public async Task Render_IfNoneMatchStaleETag_Returns200WithCurrentBody()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));
        var ctrl = MakeController();
        ctrl.Request.Headers["If-None-Match"] = "\"stale\"";

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task Render_HreflangSettings_NotForwarded_FullManifestUnaffected()
    {
        // AC3 last bullet: hreflang is /llms.txt-only. /llms-full.txt is
        // hreflang-blind by design. Two complementary guards:
        //
        //   (1) Structural: LlmsFullBuilderContext has no HreflangVariants
        //       member — the controller has no place to forward variants even
        //       if it tried. This is the strong pin; it would fail at the
        //       type-system level if a future change added the field.
        //
        //   (2) Behavioural: byte-equality of the response body across
        //       Hreflang.Enabled = true/false. Trivially holds with a mocked
        //       builder, but still guards against the controller injecting
        //       hreflang state through some other surface (logger, headers,
        //       cache key) that would diverge the response.
        Assert.That(
            typeof(LlmsFullBuilderContext).GetProperty("HreflangVariants"),
            Is.Null,
            "AC3 last bullet — LlmsFullBuilderContext must NOT carry HreflangVariants. "
            + "If this fails, the /llms-full.txt boundary has leaked hreflang state.");

        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n\n_Source: x_\n\nBody.";
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));

        // Run with Hreflang OFF
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = new LlmsFullBuilderSettings { CachePolicySeconds = 300 },
            Hreflang = new HreflangSettings { Enabled = false },
        };
        _settings.CurrentValue.Returns(_ => _currentSettings);
        var ctrlOff = MakeController();
        await ctrlOff.Render(CancellationToken.None);
        ctrlOff.Response.Body.Position = 0;
        var bodyOff = await new StreamReader(ctrlOff.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var etagOff = ctrlOff.Response.Headers["ETag"].ToString();

        // Force cache evict so the second request rebuilds (we want byte equality
        // against a fresh build, not a cached entry from the first call).
        _appCaches.RuntimeCache.ClearByKey(LlmsCacheKeys.LlmsFull(Host, Culture));

        // Run with Hreflang ON
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = new LlmsFullBuilderSettings { CachePolicySeconds = 300 },
            Hreflang = new HreflangSettings { Enabled = true },
        };
        var ctrlOn = MakeController();
        await ctrlOn.Render(CancellationToken.None);
        ctrlOn.Response.Body.Position = 0;
        var bodyOn = await new StreamReader(ctrlOn.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var etagOn = ctrlOn.Response.Headers["ETag"].ToString();

        Assert.Multiple(() =>
        {
            Assert.That(bodyOn, Is.EqualTo(bodyOff),
                "/llms-full.txt is hreflang-blind regardless of Hreflang.Enabled");
            Assert.That(etagOn, Is.EqualTo(etagOff),
                "ETag is body-derived; identical bodies must emit identical ETags");
        });
    }

    [Test]
    public async Task Render_EmptyManifest_StillEmitsETag()
    {
        // Story 2.2 empty-manifest path (scope rejects everything → 200 + empty
        // body). Story 2.3 still emits an ETag for the empty body — SHA-256("")
        // is well-defined.
        var root = StubContent("Acme", "homePage");
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = new LlmsFullBuilderSettings { CachePolicySeconds = 300 },
            LlmsFullScope = new LlmsFullScopeSettings
            {
                IncludedDocTypeAliases = new[] { "no-such-type" },
            },
        };
        _settings.CurrentValue.Returns(_ => _currentSettings);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(string.Empty));
        var ctrl = MakeController();

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctrl.Response.Headers["ETag"].ToString(),
            Is.EqualTo(LlmsTxt.Umbraco.Caching.ManifestETag.Compute(string.Empty)),
            "empty manifest emits ETag of zero-byte hash");
    }

    [Test]
    public async Task Render_ConcurrentMissesOnSameKey_BuilderInvokedOnce()
    {
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Arg.Any<string>(), _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));

        var callCount = 0;
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Delay(50);
                return "# Acme\n\n_Source: x_\n\nBody.";
            });

        const int parallel = 8;
        var tasks = new Task[parallel];
        for (var i = 0; i < parallel; i++)
        {
            var ctrl = MakeController();
            tasks[i] = ctrl.Render(CancellationToken.None);
        }
        await Task.WhenAll(tasks);

        Assert.That(callCount, Is.EqualTo(1),
            "single-flight: only one builder invocation per key per instance under concurrent miss");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 3.1 — exclusion filter + resolver overlay + resolver-throw
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_ExcludedPagesFiltered_OmittedFromManifestBuilderInput()
    {
        // Story 3.1 AC4 — pages whose ContentType.Alias is in resolved
        // ExcludedDoctypeAliases OR whose excludeFromLlmExports = true must
        // be filtered before the builder sees them. Cumulates with the
        // existing LlmsFullScope filter (Story 2.2): logical AND-NOT.
        var root = StubContent("Acme", "homePage");
        var includedA = StubContent("Page-A", "blogPost");
        var excludedByResolverAlias = StubContent("Page-B", "redirectPage");
        var includedC = StubContent("Page-C", "blogPost");
        var excludedByBool = StubContent("Page-D", "blogPost", excludeFromLlmExports: true);

        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _navigation.TryGetDescendantsKeys(root.Key, out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[1] = new[] { includedA.Key, excludedByResolverAlias.Key, includedC.Key, excludedByBool.Key };
            return true;
        });
        _publishedSnapshot.GetById(includedA.Key).Returns(includedA);
        _publishedSnapshot.GetById(excludedByResolverAlias.Key).Returns(excludedByResolverAlias);
        _publishedSnapshot.GetById(includedC.Key).Returns(includedC);
        _publishedSnapshot.GetById(excludedByBool.Key).Returns(excludedByBool);

        // Resolver returns "redirectPage" in exclusion list (top-level Story 3.1).
        var resolvedRecord = new ResolvedLlmsSettings(
            SiteName: null, SiteSummary: null,
            ExcludedDoctypeAliases: new HashSet<string>(new[] { "redirectPage" }, StringComparer.OrdinalIgnoreCase),
            BaseSettings: _currentSettings);
        _settingsResolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(resolvedRecord));

        // Configure LlmsFullScope to NOT exclude redirectPage so we know the
        // resolver's exclusion list is doing the work (default scope excludes it).
        _currentSettings = new LlmsTxtSettings
        {
            LlmsFullBuilder = new LlmsFullBuilderSettings { CachePolicySeconds = 300 },
            LlmsFullScope = new LlmsFullScopeSettings
            {
                ExcludedDocTypeAliases = Array.Empty<string>(),
            },
        };

        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));

        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        var calls = _builder.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "BuildAsync")
            .ToArray();
        Assert.That(calls, Has.Length.EqualTo(1));
        var ctx = (LlmsFullBuilderContext)calls[0].GetArguments()[0]!;
        var pageNames = ctx.Pages.Select(p => p.Name).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(pageNames, Has.Member("Acme"));
            Assert.That(pageNames, Has.Member("Page-A"));
            Assert.That(pageNames, Has.Member("Page-C"));
            Assert.That(pageNames, Has.No.Member("Page-B"), "redirectPage filtered by resolver exclusion");
            Assert.That(pageNames, Has.No.Member("Page-D"), "blogPost with excludeFromLlmExports=true filtered");
        });
    }

    [Test]
    public async Task Render_ResolverThrows_FallsBackToAppsettings_StillReturns200()
    {
        // Story 3.1 — same graceful-degradation contract as LlmsTxtController.
        _settingsResolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResolvedLlmsSettings>>(_ => throw new InvalidOperationException("resolver boom"));

        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));

        var ctrl = MakeController();
        var result = await ctrl.Render(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<EmptyResult>());
            Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 5.1 — publication-site pinning (Task 9.5) + D1 (HEAD bytes-served)
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_Success_PublishesLlmsFullTxtNotification_WithUtf8ByteCount()
    {
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n\n_Source: x_\n\nBody.";
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrl = MakeController(notificationPublisher: publisher);

        await ctrl.Render(CancellationToken.None);

        var expectedBytes = Encoding.UTF8.GetByteCount(body);
        await publisher.Received(1).PublishLlmsFullTxtAsync(
            Arg.Any<HttpContext>(),
            Arg.Is<string>(h => h == LlmsCacheKeys.NormaliseHost(Host)),
            Arg.Is<string?>(c => c == Culture),
            Arg.Is<int>(b => b == expectedBytes),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Render_HeadRequest_PublishesWithBytesServedZero()
    {
        // Decision D1 (Story 5.1 code review): HEAD writes no body, so
        // BytesServed reports 0 (matches notification xmldoc — "actually
        // written to the response").
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n\n_Source: x_\n\nBody."));
        var ctrl = MakeController(notificationPublisher: publisher, method: "HEAD");

        await ctrl.Render(CancellationToken.None);

        await publisher.Received(1).PublishLlmsFullTxtAsync(
            Arg.Any<HttpContext>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Is<int>(b => b == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Render_NoResolvableRoot_DoesNotPublish()
    {
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        _resolver.Resolve(Arg.Any<string>(), _umbracoContext).Returns(HostnameRootResolution.NotFound());
        var ctrl = MakeController(notificationPublisher: publisher);

        await ctrl.Render(CancellationToken.None);

        await publisher.DidNotReceiveWithAnyArgs().PublishLlmsFullTxtAsync(
            default!, default!, default, default, default);
    }

    [Test]
    public async Task Render_BuilderThrows_DoesNotPublish()
    {
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var ctrl = MakeController(notificationPublisher: publisher);

        await ctrl.Render(CancellationToken.None);

        await publisher.DidNotReceiveWithAnyArgs().PublishLlmsFullTxtAsync(
            default!, default!, default, default, default);
    }

    [Test]
    public async Task Render_IfNoneMatchMatches_Returns304_DoesNotPublish()
    {
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        var root = StubContent("Acme", "homePage");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n\n_Source: x_\n\nBody.";
        var etag = LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body);
        _builder.BuildAsync(Arg.Any<LlmsFullBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrl = MakeController(notificationPublisher: publisher);
        ctrl.Request.Headers["If-None-Match"] = etag;

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
        await publisher.DidNotReceiveWithAnyArgs().PublishLlmsFullTxtAsync(
            default!, default!, default, default, default);
    }
}
