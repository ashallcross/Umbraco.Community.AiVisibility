using LlmsTxt.Umbraco.Configuration;
using Microsoft.Extensions.Configuration;

namespace LlmsTxt.Umbraco.Tests.Configuration;

/// <summary>
/// Story 3.3 — drift-detection gate for the in-code defaults that adopters
/// rely on for zero-config behaviour. Pinning every documented default
/// here means a future story silently bumping (e.g.) <c>MaxLlmsFullSizeKb</c>
/// from 5120 to 10240 fails this fixture and forces a deliberate decision
/// rather than a stealth regression.
/// <para>
/// The fixture exercises three angles:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="Defaults_NewLlmsTxtSettings_AllFieldsHoldInCodeDefaults"/> —
/// the bare property initialisers (no DI, no <see cref="IConfiguration"/>).
/// Pins what the C# initialiser emits.
/// </description></item>
/// <item><description>
/// <see cref="AppsettingsBindWithEmptySection_AllFieldsHoldInCodeDefaults"/>
/// — an empty <see cref="IConfiguration"/> with no <c>LlmsTxt:</c> section.
/// Pins the AC1 contract: a host with no <c>LlmsTxt:</c> entry in its
/// <c>appsettings.json</c> still gets the in-code defaults via the
/// <c>configuration.GetSection(...).Get&lt;LlmsTxtSettings&gt;() ?? new()</c>
/// composer pattern.
/// </description></item>
/// <item><description>
/// <see cref="AppsettingsBindWithPartialSection_FieldNotInJsonHoldsDefault"/>
/// — partial config (one field set). Pins the per-field-fallback contract
/// of <c>Microsoft.Extensions.Options</c>: fields not present in the bound
/// section keep their property initialisers.
/// </description></item>
/// </list>
/// </summary>
[TestFixture]
public class LlmsTxtSettingsDefaultsTests
{
    // AC7 — every documented in-code default. If a field is reordered or its
    // default value changes, update this fixture in the SAME PR + flag the
    // change in Story 3.3 Spec Drift Notes.

    [Test]
    public void Defaults_NewLlmsTxtSettings_AllFieldsHoldInCodeDefaults()
    {
        var settings = new LlmsTxtSettings();

        Assert.Multiple(() =>
        {
            // Top-level fields (Stories 1.1, 1.2, 2.1, 2.2, 3.1)
            Assert.That(settings.MainContentSelectors, Is.Empty,
                "MainContentSelectors defaults to empty (Story 1.4 — built-in chain only)");
            Assert.That(settings.CachePolicySeconds, Is.EqualTo(60),
                "CachePolicySeconds defaults to 60s (Story 1.2 per-page TTL)");
            Assert.That(settings.SiteName, Is.Null,
                "SiteName defaults to null (Story 2.1 — falls back to root.Name → 'Site' literal)");
            Assert.That(settings.SiteSummary, Is.Null,
                "SiteSummary defaults to null (Story 2.1 — empty-blockquote line skipped per Story 3.1 patch)");
            Assert.That(settings.MaxLlmsFullSizeKb, Is.EqualTo(5120),
                "MaxLlmsFullSizeKb defaults to 5120 KB / 5 MB (Story 2.2 size cap)");
            Assert.That(settings.ExcludedDoctypeAliases, Is.Empty,
                "ExcludedDoctypeAliases (top-level) defaults to empty (Story 3.1 — no implicit cross-route exclusions)");
            Assert.That(settings.SettingsResolverCachePolicySeconds, Is.EqualTo(300),
                "SettingsResolverCachePolicySeconds defaults to 300s (Story 3.1 — matches manifest TTLs)");

            // LlmsTxtBuilder sub-section (Story 2.1)
            Assert.That(settings.LlmsTxtBuilder, Is.Not.Null);
            Assert.That(settings.LlmsTxtBuilder.SectionGrouping, Is.Empty,
                "LlmsTxtBuilder.SectionGrouping defaults to empty (Story 2.1 — single 'Pages' default section)");
            Assert.That(settings.LlmsTxtBuilder.PageSummaryPropertyAlias, Is.EqualTo("metaDescription"),
                "LlmsTxtBuilder.PageSummaryPropertyAlias defaults to 'metaDescription' (Story 2.1)");
            Assert.That(settings.LlmsTxtBuilder.CachePolicySeconds, Is.EqualTo(300),
                "LlmsTxtBuilder.CachePolicySeconds defaults to 300s (Story 2.1 manifest TTL)");

            // LlmsFullScope sub-section (Story 2.2)
            Assert.That(settings.LlmsFullScope, Is.Not.Null);
            Assert.That(settings.LlmsFullScope.RootContentTypeAlias, Is.Null,
                "LlmsFullScope.RootContentTypeAlias defaults to null — whole-site scope (Story 2.2)");
            Assert.That(settings.LlmsFullScope.IncludedDocTypeAliases, Is.Empty,
                "LlmsFullScope.IncludedDocTypeAliases defaults to empty — no positive filter (Story 2.2)");
            // Note: pre-existing deferred-work entry (Story 3.1 manual gate Step 4)
            // flags that "errorPage"/"redirectPage" don't match Clean.Core 7.0.5's
            // actual aliases (which are "error"/"redirect"). Pinning the values
            // verbatim per package-spec.md § 10 + Story 2.2 contract; Story 6.1
            // release-readiness owns the docs reconciliation.
            Assert.That(settings.LlmsFullScope.ExcludedDocTypeAliases,
                Is.EquivalentTo(new[] { "errorPage", "redirectPage" }),
                "LlmsFullScope.ExcludedDocTypeAliases defaults to ['errorPage','redirectPage'] (Story 2.2 — package-spec.md § 10)");

            // LlmsFullBuilder sub-section (Story 2.2)
            Assert.That(settings.LlmsFullBuilder, Is.Not.Null);
            Assert.That(settings.LlmsFullBuilder.Order, Is.EqualTo(LlmsFullOrder.TreeOrder),
                "LlmsFullBuilder.Order defaults to TreeOrder (Story 2.2 AC4)");
            Assert.That(settings.LlmsFullBuilder.CachePolicySeconds, Is.EqualTo(300),
                "LlmsFullBuilder.CachePolicySeconds defaults to 300s (Story 2.2 manifest TTL)");

            // Hreflang sub-section (Story 2.3)
            Assert.That(settings.Hreflang, Is.Not.Null);
            Assert.That(settings.Hreflang.Enabled, Is.False,
                "Hreflang.Enabled defaults to false — opt-in per FR25 (Story 2.3)");

            // Migrations sub-section (Story 3.1)
            Assert.That(settings.Migrations, Is.Not.Null);
            Assert.That(settings.Migrations.SkipSettingsDoctype, Is.False,
                "Migrations.SkipSettingsDoctype defaults to false — uSync coexistence opt-in (Story 3.1)");
        });
    }

    [Test]
    public void AppsettingsBindWithEmptySection_AllFieldsHoldInCodeDefaults()
    {
        // AC1 + AC7 — pin the contract that the package's composer relies on:
        // an IConfiguration whose `LlmsTxt:` section exists but carries no
        // values bound via the standard GetSection(...).Get<T>() ?? new()
        // pattern resolves to the in-code defaults. The probe key materialises
        // the section so the binder actually runs (without it, GetSection
        // returns null for an entirely-missing section and the test would
        // pass via the `?? new()` fallback rather than the binder path —
        // missing the contract under test).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmsTxt:__BinderProbe"] = "x",
            })
            .Build();

        var bound = configuration
            .GetSection(LlmsTxtSettings.SectionName)
            .Get<LlmsTxtSettings>()
            ?? new LlmsTxtSettings();

        Assert.Multiple(() =>
        {
            Assert.That(bound.MaxLlmsFullSizeKb, Is.EqualTo(5120),
                "Empty appsettings → MaxLlmsFullSizeKb falls back to in-code default");
            Assert.That(bound.CachePolicySeconds, Is.EqualTo(60),
                "Empty appsettings → CachePolicySeconds falls back to in-code default");
            Assert.That(bound.SiteName, Is.Null,
                "Empty appsettings → SiteName stays null");
            Assert.That(bound.SiteSummary, Is.Null,
                "Empty appsettings → SiteSummary stays null");
            Assert.That(bound.LlmsFullBuilder.Order, Is.EqualTo(LlmsFullOrder.TreeOrder),
                "Empty appsettings → LlmsFullBuilder.Order falls back to TreeOrder");
            Assert.That(bound.LlmsTxtBuilder.PageSummaryPropertyAlias, Is.EqualTo("metaDescription"),
                "Empty appsettings → LlmsTxtBuilder.PageSummaryPropertyAlias falls back to 'metaDescription'");
            Assert.That(bound.Hreflang.Enabled, Is.False,
                "Empty appsettings → Hreflang.Enabled falls back to false");
            Assert.That(bound.Migrations.SkipSettingsDoctype, Is.False,
                "Empty appsettings → Migrations.SkipSettingsDoctype falls back to false");
            Assert.That(bound.ExcludedDoctypeAliases, Is.Empty,
                "Empty appsettings → ExcludedDoctypeAliases falls back to empty");
            Assert.That(bound.LlmsFullScope.ExcludedDocTypeAliases,
                Is.EquivalentTo(new[] { "errorPage", "redirectPage" }),
                "Empty appsettings → LlmsFullScope.ExcludedDocTypeAliases falls back to in-code defaults");
        });
    }

    [Test]
    public void AppsettingsBindWithPartialSection_FieldNotInJsonHoldsDefault()
    {
        // AC7 — pins the per-field-fallback contract of
        // Microsoft.Extensions.Options binding: setting one field in the
        // bound section does not reset other fields to type-defaults; the
        // property initialisers survive.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmsTxt:SiteName"] = "Custom",
            })
            .Build();

        var bound = configuration
            .GetSection(LlmsTxtSettings.SectionName)
            .Get<LlmsTxtSettings>()
            ?? new LlmsTxtSettings();

        Assert.Multiple(() =>
        {
            Assert.That(bound.SiteName, Is.EqualTo("Custom"),
                "Bound field overrides in-code default");
            Assert.That(bound.MaxLlmsFullSizeKb, Is.EqualTo(5120),
                "Field not in JSON keeps property initialiser default");
            Assert.That(bound.CachePolicySeconds, Is.EqualTo(60),
                "Field not in JSON keeps property initialiser default");
            Assert.That(bound.LlmsTxtBuilder.PageSummaryPropertyAlias, Is.EqualTo("metaDescription"),
                "Sub-section field not in JSON keeps property initialiser default");
            Assert.That(bound.LlmsFullScope.ExcludedDocTypeAliases,
                Is.EquivalentTo(new[] { "errorPage", "redirectPage" }),
                "Sub-section list field not in JSON keeps in-code default");
            Assert.That(bound.Hreflang.Enabled, Is.False,
                "Sub-section bool field not in JSON keeps in-code default");
        });
    }

    [Test]
    public void AppsettingsBindWithExplicitNullSubsectionLists_NotNull()
    {
        // Code-review DN4 — pin that an adopter who writes
        // `"LlmsFullScope": { "ExcludedDocTypeAliases": null }` in their
        // appsettings does NOT end up with a null list that NREs in
        // downstream consumers (e.g. DefaultLlmsFullBuilder's
        // `.Contains(...)` calls). Microsoft.Extensions.Configuration
        // semantics around binding explicit null to a non-null property
        // initialiser are version-sensitive; this test makes the
        // behaviour explicit. If the binder ever starts replacing the
        // initialiser with null, this test fails immediately and the
        // fix lives in LlmsTxtSettings (single source of truth) rather
        // than scattered defensive `?? Array.Empty<string>()` at every
        // call site.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmsTxt:LlmsFullScope:ExcludedDocTypeAliases"] = null,
                ["LlmsTxt:LlmsFullScope:IncludedDocTypeAliases"] = null,
                ["LlmsTxt:ExcludedDoctypeAliases"] = null,
            })
            .Build();

        var bound = configuration
            .GetSection(LlmsTxtSettings.SectionName)
            .Get<LlmsTxtSettings>()
            ?? new LlmsTxtSettings();

        Assert.Multiple(() =>
        {
            Assert.That(bound.LlmsFullScope.ExcludedDocTypeAliases, Is.Not.Null,
                "Explicit-null binding must not replace the property initialiser with null (LlmsFullScope.ExcludedDocTypeAliases)");
            Assert.That(bound.LlmsFullScope.IncludedDocTypeAliases, Is.Not.Null,
                "Explicit-null binding must not replace the property initialiser with null (LlmsFullScope.IncludedDocTypeAliases)");
            Assert.That(bound.ExcludedDoctypeAliases, Is.Not.Null,
                "Explicit-null binding must not replace the property initialiser with null (top-level ExcludedDoctypeAliases)");
        });
    }
}
