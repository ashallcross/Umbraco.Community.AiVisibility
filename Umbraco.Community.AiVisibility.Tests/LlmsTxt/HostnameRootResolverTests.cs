using Umbraco.Community.AiVisibility.LlmsTxt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace Umbraco.Community.AiVisibility.Tests.LlmsTxt;

[TestFixture]
public class HostnameRootResolverTests
{
    private IDomainService _domainService = null!;
    private ILocalizationService _localizationService = null!;
    private IDocumentNavigationQueryService _navigation = null!;
    private IUmbracoContext _umbracoContext = null!;
    private IPublishedContentCache _publishedSnapshot = null!;

    [SetUp]
    public void Setup()
    {
        _domainService = Substitute.For<IDomainService>();
        _localizationService = Substitute.For<ILocalizationService>();
        _navigation = Substitute.For<IDocumentNavigationQueryService>();
        _umbracoContext = Substitute.For<IUmbracoContext>();
        _publishedSnapshot = Substitute.For<IPublishedContentCache>();
        _umbracoContext.Content.Returns(_publishedSnapshot);

        _localizationService.GetDefaultLanguageIsoCode().Returns("en-US");
        _domainService.GetAll(includeWildcards: true).Returns(Array.Empty<global::Umbraco.Cms.Core.Models.IDomain>());
        _navigation.TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[0] = Array.Empty<Guid>();
            return false;
        });
    }

    [TearDown]
    public void TearDown()
    {
        _umbracoContext?.Dispose();
    }

    private HostnameRootResolver MakeResolver()
        => new(_domainService, _localizationService, _navigation,
            NullLogger<HostnameRootResolver>.Instance);

    [Test]
    public void Resolve_HostMatchesExactDomain_ReturnsBoundRoot()
    {
        var rootContent = StubContent("Acme");
        SetupSnapshot(42, rootContent);
        SetupDomains(Domain("sitea.example", 42, "en-GB"));

        var result = MakeResolver().Resolve("sitea.example", _umbracoContext);

        Assert.Multiple(() =>
        {
            Assert.That(result.Root, Is.SameAs(rootContent));
            Assert.That(result.Culture, Is.EqualTo("en-gb"), "culture lowercased for stability");
        });
    }

    [Test]
    public void Resolve_HostMatchCaseInsensitive_StillResolves()
    {
        var rootContent = StubContent("Acme");
        SetupSnapshot(42, rootContent);
        SetupDomains(Domain("SiteA.Example", 42, "en-GB"));

        var result = MakeResolver().Resolve("sitea.example", _umbracoContext);

        Assert.That(result.Root, Is.SameAs(rootContent));
    }

    [Test]
    public void HostWithPort_PortStripped()
    {
        var rootContent = StubContent("Acme");
        SetupSnapshot(42, rootContent);
        SetupDomains(Domain("sitea.example", 42, "en-GB"));

        var result = MakeResolver().Resolve("sitea.example:8080", _umbracoContext);

        Assert.That(result.Root, Is.SameAs(rootContent));
    }

    [Test]
    public void Resolve_DomainHasScheme_StillMatchesByHost()
    {
        var rootContent = StubContent("Acme");
        SetupSnapshot(42, rootContent);
        SetupDomains(Domain("https://sitea.example", 42, "en-GB"));

        var result = MakeResolver().Resolve("sitea.example", _umbracoContext);

        Assert.That(result.Root, Is.SameAs(rootContent));
    }

    [Test]
    public void WildcardDomain_MatchesSubdomain()
    {
        var rootContent = StubContent("Acme");
        SetupSnapshot(42, rootContent);
        SetupDomains(Domain("*.example.com", 42, "en-GB", isWildcard: true));

        var result = MakeResolver().Resolve("foo.example.com", _umbracoContext);

        Assert.That(result.Root, Is.SameAs(rootContent));
    }

    [Test]
    public void Resolve_WildcardDomain_DoesNotMatchUnrelatedHost()
    {
        var siteRoot = StubContent("Acme");
        SetupSnapshot(42, siteRoot);
        SetupDomains(Domain("*.example.com", 42, "en-GB", isWildcard: true));
        // No-domain fallback: TryGetRootKeys → false → NotFound.

        var result = MakeResolver().Resolve("other.com", _umbracoContext);

        Assert.That(result.Root, Is.Null, "no fallback root because no domains matched and TryGetRootKeys returned false");
    }

    [Test]
    public void NoMatch_FallsBackToDefaultCultureRoot()
    {
        var fallback = StubContent("DefaultRoot");
        var fallbackKey = Guid.Parse("11111111-1111-1111-1111-111111111111");
        _publishedSnapshot.GetById(fallbackKey).Returns(fallback);
        _navigation.TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[0] = new[] { fallbackKey };
            return true;
        });
        SetupDomains(Domain("siteb.example", 99, "en-GB"));

        var result = MakeResolver().Resolve("unknown.example", _umbracoContext);

        Assert.Multiple(() =>
        {
            Assert.That(result.Root, Is.SameAs(fallback));
            Assert.That(result.Culture, Is.EqualTo("en-us"), "default culture from ILocalizationService");
        });
    }

    [Test]
    public void NoDomains_FallsBackToDefaultCultureRoot()
    {
        var fallback = StubContent("DefaultRoot");
        var fallbackKey = Guid.Parse("22222222-2222-2222-2222-222222222222");
        _publishedSnapshot.GetById(fallbackKey).Returns(fallback);
        _navigation.TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[0] = new[] { fallbackKey };
            return true;
        });
        // Default — no domains.

        var result = MakeResolver().Resolve("any.example", _umbracoContext);

        Assert.That(result.Root, Is.SameAs(fallback));
    }

    [Test]
    public void Resolve_PublishedSnapshotNull_ReturnsNotFound()
    {
        _umbracoContext.Content.Returns((IPublishedContentCache?)null);

        var result = MakeResolver().Resolve("sitea.example", _umbracoContext);

        Assert.That(result.Root, Is.Null);
    }

    [Test]
    public void Resolve_DomainBindingHasNoRootContentId_Skips()
    {
        var fallback = StubContent("DefaultRoot");
        var fallbackKey = Guid.Parse("33333333-3333-3333-3333-333333333333");
        _publishedSnapshot.GetById(fallbackKey).Returns(fallback);
        _navigation.TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[0] = new[] { fallbackKey };
            return true;
        });
        var orphanDomain = Substitute.For<IDomain>();
        orphanDomain.DomainName.Returns("sitea.example");
        orphanDomain.RootContentId.Returns((int?)null);
        orphanDomain.LanguageIsoCode.Returns("en-GB");
        SetupDomains(orphanDomain);

        var result = MakeResolver().Resolve("sitea.example", _umbracoContext);

        // The orphan domain matches but has no root → falls back to root keys.
        Assert.That(result.Root, Is.SameAs(fallback));
    }

    [Test]
    public void Resolve_CulturePathBinding_NeverMatchesHost()
    {
        var fallback = StubContent("DefaultRoot");
        var fallbackKey = Guid.Parse("44444444-4444-4444-4444-444444444444");
        _publishedSnapshot.GetById(fallbackKey).Returns(fallback);
        _navigation.TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>()!).Returns(call =>
        {
            call[0] = new[] { fallbackKey };
            return true;
        });
        // "/en/" style culture-only binding — never carries a hostname.
        SetupDomains(Domain("/en/", 99, "en-GB"));

        var result = MakeResolver().Resolve("sitea.example", _umbracoContext);

        Assert.That(result.Root, Is.SameAs(fallback), "culture-path binding doesn't match a host");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static IPublishedContent StubContent(string name)
    {
        var c = Substitute.For<IPublishedContent>();
        c.Name.Returns(name);
        c.Key.Returns(Guid.NewGuid());
        return c;
    }

    private void SetupSnapshot(int id, IPublishedContent content)
    {
        _publishedSnapshot.GetById(id).Returns(content);
    }

    private void SetupDomains(params IDomain[] domains)
    {
        _domainService.GetAll(includeWildcards: true).Returns(domains);
    }

    private static IDomain Domain(string domainName, int rootContentId, string languageIsoCode, bool isWildcard = false)
    {
        var d = Substitute.For<IDomain>();
        d.DomainName.Returns(domainName);
        d.RootContentId.Returns((int?)rootContentId);
        d.LanguageIsoCode.Returns(languageIsoCode);
        d.IsWildcard.Returns(isWildcard);
        return d;
    }
}
