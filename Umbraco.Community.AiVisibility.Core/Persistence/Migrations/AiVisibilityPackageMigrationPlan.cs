using Umbraco.Cms.Core.Packaging;

namespace Umbraco.Community.AiVisibility.Persistence.Migrations;

/// <summary>
/// Story 3.1 — package-migration plan that ships the Settings doctype +
/// per-page exclusion composition for Umbraco.Community.AiVisibility.
/// Story 6.0c (2026-05-06) extended the plan with
/// <see cref="RenameRequestLogTable_2_0"/> to migrate adopter DBs from the
/// pre-rename <c>llmsTxtRequestLog</c> table to <c>aiVisibilityRequestLog</c>.
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
/// <b>Plan key string:</b> the constructor-base argument
/// <c>"Umbraco.Community.AiVisibility"</c> is persisted as the plan key in
/// Umbraco's <c>umbracoMigrations</c> host-DB table. Pre-rename adopters
/// (Cogworks-internal alpha) carry the old key
/// <c>"LlmsTxt.Umbraco"</c>. Pre-1.0 = no production adopters; the key
/// flip is a one-time forward with no backwards-compat shim.
/// </para>
/// <para>
/// Adopters who own the doctype via uSync set
/// <c>AiVisibility:Migrations:SkipSettingsDoctype: true</c>;
/// <see cref="Composing.SettingsComposer"/> calls
/// <c>PackageMigrationPlans().Remove&lt;AiVisibilityPackageMigrationPlan&gt;()</c>
/// when the flag is set so this plan never enters the migration pipeline.
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
            // Story 5.1 — adds the request-log table backing the default
            // IRequestLog writer + LogRetentionJob. AddRequestLogTable_1_0's
            // historical TableName constant remains "llmsTxtRequestLog" per
            // the project-context.md immutability rule; the actual schema
            // landed by Create.Table<RequestLogEntry>() reflects the
            // entity's current [TableName] binding.
            .To<AddRequestLogTable_1_0>("9B3D7E4A-2C8F-4F1B-A5E0-7D9B2A6F1C8E")
            // Story 6.0c — rename the request-log table for pre-rename
            // alpha adopters. Drops llmsTxtRequestLog IF EXISTS, creates
            // aiVisibilityRequestLog IF NOT EXISTS. On fresh installs both
            // sub-operations are no-ops (step 1 already created the
            // new-named table via the entity's [TableName] binding).
            .To<RenameRequestLogTable_2_0>("4F3B2A8C-1D5E-4F7A-9B6C-8E0D2F4A6B1C");
}
