using LlmsTxt.Umbraco.Configuration;
using Microsoft.Extensions.Configuration;

namespace LlmsTxt.Umbraco.Tests.Configuration;

/// <summary>
/// Story 4.1 AC9 — pins the appsettings binding contract for
/// <see cref="ContentSignalSettings.PerDocTypeAlias"/>:
/// <para>
///   <c>LlmsTxt:ContentSignal:PerDocTypeAlias:articlePage = "..."</c> binds to
///   <c>PerDocTypeAlias["articlePage"] = "..."</c> and lookups against the
///   bound dictionary are case-insensitive (matching the resolver's
///   <c>OrdinalIgnoreCase</c> contract).
/// </para>
/// <para>
/// The <c>Microsoft.Extensions.Configuration</c> binder may or may not preserve
/// the property initialiser's <see cref="StringComparer.OrdinalIgnoreCase"/>;
/// <see cref="ContentSignalResolver"/> compensates with an explicit
/// case-insensitive comparison, but this fixture verifies the binding shape
/// itself so a future binder regression surfaces immediately.
/// </para>
/// </summary>
[TestFixture]
public class ContentSignalSettingsTests
{
    [Test]
    public void AppsettingsBindWithDefaultOnly_BindsValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmsTxt:ContentSignal:Default"] = "ai-train=no, search=yes, ai-input=yes",
            })
            .Build();

        var bound = configuration
            .GetSection(LlmsTxtSettings.SectionName)
            .Get<LlmsTxtSettings>()
            ?? new LlmsTxtSettings();

        Assert.That(bound.ContentSignal.Default, Is.EqualTo("ai-train=no, search=yes, ai-input=yes"));
        Assert.That(bound.ContentSignal.PerDocTypeAlias.Count, Is.EqualTo(0));
    }

    [Test]
    public void AppsettingsBindWithPerDocTypeAlias_BindsCaseInsensitiveDictionary()
    {
        // Bind two per-doctype entries with mixed casing. The resolver's
        // OrdinalIgnoreCase lookup contract means both should be reachable
        // regardless of the binder's comparer choice.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmsTxt:ContentSignal:Default"] = "ai-train=no",
                ["LlmsTxt:ContentSignal:PerDocTypeAlias:articlePage"] = "ai-train=yes, search=yes",
                ["LlmsTxt:ContentSignal:PerDocTypeAlias:LandingPage"] = "ai-train=no, search=no",
            })
            .Build();

        var bound = configuration
            .GetSection(LlmsTxtSettings.SectionName)
            .Get<LlmsTxtSettings>()
            ?? new LlmsTxtSettings();

        Assert.That(bound.ContentSignal.PerDocTypeAlias.Count, Is.EqualTo(2));

        // Verify ContentSignalResolver picks up both regardless of input casing.
        Assert.That(ContentSignalResolver.Resolve(bound, "articlePage"),
            Is.EqualTo("ai-train=yes, search=yes"),
            "Per-doctype override (exact-case) wins over site default");
        Assert.That(ContentSignalResolver.Resolve(bound, "ArticlePage"),
            Is.EqualTo("ai-train=yes, search=yes"),
            "Per-doctype override is case-insensitive (mixed-case lookup hits lowercase key)");
        Assert.That(ContentSignalResolver.Resolve(bound, "landingpage"),
            Is.EqualTo("ai-train=no, search=no"),
            "Per-doctype override is case-insensitive (lowercase lookup hits PascalCase key)");
        Assert.That(ContentSignalResolver.Resolve(bound, "homePage"),
            Is.EqualTo("ai-train=no"),
            "Doctype not in per-doctype dictionary falls back to site default");
    }
}
