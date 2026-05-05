using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Persistence.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Extensions;

namespace LlmsTxt.Umbraco.Composers;

/// <summary>
/// Story 3.1 — registers <see cref="ILlmsSettingsResolver"/> + the
/// Settings-doctype migration plan. The migration-plan registration is gated
/// on <c>LlmsTxt:Migrations:SkipSettingsDoctype</c> for uSync coexistence
/// (architecture.md line 1092).
/// </summary>
/// <remarks>
/// <para>
/// <b>Migration-plan registration is auto-discovery, not explicit.</b>
/// Umbraco's <see cref="Umbraco.Cms.Core.Packaging.PackageMigrationPlanCollection"/>
/// is a <see cref="WeightedCollectionBuilderBase{TBuilder, TCollection, TItem}"/>;
/// the framework's <c>AddCoreInitialServices</c> calls
/// <c>collection.Add(IEnumerable&lt;Type&gt;)</c> with all types derived from
/// <see cref="Umbraco.Cms.Core.Packaging.PackageMigrationPlan"/> discovered by
/// <c>TypeLoader.GetPackageMigrationPlans</c>. Our composer therefore inverts
/// the obvious shape: it does NOT call <c>Add&lt;LlmsTxtSettingsMigrationPlan&gt;()</c>
/// (the framework already does); it calls <c>Remove&lt;LlmsTxtSettingsMigrationPlan&gt;()</c>
/// when <see cref="LlmsMigrationsSettings.SkipSettingsDoctype"/> is <c>true</c>.
/// </para>
/// <para>
/// Spec Drift Note logged in the story spec — the original spec text
/// (Task 4.1) called for <c>builder.AddPackageMigrationPlan&lt;T&gt;()</c>
/// gated on the flag; that extension does not exist in v17.3.2. The
/// canonical opt-out shape is <c>PackageMigrationPlans().Remove&lt;T&gt;()</c>
/// against the framework's auto-discovered list.
/// </para>
/// <para>
/// <b>Adopter override discipline</b> — same as <see cref="BuildersComposer"/>
/// (Story 2.1). Adopters wanting to swap <see cref="ILlmsSettingsResolver"/>
/// register their own implementation BEFORE us (our <c>TryAddScoped</c> bows
/// out) or AFTER us (DI's last-registration-wins resolves theirs).
/// </para>
/// </remarks>
public sealed class SettingsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Resolver — Scoped (architecture.md line 377). Default impl reads
        // request-scoped IUmbracoContextAccessor; Singleton would form a
        // captive dependency on the root provider. Pinned by
        // SettingsComposerTests.Compose_StartupValidation_LlmsSettingsResolver_NoCaptiveDependency.
        builder.Services.TryAddScoped<ILlmsSettingsResolver, DefaultLlmsSettingsResolver>();

        // Migration-plan auto-discovery via TypeLoader → opt-out only.
        // Read the flag directly from IConfiguration; the LlmsTxtSettings
        // binding may not yet be wired at composer time (RoutingComposer
        // binds it independently — composer order is "default suffices",
        // architecture line 637).
        var skipDoctype = builder.Config
            .GetSection(LlmsTxtSettings.SectionName)
            .GetSection("Migrations")
            .GetValue<bool>(nameof(LlmsMigrationsSettings.SkipSettingsDoctype));

        if (skipDoctype)
        {
            builder.PackageMigrationPlans().Remove<LlmsTxtSettingsMigrationPlan>();
        }
    }
}
