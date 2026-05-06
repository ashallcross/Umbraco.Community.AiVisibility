using Umbraco.Community.AiVisibility.LlmsTxt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace Umbraco.Community.AiVisibility.Tests.LlmsTxt;

[TestFixture]
public class HreflangVariantsResolverTests
{
    private const string MatchedCulture = "en-gb";
    private static readonly Guid RootKey = new("11111111-1111-1111-1111-111111111111");

    private IDomainService _domainService = null!;
    private IPublishedUrlProvider _urlProvider = null!;
    private IUmbracoContext _umbracoContext = null!;
    private IPublishedContentCache _snapshot = null!;
    private IPublishedContent _root = null!;

    [SetUp]
    public void Setup()
    {
        _domainService = Substitute.For<IDomainService>();
        _urlProvider = Substitute.For<IPublishedUrlProvider>();
        _umbracoContext = Substitute.For<IUmbracoContext>();
        _snapshot = Substitute.For<IPublishedContentCache>();
        _umbracoContext.Content.Returns(_snapshot);
        _root = StubPage("Acme", RootKey, cultures: new[] { "en-gb", "fr-fr" });
        _snapshot.GetById(Arg.Any<int>()).Returns((IPublishedContent?)null);
        _snapshot.GetById(101).Returns(_root);
    }

    [TearDown]
    public void TearDown()
    {
        _umbracoContext.Dispose();
    }

    private HreflangVariantsResolver MakeResolver()
        => new(_domainService, _urlProvider, NullLogger<HreflangVariantsResolver>.Instance);

    private void StubDomains(params IDomain[] domains)
    {
        // Build the array OUTSIDE the .Returns(...) call so NSubstitute's
        // last-call tracking isn't disturbed by intermediate substitute calls
        // (Substitute.For<IDomain>() inside StubDomain).
        _domainService.GetAll(includeWildcards: true).Returns(domains);
    }

    [Test]
    public async Task ResolveAsync_NoSiblingDomains_ReturnsEmptyDictionary()
    {
        StubDomains(StubDomain("sitea.example", "en-GB", 101));

        var result = await MakeResolver().ResolveAsync(
            new[] { _root }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task ResolveAsync_OnePageWithOneSiblingCulture_ReturnsOneVariantPerPage()
    {
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubDomain("sitea.fr", "fr-FR", 101));
        _urlProvider.GetUrl(_root, UrlMode.Relative, "fr-fr", Arg.Any<Uri?>())
            .Returns("/fr/");

        var result = await MakeResolver().ResolveAsync(
            new[] { _root }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[RootKey][0].Culture, Is.EqualTo("fr-fr"));
            Assert.That(result[RootKey][0].RelativeMarkdownUrl, Is.EqualTo("/fr/index.html.md"),
                "trailing-slash → /index.html.md suffix (mirrors DefaultLlmsTxtBuilder)");
        });
    }

    [Test]
    public async Task ResolveAsync_PageNotPublishedInSiblingCulture_OmittedFromVariants()
    {
        var about = StubPage("About", Guid.NewGuid(), cultures: new[] { "en-gb" });
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubDomain("sitea.fr", "fr-FR", 101));

        var result = await MakeResolver().ResolveAsync(
            new[] { about }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.That(result, Is.Empty,
            "page is not published in fr-fr → no variant entry");
    }

    [Test]
    public async Task ResolveAsync_UrlProviderThrows_OmitsThatVariantAndLogs()
    {
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubDomain("sitea.fr", "fr-FR", 101));
        _urlProvider.GetUrl(_root, UrlMode.Relative, "fr-fr", Arg.Any<Uri?>())
            .Returns(_ => throw new InvalidOperationException("URL provider exploded"));

        var result = await MakeResolver().ResolveAsync(
            new[] { _root }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.That(result, Is.Empty,
            "URL provider exception → variant skipped, page falls out of result map");
    }

    [Test]
    public async Task ResolveAsync_MultipleSiblingCultures_AllVariantsForPagesPublishedInThem()
    {
        var about = StubPage("About", Guid.NewGuid(), cultures: new[] { "en-gb", "fr-fr", "nl-nl" });
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubDomain("sitea.fr", "fr-FR", 101),
            StubDomain("sitea.nl", "nl-NL", 101));
        _urlProvider.GetUrl(about, UrlMode.Relative, "fr-fr", Arg.Any<Uri?>())
            .Returns("/fr/about");
        _urlProvider.GetUrl(about, UrlMode.Relative, "nl-nl", Arg.Any<Uri?>())
            .Returns("/nl/about");

        var result = await MakeResolver().ResolveAsync(
            new[] { about }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        var variants = result[about.Key];
        Assert.Multiple(() =>
        {
            Assert.That(variants, Has.Count.EqualTo(2));
            Assert.That(variants.Any(v => v.Culture == "fr-fr" && v.RelativeMarkdownUrl == "/fr/about.md"));
            Assert.That(variants.Any(v => v.Culture == "nl-nl" && v.RelativeMarkdownUrl == "/nl/about.md"));
        });
    }

    [Test]
    public async Task ResolveAsync_DifferentRootDomain_NotConsideredSibling()
    {
        var otherRoot = StubPage("Other", Guid.NewGuid(), cultures: new[] { "fr-fr" });
        _snapshot.GetById(202).Returns(otherRoot);
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubDomain("siteb.example", "fr-FR", 202));

        var result = await MakeResolver().ResolveAsync(
            new[] { _root }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.That(result, Is.Empty,
            "fr-FR is bound to a different root → not a sibling for our matched root");
    }

    [Test]
    public async Task ResolveAsync_AbsoluteUrlReturned_VariantSkipped()
    {
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubDomain("sitea.fr", "fr-FR", 101));
        _urlProvider.GetUrl(_root, UrlMode.Relative, "fr-fr", Arg.Any<Uri?>())
            .Returns("https://other.example/fr/");

        var result = await MakeResolver().ResolveAsync(
            new[] { _root }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.That(result, Is.Empty,
            "absolute URL is rejected — defensive against multi-site misconfig");
    }

    [Test]
    public async Task ResolveAsync_FileSchemeAbsoluteUrlReturned_VariantSkipped()
    {
        // Code-review P6 (2026-04-30) — Unix `Uri.TryCreate("/path", Absolute)`
        // can resolve to `file:///path`. Earlier code kept file-scheme URIs
        // through; the corrected guard skips ALL absolute URIs regardless of
        // scheme.
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubDomain("sitea.fr", "fr-FR", 101));
        _urlProvider.GetUrl(_root, UrlMode.Relative, "fr-fr", Arg.Any<Uri?>())
            .Returns("file:///fr/index.html");

        var result = await MakeResolver().ResolveAsync(
            new[] { _root }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.That(result, Is.Empty, "file-scheme absolute URI must be skipped, not emitted as a variant");
    }

    [TestCase("/path(with-paren)/")]
    [TestCase("/path)backwards")]
    [TestCase("/path?query=1")]
    [TestCase("/path#fragment")]
    public async Task ResolveAsync_PageWithUnsafeCharInUrl_VariantSkipped(string unsafeUrl)
    {
        // Code-review D2 (2026-04-30) — the output line shape `({culture}: {url})`
        // ambiguates if the URL itself contains `(`, `)`, `?`, or `#`. Restrict
        // the variant URL alphabet rather than percent-encoding (which would
        // diverge primary and variant URL formatting).
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubDomain("sitea.fr", "fr-FR", 101));
        _urlProvider.GetUrl(_root, UrlMode.Relative, "fr-fr", Arg.Any<Uri?>())
            .Returns(unsafeUrl);

        var result = await MakeResolver().ResolveAsync(
            new[] { _root }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.That(result, Is.Empty,
            $"variant URL containing an unsafe character must be skipped (input: {unsafeUrl})");
    }

    [Test]
    public async Task ResolveAsync_CultureOnlyWildcardBinding_TreatedAsSibling()
    {
        // Umbraco serialises culture-only path-bound second cultures under a
        // node as `*<rootId>` (IsWildcard=true). The resolver MUST include
        // these — they are the most common multi-culture pattern when adopters
        // use a single hostname with culture sub-paths.
        // Real-world DB shape this regression-tests:
        //   sitea.example   → root 1120, en-US (the matched culture's binding)
        //   *1120           → root 1120, cy-GB (the culture-only sibling)
        var page = StubPage("Features", Guid.NewGuid(), cultures: new[] { "en-gb", "cy-gb" });
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubWildcardDomain("*101", "cy-GB", 101));
        _urlProvider.GetUrl(page, UrlMode.Relative, "cy-gb", Arg.Any<Uri?>())
            .Returns("/features-welsh");

        var result = await MakeResolver().ResolveAsync(
            new[] { page }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[page.Key][0].Culture, Is.EqualTo("cy-gb"),
                "*<rootId> wildcard with a different culture must surface as a sibling");
            Assert.That(result[page.Key][0].RelativeMarkdownUrl, Is.EqualTo("/features-welsh.md"));
        });
    }

    [Test]
    public async Task ResolveAsync_SubdomainWildcardBinding_NotTreatedAsSibling()
    {
        // `*.example.com` shape is a subdomain match, not a culture binding
        // for the matched root — must be filtered out even though IsWildcard
        // is true on both this and the `*<rootId>` form.
        StubDomains(
            StubDomain("sitea.example", "en-GB", 101),
            StubWildcardDomain("*.example.com", "fr-FR", 101));

        var result = await MakeResolver().ResolveAsync(
            new[] { _root }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.That(result, Is.Empty,
            "subdomain wildcard binding must be filtered out — not a culture sibling");
    }

    [Test]
    public async Task ResolveAsync_PageCulturesKeyedWithUppercase_StillMatched()
    {
        // Umbraco's IPublishedContent.Cultures keys can preserve original
        // casing from umbracoLanguage.languageISOCode (e.g. "cy-GB" with capital
        // GB). Our domain enumeration lowercases each LanguageIsoCode, so a
        // naive ContainsKey lookup on an ordinal-comparer dictionary would miss.
        // Real-world Clean.Core 7.0.5 hits this on the multi-culture TestSite.
        var page = StubPageWithExactCultureKeys("Features", Guid.NewGuid(),
            new[] { "en-US", "cy-GB" });
        StubDomains(
            StubDomain("sitea.example", "en-US", 101),
            StubWildcardDomain("*101", "cy-GB", 101));
        _urlProvider.GetUrl(page, UrlMode.Relative, "cy-gb", Arg.Any<Uri?>())
            .Returns("/cy/features");

        var result = await MakeResolver().ResolveAsync(
            new[] { page }, "en-us", RootKey, _umbracoContext, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(1),
                "Cultures dictionary keyed `cy-GB` (uppercase) must still resolve "
                + "against lowercased BCP-47 `cy-gb` from domain enumeration");
            Assert.That(result[page.Key][0].Culture, Is.EqualTo("cy-gb"));
        });
    }

    [Test]
    public async Task ResolveAsync_NullSnapshot_ReturnsEmpty()
    {
        _umbracoContext.Content.Returns((IPublishedContentCache?)null);

        var result = await MakeResolver().ResolveAsync(
            new[] { _root }, MatchedCulture, RootKey, _umbracoContext, CancellationToken.None);

        Assert.That(result, Is.Empty);
    }

    private static IPublishedContent StubPage(string name, Guid key, IReadOnlyList<string> cultures)
    {
        var c = Substitute.For<IPublishedContent>();
        c.Name.Returns(name);
        c.Key.Returns(key);
        var cultureMap = cultures.ToDictionary(
            x => x,
            x => (PublishedCultureInfo)null!,
            StringComparer.OrdinalIgnoreCase);
        c.Cultures.Returns((IReadOnlyDictionary<string, PublishedCultureInfo>)cultureMap);
        return c;
    }

    private static IDomain StubDomain(string domainName, string isoCode, int rootContentId)
    {
        // The resolver discriminates wildcard kind via DomainName.StartsWith("*.")
        // (Spec Drift Note #5), NOT IDomain.IsWildcard, so we don't stub IsWildcard.
        var d = Substitute.For<IDomain>();
        d.DomainName.Returns(domainName);
        d.LanguageIsoCode.Returns(isoCode);
        d.RootContentId.Returns(rootContentId);
        return d;
    }

    private static IPublishedContent StubPageWithExactCultureKeys(
        string name,
        Guid key,
        IReadOnlyList<string> cultures)
    {
        // Builds a Cultures dictionary with EXACT casing as supplied (no
        // normalisation), backed by an ordinal-comparer dictionary so the
        // resolver's case-insensitive lookup is genuinely tested.
        var c = Substitute.For<IPublishedContent>();
        c.Name.Returns(name);
        c.Key.Returns(key);
        var ordinalDict = new Dictionary<string, PublishedCultureInfo>(StringComparer.Ordinal);
        foreach (var culture in cultures)
        {
            ordinalDict[culture] = (PublishedCultureInfo)null!;
        }
        c.Cultures.Returns((IReadOnlyDictionary<string, PublishedCultureInfo>)ordinalDict);
        return c;
    }

    private static IDomain StubWildcardDomain(string domainName, string isoCode, int rootContentId)
    {
        // Same rationale as StubDomain — IsWildcard is not consulted by the
        // resolver; only DomainName.StartsWith("*.") matters.
        var d = Substitute.For<IDomain>();
        d.DomainName.Returns(domainName);
        d.LanguageIsoCode.Returns(isoCode);
        d.RootContentId.Returns(rootContentId);
        return d;
    }
}
