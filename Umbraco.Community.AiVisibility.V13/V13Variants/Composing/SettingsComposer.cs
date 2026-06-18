using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Community.AiVisibility.Composing;

/// <summary>
/// Story 3.1 — registers <see cref="ISettingsResolver"/> + the
/// Settings-doctype migration plan. The migration-plan registration is gated
/// on <c>AiVisibility:Migrations:SkipSettingsDoctype</c> for uSync coexistence
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
/// the obvious shape: it does NOT call <c>Add&lt;AiVisibilityPackageMigrationPlan&gt;()</c>
/// (the framework already does). Unlike the shipped v17 composer, the v13 SKU
/// keeps the plan registered when <see cref="MigrationsSettings.SkipSettingsDoctype"/>
/// is <c>true</c>; the doctype step self-skips so the request-log table still
/// migrates.
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
/// (Story 2.1). Adopters wanting to swap <see cref="ISettingsResolver"/>
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
        builder.Services.TryAddScoped<ISettingsResolver, DefaultSettingsResolver>();

        // Migration-plan auto-discovery via TypeLoader. The v13 migration step
        // itself reads AiVisibility:Migrations:SkipSettingsDoctype and skips only
        // the doctype/composition. Do not remove the whole plan here: the same
        // plan also creates aiVisibilityRequestLog, which uSync does not own.
    }
}
