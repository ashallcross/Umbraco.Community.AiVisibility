using Umbraco.Community.AiVisibility.Configuration;

namespace Umbraco.Community.AiVisibility.Tests.Configuration;

/// <summary>
/// Story 4.1 AC8 + AC9 — pins the per-doctype-then-site-default fallback shape
/// of <see cref="ContentSignalResolver.Resolve(AiVisibilitySettings, string?)"/>.
/// Test density: happy path + per-doctype override + case-insensitive lookup +
/// 3 fallback variants (project-context.md § Testing Rules — ceiling, not floor).
/// </summary>
[TestFixture]
public class ContentSignalResolverTests
{
    [Test]
    public void Resolve_SiteDefaultNull_NoPerDoctype_ReturnsNull()
    {
        var settings = new AiVisibilitySettings(); // ContentSignal.Default = null, PerDocTypeAlias empty

        Assert.That(ContentSignalResolver.Resolve(settings, "articlePage"), Is.Null);
    }

    [Test]
    public void Resolve_SiteDefaultWhitespace_TreatedAsNull()
    {
        var settings = new AiVisibilitySettings
        {
            ContentSignal = new ContentSignalSettings { Default = "   " },
        };

        Assert.That(ContentSignalResolver.Resolve(settings, "articlePage"), Is.Null);
    }

    [Test]
    public void Resolve_SiteDefaultSet_NoPerDoctypeMatch_ReturnsTrimmedSiteDefault()
    {
        var settings = new AiVisibilitySettings
        {
            ContentSignal = new ContentSignalSettings { Default = "  ai-train=no, search=yes  " },
        };

        Assert.That(ContentSignalResolver.Resolve(settings, "homePage"),
            Is.EqualTo("ai-train=no, search=yes"));
    }

    [Test]
    public void Resolve_PerDoctypeMatch_ReturnsTrimmedOverride_WinsOverSiteDefault()
    {
        var settings = new AiVisibilitySettings
        {
            ContentSignal = new ContentSignalSettings
            {
                Default = "ai-train=no",
                PerDocTypeAlias = new Dictionary<string, string>
                {
                    ["articlePage"] = "  ai-train=yes, search=yes  ",
                },
            },
        };

        Assert.That(ContentSignalResolver.Resolve(settings, "articlePage"),
            Is.EqualTo("ai-train=yes, search=yes"));
    }

    [Test]
    public void Resolve_PerDoctypeMatch_CaseInsensitive()
    {
        // The dictionary may not preserve the property initialiser's comparer
        // when bound from appsettings; the resolver compensates with an
        // explicit OrdinalIgnoreCase comparison.
        var settings = new AiVisibilitySettings
        {
            ContentSignal = new ContentSignalSettings
            {
                Default = "ai-train=no",
                PerDocTypeAlias = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["articlePage"] = "ai-train=yes, search=yes",
                },
            },
        };

        Assert.That(ContentSignalResolver.Resolve(settings, "ArticlePage"),
            Is.EqualTo("ai-train=yes, search=yes"));
        Assert.That(ContentSignalResolver.Resolve(settings, "ARTICLEPAGE"),
            Is.EqualTo("ai-train=yes, search=yes"));
    }

    [Test]
    public void Resolve_PerDoctypeWhitespaceValue_FallsBackToSiteDefault()
    {
        var settings = new AiVisibilitySettings
        {
            ContentSignal = new ContentSignalSettings
            {
                Default = "ai-train=no",
                PerDocTypeAlias = new Dictionary<string, string>
                {
                    ["articlePage"] = "   ",
                },
            },
        };

        Assert.That(ContentSignalResolver.Resolve(settings, "articlePage"),
            Is.EqualTo("ai-train=no"));
    }

    [Test]
    public void Resolve_NullDoctype_SiteDefaultSet_ReturnsSiteDefault()
    {
        var settings = new AiVisibilitySettings
        {
            ContentSignal = new ContentSignalSettings { Default = "ai-train=no" },
        };

        Assert.That(ContentSignalResolver.Resolve(settings, doctypeAlias: null),
            Is.EqualTo("ai-train=no"));
    }

    [Test]
    public void Resolve_NullSettings_ReturnsNull()
    {
        Assert.That(ContentSignalResolver.Resolve(settings: null!, "articlePage"), Is.Null);
    }
}
