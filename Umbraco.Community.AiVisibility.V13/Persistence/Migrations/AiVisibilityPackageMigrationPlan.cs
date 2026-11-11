using Umbraco.Cms.Core.Packaging;

namespace Umbraco.Community.AiVisibility.V13.Persistence.Migrations;

/// <summary>
/// v13 variant (Story 9.2) — package-migration plan for the v13 SKU. Ships the
/// <c>aiVisibilitySettings</c> doctype + per-page exclusion composition and the
/// <c>aiVisibilityRequestLog</c> table, using synchronous <see cref="MigrationBase"/>
/// steps (v13 has no <c>AsyncMigrationBase</c>).
/// </summary>
/// <remarks>
/// <para>
/// Auto-discovered via <c>TypeLoader</c> on first boot — no registration call
/// (verified on v13.14.0 at Story 9.0 § Probe 6). Plan key
/// <c>"Umbraco.Community.AiVisibility"</c> matches the v17 plan: a site runs only
/// one SKU, so the key is consistent across them; the state GUIDs for the two
/// retained steps are reused verbatim from the v17 plan so the migration-state
/// semantics line up.
/// </para>
/// <para>
/// <b>Divergence from the v17 plan:</b> the v17-only <c>RenameRequestLogTable_2_0</c>
/// step is omitted — the v13 SKU is brand new with no pre-rename adopters, so
/// <see cref="AddRequestLogTable"/> creates the final table name directly (AC6).
/// </para>
/// <para>
/// Adopters who own the doctype via uSync set
/// <c>AiVisibility:Migrations:SkipSettingsDoctype: true</c>; the plan still
/// runs, but <see cref="CreateAiVisibilitySettingsDoctype"/> returns early and
/// skips only the doctype/composition creation. <see cref="AddRequestLogTable"/>
/// runs regardless. Once that step has been recorded as executed, flipping the
/// flag back to <c>false</c> does not create the doctype retroactively.
/// </para>
/// </remarks>
public sealed class AiVisibilityPackageMigrationPlan : PackageMigrationPlan
{
    public AiVisibilityPackageMigrationPlan()
        : base("Umbraco.Community.AiVisibility")
    {
    }

    protected override void DefinePlan()
        => From(string.Empty)
            .To<CreateAiVisibilitySettingsDoctype>("A4F2C1E7-8B5D-4A3E-9F1C-2D8E5B7C0A6F")
            .To<AddRequestLogTable>("9B3D7E4A-2C8F-4F1B-A5E0-7D9B2A6F1C8E");
}
