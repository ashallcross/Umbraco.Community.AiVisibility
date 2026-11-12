using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Community.AiVisibility.Composing;

/// <summary>
/// Registers <see cref="ISettingsResolver"/>. The package migration plan is
/// auto-discovered by Umbraco and is always left registered; the
/// <c>AiVisibility:Migrations:SkipSettingsDoctype</c> flag (uSync coexistence)
/// is honoured inside the doctype migration step itself, not here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Migration-plan registration is auto-discovery, not explicit.</b>
/// Umbraco's <see cref="Umbraco.Cms.Core.Packaging.PackageMigrationPlanCollection"/>
/// is populated by the framework with every type derived from
/// <see cref="Umbraco.Cms.Core.Packaging.PackageMigrationPlan"/> discovered by
/// <c>TypeLoader.GetPackageMigrationPlans</c>, so this composer neither adds
/// nor removes <c>AiVisibilityPackageMigrationPlan</c>. Removing the plan would
/// also drop its request-log table steps, which uSync does not own.
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

        // Migration-plan auto-discovery via TypeLoader. The doctype migration
        // step itself reads AiVisibility:Migrations:SkipSettingsDoctype and skips
        // only the doctype/composition. Do not remove the whole plan here: the
        // same plan also creates aiVisibilityRequestLog, which uSync does not own.
    }
}
