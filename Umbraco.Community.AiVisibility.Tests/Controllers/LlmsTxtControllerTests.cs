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
public class LlmsTxtControllerTests
{
    private const string Host = "sitea.example";
    private const string Culture = "en-gb";

    private ILlmsTxtBuilder _builder = null!;
    private IHostnameRootResolver _resolver = null!;
    private IHreflangVariantsResolver _hreflangResolver = null!;
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
        _builder = Substitute.For<ILlmsTxtBuilder>();
        _resolver = Substitute.For<IHostnameRootResolver>();
        _hreflangResolver = Substitute.For<IHreflangVariantsResolver>();
        _hreflangResolver.ResolveAsync(
                Arg.Any<IReadOnlyList<IPublishedContent>>(),
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<IUmbracoContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>>(
                new Dictionary<Guid, IReadOnlyList<HreflangVariant>>()));
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

        // Story 3.1 — default resolver substitute returns appsettings-only
        // overlay (matches DefaultLlmsSettingsResolver's no-Settings-node path).
        // Per-test classes override via local Returns() when needed.
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

    private LlmsTxtController MakeController(
        string requestHost = Host,
        LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher? notificationPublisher = null)
    {
        var ctrl = new LlmsTxtController(
            _builder,
            _resolver,
            _hreflangResolver,
            _settingsResolver,
            _umbracoContextFactory,
            _navigation,
            _appCaches,
            _settings,
            notificationPublisher ?? Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>(),
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
    public async Task Render_EmitsETagOnSuccess()
    {
        // Story 2.3 — picks up the ETag work Story 2.1 explicitly deferred. ETag
        // is content-derived (SHA-256(body) → base64-url-12 → quoted strong
        // validator). Replaces the Story 2.1-era `Response_DoesNotEmitETagInThisStory`.
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n> \n";
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrl = MakeController();

        await ctrl.Render(CancellationToken.None);

        var etag = ctrl.Response.Headers["ETag"].ToString();
        Assert.That(etag, Is.Not.Empty, "ETag header is now emitted (Story 2.3 AC1+AC6)");
        Assert.That(etag, Is.EqualTo(LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body)),
            "ETag is the body-derived hash (matches ManifestETag.Compute)");
    }

    [Test]
    public async Task Render_CacheHit_BuilderNotInvoked()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# CachedAcme\n> \n";
        _appCaches.RuntimeCache.Insert(
            LlmsCacheKeys.LlmsTxt(Host, Culture),
            () => new LlmsTxt.Umbraco.Caching.ManifestCacheEntry(
                body,
                LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body)),
            TimeSpan.FromMinutes(5));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>());
        await _builder.DidNotReceive().BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>());
        ctrl.Response.Body.Position = 0;
        var responseBody = await new StreamReader(ctrl.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.That(responseBody, Does.Contain("# CachedAcme"));
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

    // ────────────────────────────────────────────────────────────────────────
    // Story 2.3 — If-None-Match / 304 / hreflang / single-flight
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_IfNoneMatchMatchesCurrentETag_Returns304NoBody()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n> \n";
        var etag = LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body);
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrl = MakeController();
        ctrl.Request.Headers["If-None-Match"] = etag;

        await ctrl.Render(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
            Assert.That(ctrl.Response.ContentType, Is.Null,
                "RFC 7232 § 4.1: 304 must NOT carry Content-Type");
            Assert.That(ctrl.Response.Headers["ETag"].ToString(), Is.EqualTo(etag),
                "304 carries the same ETag as 200");
            Assert.That(ctrl.Response.Headers["Cache-Control"].ToString(), Is.EqualTo("public, max-age=300"));
            Assert.That(ctrl.Response.Headers["Vary"].ToString(), Is.EqualTo("Accept"));
            Assert.That(ctrl.Response.Body.Length, Is.Zero, "304 must not write a body");
        });
    }

    [Test]
    public async Task Render_IfNoneMatchStaleETag_Returns200WithCurrentBody()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController();
        ctrl.Request.Headers["If-None-Match"] = "\"stale-etag\"";

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctrl.Response.ContentType, Is.EqualTo(Constants.HttpHeaders.MarkdownContentType));
    }

    [Test]
    public async Task Render_IfNoneMatchAndIfModifiedSince_HonoursIfNoneMatch()
    {
        // RFC 7232 § 6: strong validator wins. With a matching If-None-Match,
        // we 304 regardless of whether If-Modified-Since would have served 200.
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n> \n";
        var etag = LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body);
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrl = MakeController();
        ctrl.Request.Headers["If-None-Match"] = etag;
        ctrl.Request.Headers["If-Modified-Since"] = "Sun, 01 Jan 2000 00:00:00 GMT";

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
    }

    [Test]
    public async Task Render_HeadWith304_NoBody()
    {
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n> \n";
        var etag = LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body);
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrl = MakeController();
        ctrl.Request.Method = "HEAD";
        ctrl.Request.Headers["If-None-Match"] = etag;

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
        Assert.That(ctrl.Response.Body.Length, Is.Zero);
    }

    [Test]
    public async Task Render_CacheHit_EtagReused_NotReHashed()
    {
        // Stuff a precomputed entry with a sentinel ETag that doesn't match
        // ManifestETag.Compute(body); the controller MUST reuse the cached
        // ETag, not rehash on every request.
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n> \n";
        const string sentinelETag = "\"sentinel-not-real\"";
        _appCaches.RuntimeCache.Insert(
            LlmsCacheKeys.LlmsTxt(Host, Culture),
            () => new LlmsTxt.Umbraco.Caching.ManifestCacheEntry(body, sentinelETag),
            TimeSpan.FromMinutes(5));
        var ctrl = MakeController();

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.Headers["ETag"].ToString(), Is.EqualTo(sentinelETag),
            "cache hit reuses the stored ETag without re-hashing");
    }

    [Test]
    public async Task Render_HreflangEnabled_ResolvesAndPassesVariantsToBuilder()
    {
        var root = StubContent("Acme");
        _currentSettings = new LlmsTxtSettings
        {
            LlmsTxtBuilder = new LlmsTxtBuilderSettings { CachePolicySeconds = 300 },
            Hreflang = new HreflangSettings { Enabled = true },
        };
        _settings.CurrentValue.Returns(_ => _currentSettings);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        var variants = new Dictionary<Guid, IReadOnlyList<HreflangVariant>>
        {
            [root.Key] = new[] { new HreflangVariant("fr-fr", "/fr/index.html.md") },
        };
        _hreflangResolver.ResolveAsync(
                Arg.Any<IReadOnlyList<IPublishedContent>>(),
                Culture,
                root.Key,
                _umbracoContext,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>>(variants));

        LlmsTxtBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsTxtBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController();

        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.HreflangVariants, Is.Not.Null);
        Assert.That(observed.HreflangVariants![root.Key][0].Culture, Is.EqualTo("fr-fr"));
    }

    [Test]
    public async Task Render_HreflangDisabled_PassesNullVariants_ResolverNotInvoked()
    {
        var root = StubContent("Acme");
        // Default _currentSettings has Hreflang.Enabled = false
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));

        LlmsTxtBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsTxtBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController();

        await ctrl.Render(CancellationToken.None);

        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.HreflangVariants, Is.Null,
            "hreflang disabled → null passed to builder (zero-cost path)");
        await _hreflangResolver.DidNotReceive().ResolveAsync(
            Arg.Any<IReadOnlyList<IPublishedContent>>(),
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<IUmbracoContext>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Render_HreflangResolverThrows_LogsWarningAndContinues()
    {
        var root = StubContent("Acme");
        _currentSettings = new LlmsTxtSettings
        {
            LlmsTxtBuilder = new LlmsTxtBuilderSettings { CachePolicySeconds = 300 },
            Hreflang = new HreflangSettings { Enabled = true },
        };
        _settings.CurrentValue.Returns(_ => _currentSettings);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _hreflangResolver.ResolveAsync(
                Arg.Any<IReadOnlyList<IPublishedContent>>(),
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<IUmbracoContext>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>>>(_ =>
                throw new InvalidOperationException("hreflang resolver exploded"));

        LlmsTxtBuilderContext? observed = null;
        _builder.BuildAsync(Arg.Do<LlmsTxtBuilderContext>(c => observed = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController();

        var result = await ctrl.Render(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<EmptyResult>(), "manifest serves 200 — graceful degradation");
        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(observed, Is.Not.Null);
        Assert.That(observed!.HreflangVariants, Is.Null,
            "resolver exception → variants nulled out, builder runs without hreflang");
    }

    [Test]
    public async Task Render_HreflangEnabled_NoSiblingCultures_BodyByteIdenticalToDisabled()
    {
        // Spec § Failure & Edge Cases line 209 — when the flag is on but no sibling
        // cultures exist (resolver returns empty), the manifest must be
        // byte-identical to the flag-off output. The builder is mocked here, so
        // we assert two independent properties: (1) resolver IS invoked when the
        // flag is on, (2) the empty-dict variants flowing into the builder are
        // structurally equivalent to the flag-off null variants — same builder
        // output, same response body.
        const string body = "# Acme\n\n## Pages\n\n- [About](/about.md): About body\n";

        // Run 1 — flag ON, resolver returns empty dictionary.
        var rootOn = StubContent("Acme");
        _currentSettings = new LlmsTxtSettings
        {
            LlmsTxtBuilder = new LlmsTxtBuilderSettings { CachePolicySeconds = 300 },
            Hreflang = new HreflangSettings { Enabled = true },
        };
        _settings.CurrentValue.Returns(_ => _currentSettings);
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(rootOn, Culture));
        _hreflangResolver.ResolveAsync(
                Arg.Any<IReadOnlyList<IPublishedContent>>(),
                Culture,
                rootOn.Key,
                _umbracoContext,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>>(
                new Dictionary<Guid, IReadOnlyList<HreflangVariant>>(0)));

        LlmsTxtBuilderContext? observedOn = null;
        _builder.BuildAsync(Arg.Do<LlmsTxtBuilderContext>(c => observedOn = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrlOn = MakeController();
        await ctrlOn.Render(CancellationToken.None);
        ctrlOn.Response.Body.Position = 0;
        var bodyOn = await new StreamReader(ctrlOn.Response.Body, Encoding.UTF8).ReadToEndAsync();

        // Run 2 — flag OFF, resolver not invoked. Force a fresh cache miss with
        // a distinct hostname so the previous entry doesn't satisfy this run.
        var rootOff = StubContent("Acme");
        _currentSettings = new LlmsTxtSettings
        {
            LlmsTxtBuilder = new LlmsTxtBuilderSettings { CachePolicySeconds = 300 },
            Hreflang = new HreflangSettings { Enabled = false },
        };
        _resolver.Resolve("siteb.example", _umbracoContext)
            .Returns(HostnameRootResolution.Found(rootOff, Culture));
        LlmsTxtBuilderContext? observedOff = null;
        _builder.BuildAsync(Arg.Do<LlmsTxtBuilderContext>(c => observedOff = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrlOff = MakeController();
        ctrlOff.Request.Host = new HostString("siteb.example");
        await ctrlOff.Render(CancellationToken.None);
        ctrlOff.Response.Body.Position = 0;
        var bodyOff = await new StreamReader(ctrlOff.Response.Body, Encoding.UTF8).ReadToEndAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bodyOn, Is.EqualTo(bodyOff), "byte-identical bodies");
            Assert.That(observedOn, Is.Not.Null);
            Assert.That(observedOff, Is.Not.Null);
            Assert.That(
                observedOn!.HreflangVariants?.Count ?? 0,
                Is.EqualTo(0),
                "empty resolver → empty (or null) variants flow into builder");
            Assert.That(
                observedOff!.HreflangVariants,
                Is.Null,
                "flag-off → null variants (zero-cost path)");
        });
    }

    [Test]
    public async Task Render_ConcurrentMissesOnSameKey_BuilderInvokedOnce()
    {
        // Single-flight contract: IAppPolicyCache.GetCacheItem serialises factory
        // delegate per key per instance. Concurrent callers on a cold cache park
        // on the first builder invocation; only one runs.
        var root = StubContent("Acme");
        _resolver.Resolve(Arg.Any<string>(), _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));

        var callCount = 0;
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref callCount);
                // Widen the race window so the concurrent callers genuinely
                // overlap on slower CI.
                await Task.Delay(50);
                return "# Acme\n> \n";
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

    private static IPublishedContent StubContent(string name, string contentTypeAlias = "homePage", bool excludeFromLlmExports = false)
    {
        var c = Substitute.For<IPublishedContent>();
        c.Name.Returns(name);
        c.Key.Returns(Guid.NewGuid());
        var ct = Substitute.For<IPublishedContentType>();
        ct.Alias.Returns(contentTypeAlias);
        c.ContentType.Returns(ct);
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
    // Story 3.1 — exclusion filter + resolver overlay + resolver-throw
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_ExcludedPagesFiltered_OmittedFromManifestBuilderInput()
    {
        // Story 3.1 AC4 — pages whose ContentType.Alias is in resolved
        // ExcludedDoctypeAliases OR whose excludeFromLlmExports = true must
        // be filtered before the builder sees them. Pin via the builder's
        // received argument: only non-excluded pages reach it.
        var root = StubContent("Acme", contentTypeAlias: "homePage");
        var includedA = StubContent("Page-A", contentTypeAlias: "blogPost");
        var excludedByAlias = StubContent("Page-B", contentTypeAlias: "redirectPage");
        var includedC = StubContent("Page-C", contentTypeAlias: "blogPost");
        var excludedByBool = StubContent("Page-D", contentTypeAlias: "blogPost", excludeFromLlmExports: true);

        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        // Seed descendant walk
        var descendantKey1 = includedA.Key;
        var descendantKey2 = excludedByAlias.Key;
        var descendantKey3 = includedC.Key;
        var descendantKey4 = excludedByBool.Key;
        _navigation.TryGetDescendantsKeys(root.Key, out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[1] = new[] { descendantKey1, descendantKey2, descendantKey3, descendantKey4 };
            return true;
        });
        _publishedSnapshot.GetById(descendantKey1).Returns(includedA);
        _publishedSnapshot.GetById(descendantKey2).Returns(excludedByAlias);
        _publishedSnapshot.GetById(descendantKey3).Returns(includedC);
        _publishedSnapshot.GetById(descendantKey4).Returns(excludedByBool);

        // Resolver returns "redirectPage" in exclusion list.
        var resolvedRecord = new ResolvedLlmsSettings(
            SiteName: null, SiteSummary: null,
            ExcludedDoctypeAliases: new HashSet<string>(new[] { "redirectPage" }, StringComparer.OrdinalIgnoreCase),
            BaseSettings: _currentSettings);
        _settingsResolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(resolvedRecord));

        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));

        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        // Inspect the LlmsTxtBuilderContext passed to the builder.
        var calls = _builder.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "BuildAsync")
            .ToArray();
        Assert.That(calls, Has.Length.EqualTo(1));
        var ctx = (LlmsTxtBuilderContext)calls[0].GetArguments()[0]!;
        var pageNames = ctx.Pages.Select(p => p.Name).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(pageNames, Has.Member("Acme"), "root must be included");
            Assert.That(pageNames, Has.Member("Page-A"), "blogPost (not in exclusion list, no per-page bool) included");
            Assert.That(pageNames, Has.Member("Page-C"), "second non-excluded blogPost included");
            Assert.That(pageNames, Has.No.Member("Page-B"), "redirectPage filtered out by alias exclusion");
            Assert.That(pageNames, Has.No.Member("Page-D"), "blogPost with excludeFromLlmExports=true filtered out");
        });
    }

    [Test]
    public async Task Render_ResolvedSiteName_OverridesAppsettings_PassedToBuilder()
    {
        // Story 3.1 AC3 — resolver overlay's SiteName/SiteSummary reach the builder
        // via LlmsTxtBuilderContext.Settings (ResolvedLlmsSettings).
        _currentSettings = new LlmsTxtSettings
        {
            SiteName = "Default Acme",
            SiteSummary = "Default summary",
            LlmsTxtBuilder = new LlmsTxtBuilderSettings { CachePolicySeconds = 300 },
        };
        var resolvedRecord = new ResolvedLlmsSettings(
            SiteName: "Acme Docs",            // doctype overlay
            SiteSummary: "Acme product docs", // doctype overlay
            ExcludedDoctypeAliases: new HashSet<string>(),
            BaseSettings: _currentSettings);
        _settingsResolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(resolvedRecord));

        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme Docs\n> Acme product docs\n"));

        var ctrl = MakeController();
        await ctrl.Render(CancellationToken.None);

        var calls = _builder.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "BuildAsync")
            .ToArray();
        var ctx = (LlmsTxtBuilderContext)calls[0].GetArguments()[0]!;

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Settings.SiteName, Is.EqualTo("Acme Docs"),
                "resolver-overlaid SiteName reaches the builder context");
            Assert.That(ctx.Settings.SiteSummary, Is.EqualTo("Acme product docs"));
            Assert.That(ctx.Settings.BaseSettings.SiteName, Is.EqualTo("Default Acme"),
                "appsettings snapshot still accessible via BaseSettings");
        });
    }

    [Test]
    public async Task Render_ResolverThrows_FallsBackToAppsettings_StillReturns200()
    {
        // Story 3.1 — resolver-throw graceful degradation. Same shape as Story 2.3
        // hreflang resolver-throw. Manifest STILL builds, using the appsettings
        // snapshot only (no doctype overlay, no exclusion list).
        _currentSettings = new LlmsTxtSettings
        {
            SiteName = "Fallback Acme",
            LlmsTxtBuilder = new LlmsTxtBuilderSettings { CachePolicySeconds = 300 },
        };
        _settingsResolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResolvedLlmsSettings>>(_ => throw new InvalidOperationException("resolver boom"));

        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Fallback Acme\n> \n"));

        var ctrl = MakeController();
        var result = await ctrl.Render(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<EmptyResult>(),
                "resolver throw must NOT 500 — graceful degradation to appsettings");
            Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        });

        // Verify the builder received a context built from appsettings only
        // (BaseSettings.SiteName is the appsettings value; SiteName mirrors it).
        var calls = _builder.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "BuildAsync")
            .ToArray();
        var ctx = (LlmsTxtBuilderContext)calls[0].GetArguments()[0]!;
        Assert.That(ctx.Settings.SiteName, Is.EqualTo("Fallback Acme"),
            "fallback record carries appsettings SiteName verbatim");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 5.1 — publication-site pinning (Task 9.5)
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_Success_PublishesLlmsTxtNotification()
    {
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));
        var ctrl = MakeController(notificationPublisher: publisher);

        await ctrl.Render(CancellationToken.None);

        await publisher.Received(1).PublishLlmsTxtAsync(
            Arg.Any<HttpContext>(),
            Arg.Is<string>(h => h == LlmsCacheKeys.NormaliseHost(Host)),
            Arg.Is<string?>(c => c == Culture),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Render_NoResolvableRoot_DoesNotPublish()
    {
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        _resolver.Resolve(Arg.Any<string>(), _umbracoContext)
            .Returns(HostnameRootResolution.NotFound());
        var ctrl = MakeController(notificationPublisher: publisher);

        await ctrl.Render(CancellationToken.None);

        await publisher.DidNotReceiveWithAnyArgs().PublishLlmsTxtAsync(
            default!, default!, default, default);
    }

    [Test]
    public async Task Render_BuilderThrows_DoesNotPublish()
    {
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext)
            .Returns(HostnameRootResolution.Found(root, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var ctrl = MakeController(notificationPublisher: publisher);

        await ctrl.Render(CancellationToken.None);

        await publisher.DidNotReceiveWithAnyArgs().PublishLlmsTxtAsync(
            default!, default!, default, default);
    }

    [Test]
    public async Task Render_IfNoneMatchMatches_Returns304_DoesNotPublish()
    {
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        const string body = "# Acme\n> \n";
        var etag = LlmsTxt.Umbraco.Caching.ManifestETag.Compute(body);
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(body));
        var ctrl = MakeController(notificationPublisher: publisher);
        ctrl.Request.Headers["If-None-Match"] = etag;

        await ctrl.Render(CancellationToken.None);

        Assert.That(ctrl.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
        await publisher.DidNotReceiveWithAnyArgs().PublishLlmsTxtAsync(
            default!, default!, default, default);
    }

    [Test]
    public async Task PublishesOncePerRequest_MultiIDomain_DistinctHostnames()
    {
        // Failure & Edge Cases line 153 — siteA and siteB log under their
        // own normalised hostname; one publication per HTTP request.
        var publisher = Substitute.For<LlmsTxt.Umbraco.Notifications.ILlmsNotificationPublisher>();
        var rootA = StubContent("Acme A");
        var rootB = StubContent("Acme B");
        _resolver.Resolve("sitea.example", _umbracoContext)
            .Returns(HostnameRootResolution.Found(rootA, Culture));
        _resolver.Resolve("siteb.example", _umbracoContext)
            .Returns(HostnameRootResolution.Found(rootB, Culture));
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# x\n> \n"));

        var ctrlA = MakeController(requestHost: "sitea.example", notificationPublisher: publisher);
        var ctrlB = MakeController(requestHost: "siteb.example", notificationPublisher: publisher);

        await ctrlA.Render(CancellationToken.None);
        await ctrlB.Render(CancellationToken.None);

        await publisher.Received(1).PublishLlmsTxtAsync(
            Arg.Any<HttpContext>(),
            Arg.Is<string>(h => h == "sitea.example"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await publisher.Received(1).PublishLlmsTxtAsync(
            Arg.Any<HttpContext>(),
            Arg.Is<string>(h => h == "siteb.example"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 6.0a (Codex finding #3) — CachePolicySeconds zero/negative parity
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Render_CachePolicySecondsZero_BypassesCache_BuilderInvokedEachRequest()
    {
        // Story 6.0a AC3 — CachePolicySeconds = 0 disables the manifest cache
        // entirely (matches the LlmsTxtSettings.CachePolicySeconds xmldoc — "0
        // effectively disables caching"). Builder is invoked on every request;
        // cache stays empty. Mirrors LlmsFullTxtControllerTests of the same
        // shape.
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _currentSettings = new LlmsTxtSettings
        {
            LlmsTxtBuilder = new LlmsTxtBuilderSettings { CachePolicySeconds = 0 },
        };
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));

        var ctrl1 = MakeController();
        await ctrl1.Render(CancellationToken.None);
        var ctrl2 = MakeController();
        await ctrl2.Render(CancellationToken.None);

        await _builder.Received(2).BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>());
        Assert.That(_appCaches.RuntimeCache.Get(LlmsCacheKeys.LlmsTxt(Host, Culture)), Is.Null,
            "CachePolicySeconds = 0 must not persist anything in the runtime cache");
    }

    [Test]
    public async Task Render_CachePolicySecondsNegative_TreatedAsZero_BuilderInvokedEachRequest()
    {
        // Story 6.0a AC3 — negative is an operator typo; treat as 0 (cache
        // disabled) and rely on logged Warning to surface it. Mirrors
        // LlmsFullTxtController's "negative → 0 + Warning" defensive policy.
        var root = StubContent("Acme");
        _resolver.Resolve(Host, _umbracoContext).Returns(HostnameRootResolution.Found(root, Culture));
        _currentSettings = new LlmsTxtSettings
        {
            LlmsTxtBuilder = new LlmsTxtBuilderSettings { CachePolicySeconds = -10 },
        };
        _builder.BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("# Acme\n> \n"));

        var ctrl1 = MakeController();
        await ctrl1.Render(CancellationToken.None);
        var ctrl2 = MakeController();
        await ctrl2.Render(CancellationToken.None);

        await _builder.Received(2).BuildAsync(Arg.Any<LlmsTxtBuilderContext>(), Arg.Any<CancellationToken>());
    }
}
