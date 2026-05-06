using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace Umbraco.Community.AiVisibility.Tests.Configuration;

[TestFixture]
public class DefaultLlmsSettingsResolverTests
{
    private const string Host = "sitea.example";
    private const string Culture = "en-gb";
    private static readonly Guid SettingsNodeKey = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private AiVisibilitySettings _appsettings = null!;
    private IOptionsMonitor<AiVisibilitySettings> _options = null!;
    private IUmbracoContextAccessor _accessor = null!;
    private IUmbracoContext _umbracoContext = null!;
    private IPublishedContentCache _publishedSnapshot = null!;
    private IDocumentNavigationQueryService _navigation = null!;
    private AppCaches _appCaches = null!;
    private DefaultSettingsResolver _resolver = null!;

    [SetUp]
    public void Setup()
    {
        // Reset the static log-once dedup so log-assertion tests see fresh
        // state regardless of fixture/test ordering.
        DefaultSettingsResolver.ResetForTestingDedupGuards();

        _appsettings = new AiVisibilitySettings();
        _options = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        _options.CurrentValue.Returns(_ => _appsettings);

        _accessor = Substitute.For<IUmbracoContextAccessor>();
        _umbracoContext = Substitute.For<IUmbracoContext>();
        _publishedSnapshot = Substitute.For<IPublishedContentCache>();
        _umbracoContext.Content.Returns(_publishedSnapshot);
        _accessor.TryGetUmbracoContext(out Arg.Any<IUmbracoContext>()!).Returns(call =>
        {
            call[0] = _umbracoContext;
            return true;
        });

        _navigation = Substitute.For<IDocumentNavigationQueryService>();
        _navigation.TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[0] = Array.Empty<Guid>();
            return true;
        });

        _appCaches = new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache()));

        _resolver = new DefaultSettingsResolver(
            _options,
            _accessor,
            _navigation,
            _appCaches,
            NullLogger<DefaultSettingsResolver>.Instance);
    }

    /// <summary>
    /// Build a resolver wired to a captured-logs <see cref="ILogger"/>
    /// substitute. Returns the substitute so tests can assert on
    /// <c>LogInformation</c> / <c>LogWarning</c> call counts.
    /// </summary>
    private (DefaultSettingsResolver resolver, ILogger<DefaultSettingsResolver> logger) CreateResolverWithCapturedLogger()
    {
        var logger = Substitute.For<ILogger<DefaultSettingsResolver>>();
        // ILogger.IsEnabled must return true or LoggerExtensions.LogXxx
        // short-circuits before reaching ILogger.Log.
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var resolver = new DefaultSettingsResolver(
            _options,
            _accessor,
            _navigation,
            _appCaches,
            logger);
        return (resolver, logger);
    }

    [TearDown]
    public void TearDown()
    {
        _appCaches.Dispose();
        _umbracoContext.Dispose();
    }

    [Test]
    public async Task ResolveAsync_NoSettingsNode_ReturnsAppsettingsVerbatim()
    {
        // No root content nodes have aiVisibilitySettings doctype → fall back to
        // appsettings values verbatim. Information-once log is fire-and-forget;
        // not asserted here (NullLogger drops it).
        _appsettings = new AiVisibilitySettings
        {
            SiteName = "Default Acme",
            SiteSummary = "Default summary",
            ExcludedDoctypeAliases = new[] { "errorPage" },
        };

        var resolved = await _resolver.ResolveAsync(Host, Culture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.SiteName, Is.EqualTo("Default Acme"));
            Assert.That(resolved.SiteSummary, Is.EqualTo("Default summary"));
            Assert.That(resolved.ExcludedDoctypeAliases, Has.Member("errorPage"));
            Assert.That(resolved.BaseSettings, Is.SameAs(_appsettings));
        });
    }

    [Test]
    public async Task ResolveAsync_SettingsNodeWithSiteName_OverridesAppsettings()
    {
        _appsettings = new AiVisibilitySettings { SiteName = "Default Acme" };
        SetupSettingsNode(siteName: "Acme Docs");

        var resolved = await _resolver.ResolveAsync(Host, Culture, CancellationToken.None);

        Assert.That(resolved.SiteName, Is.EqualTo("Acme Docs"),
            "doctype value wins over appsettings");
    }

    [Test]
    public async Task ResolveAsync_SettingsNodeWithEmptySiteName_FallsBackToAppsettings_PerField()
    {
        // Per-field fallback (NOT all-or-nothing): empty siteName falls back to
        // appsettings; siteSummary still uses the doctype value.
        _appsettings = new AiVisibilitySettings { SiteName = "Default Acme" };
        SetupSettingsNode(siteName: "", siteSummary: "Doctype summary");

        var resolved = await _resolver.ResolveAsync(Host, Culture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.SiteName, Is.EqualTo("Default Acme"),
                "empty siteName → appsettings fallback");
            Assert.That(resolved.SiteSummary, Is.EqualTo("Doctype summary"),
                "siteSummary doctype value still wins (per-field, not all-or-nothing)");
        });
    }

    [Test]
    public async Task ResolveAsync_SettingsNodeWithSiteSummaryOver500Chars_TruncatesAndAppendsEllipsis()
    {
        var longSummary = new string('A', 600);
        SetupSettingsNode(siteSummary: longSummary);

        var resolved = await _resolver.ResolveAsync(Host, Culture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.SiteSummary, Is.Not.Null);
            Assert.That(resolved.SiteSummary!.Length, Is.LessThanOrEqualTo(500),
                "truncated at ≤500 chars total (slice budget = 499 + 1-char ellipsis)");
            Assert.That(resolved.SiteSummary, Does.EndWith("…"),
                "ellipsis appended on truncation");
        });
    }

    [Test]
    public async Task ResolveAsync_NoSettingsNode_LogsInformationOnce()
    {
        // Spec § AC3 + Failure & Edge Cases — first-seen log guard must fire
        // exactly once per culture across the process lifetime, not per call
        // and not per resolver instance. The dedup state is static (production
        // shape — Scoped lifetime would otherwise reset on every request).
        // Disable resolver cache so every call genuinely hits BuildSnapshot
        // and exercises the dedup guard, not the cache layer's natural dedup.
        _appsettings = new AiVisibilitySettings { SettingsResolverCachePolicySeconds = 0 };
        var (resolver, logger) = CreateResolverWithCapturedLogger();

        _ = await resolver.ResolveAsync(Host, Culture, CancellationToken.None);
        _ = await resolver.ResolveAsync(Host, Culture, CancellationToken.None);
        _ = await resolver.ResolveAsync(Host, Culture, CancellationToken.None);

        // ILogger.LogInformation expands to ILogger.Log(LogLevel.Information, ...).
        var infoCalls = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Where(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Information)
            .Count();
        Assert.That(infoCalls, Is.EqualTo(1),
            "Information-once-per-culture guard: exactly one log across three uncached calls");
    }

    [Test]
    public async Task ResolveAsync_SettingsNodeWithSiteSummaryOver500Chars_TruncatesAndWarns()
    {
        // Spec § AC3 + Failure & Edge Cases — siteSummary truncation must
        // surface as a Warning, deduplicated to fire exactly once per culture.
        // TTL=0 to exercise the dedup guard rather than the cache layer.
        _appsettings = new AiVisibilitySettings { SettingsResolverCachePolicySeconds = 0 };
        SetupSettingsNode(siteSummary: new string('A', 600));
        var (resolver, logger) = CreateResolverWithCapturedLogger();

        _ = await resolver.ResolveAsync(Host, Culture, CancellationToken.None);
        _ = await resolver.ResolveAsync(Host, Culture, CancellationToken.None);

        var warnCalls = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Where(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Warning)
            .Count();
        Assert.That(warnCalls, Is.EqualTo(1),
            "Warning-once-per-culture truncation guard across two uncached calls");
    }

    [Test]
    public async Task ResolveAsync_ExcludedDoctypeAliases_UnionsAppsettingsAndDoctypeValues()
    {
        _appsettings = new AiVisibilitySettings
        {
            ExcludedDoctypeAliases = new[] { "errorPage", "redirectPage" },
        };
        SetupSettingsNode(excludedAliases: "blogPost\nlandingPage");

        var resolved = await _resolver.ResolveAsync(Host, Culture, CancellationToken.None);

        Assert.That(resolved.ExcludedDoctypeAliases, Is.EquivalentTo(new[]
        {
            "errorPage", "redirectPage", "blogPost", "landingPage",
        }), "appsettings + doctype values are unioned (case-insensitive set)");
    }

    [Test]
    public async Task ResolveAsync_ExcludedDoctypeAliases_ParsesNewlineCommaSemicolonSeparators()
    {
        SetupSettingsNode(excludedAliases: "alias1,alias2;alias3\nalias4");

        var resolved = await _resolver.ResolveAsync(Host, Culture, CancellationToken.None);

        Assert.That(resolved.ExcludedDoctypeAliases, Is.EquivalentTo(new[]
        {
            "alias1", "alias2", "alias3", "alias4",
        }), "all four separators (\\n, \\r, ',', ';') parse correctly");
    }

    [Test]
    public async Task ResolveAsync_CacheHit_DoesNotReWalkContentTree()
    {
        SetupSettingsNode(siteName: "First Call");

        _ = await _resolver.ResolveAsync(Host, Culture, CancellationToken.None);
        var navCallsAfterFirst = _navigation.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "TryGetRootKeys");

        // Second call to the SAME (host, culture) within TTL must not re-walk.
        _ = await _resolver.ResolveAsync(Host, Culture, CancellationToken.None);
        var navCallsAfterSecond = _navigation.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "TryGetRootKeys");

        Assert.That(navCallsAfterSecond, Is.EqualTo(navCallsAfterFirst),
            "second resolve within TTL must hit the cache and skip TryGetRootKeys");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private void SetupSettingsNode(string? siteName = null, string? siteSummary = null, string? excludedAliases = null)
    {
        var settingsNode = Substitute.For<IPublishedContent>();
        settingsNode.Key.Returns(SettingsNodeKey);
        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns("aiVisibilitySettings");
        settingsNode.ContentType.Returns(contentType);

        StubProperty(settingsNode, "siteName", siteName);
        StubProperty(settingsNode, "siteSummary", siteSummary);
        StubProperty(settingsNode, "excludedDoctypeAliases", excludedAliases);

        _navigation.TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[0] = new[] { SettingsNodeKey };
            return true;
        });
        _publishedSnapshot.GetById(SettingsNodeKey).Returns(settingsNode);
    }

    private static void StubProperty(IPublishedContent node, string alias, string? value)
    {
        if (value is null)
        {
            node.GetProperty(alias).Returns((IPublishedProperty?)null);
            return;
        }
        var prop = Substitute.For<IPublishedProperty>();
        prop.HasValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns(!string.IsNullOrEmpty(value));
        prop.GetValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns(value);
        node.GetProperty(alias).Returns(prop);
    }
}
