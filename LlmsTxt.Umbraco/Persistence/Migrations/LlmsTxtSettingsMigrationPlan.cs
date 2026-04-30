using Umbraco.Cms.Core.Packaging;

namespace LlmsTxt.Umbraco.Persistence.Migrations;

/// <summary>
/// Story 3.1 — package-migration plan that ships the Settings doctype +
/// per-page exclusion composition for LlmsTxt.Umbraco.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pattern source:</b> AgentRun.Umbraco's <c>AgentRunMigrationPlan</c>
/// (imperative <c>PackageMigrationPlan</c> + hand-coded
/// <c>AsyncMigrationBase</c> steps). The original Story 3.1 spec called
/// for <see cref="AutomaticPackageMigrationPlan"/> + an embedded
/// <c>package.xml</c> resource per <c>architecture.md</c> § 339 — that path
/// failed at first manual gate because <c>CompiledPackageXmlParser</c>
/// rejects hand-authored <c>&lt;umbPackage&gt;</c> documents (the parser
/// expects the exact serialised shape Backoffice's "Create Package"
/// feature exports, which is a circular bootstrap problem for fresh
/// packages). AgentRun's evidence shows the imperative pattern is what
/// actually ships in Umbraco v17 packages.
/// </para>
/// <para>
/// <b>Spec drift logged for Architect retro:</b> recommend updating
/// <c>architecture.md</c> § 339 + 549 + <c>epics.md</c> § Story 3.1 AC1 to
/// name <see cref="PackageMigrationPlan"/> as the canonical pattern, with
/// AgentRun cited as precedent.
/// </para>
/// <para>
/// Adopters who own the doctype via uSync set
/// <c>LlmsTxt:Migrations:SkipSettingsDoctype: true</c>;
/// <see cref="Composers.SettingsComposer"/> calls
/// <c>PackageMigrationPlans().Remove&lt;LlmsTxtSettingsMigrationPlan&gt;()</c>
/// when the flag is set so this plan never enters the migration pipeline.
/// </para>
/// </remarks>
public sealed class LlmsTxtSettingsMigrationPlan : PackageMigrationPlan
{
    public LlmsTxtSettingsMigrationPlan()
        : base("LlmsTxt.Umbraco")
    {
    }

    protected override void DefinePlan()
        => From(string.Empty)
            .To<CreateLlmsSettingsDoctype>("A4F2C1E7-8B5D-4A3E-9F1C-2D8E5B7C0A6F");
}
