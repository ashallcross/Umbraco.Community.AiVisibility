using Umbraco.Community.AiVisibility.Persistence.Entities;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Install;

namespace Umbraco.Community.AiVisibility.Persistence.Migrations;

/// <summary>
/// Story 6.0c — pre-rename adopters' request-log table is named
/// <c>llmsTxtRequestLog</c>; this step drops the old table (if present) and
/// creates the new <c>aiVisibilityRequestLog</c> table from the
/// <see cref="RequestLogEntry"/> entity (whose <c>[TableName]</c> annotation
/// post-rename binds to the new name).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pre-1.0 = no production data preserved.</b> The request log is
/// operational telemetry (what AI agents hit, when), not source-of-truth
/// state. A data-preserving rename
/// (<c>INSERT INTO aiVisibilityRequestLog SELECT * FROM llmsTxtRequestLog</c>)
/// would add surface area for a one-time concern. Drop+recreate is the
/// simpler path; spec ratifies (Story 6.0c What NOT to Build).
/// </para>
/// <para>
/// <b>Fresh install behaviour:</b> step 1
/// (<see cref="AddRequestLogTable_1_0"/>) creates
/// <c>aiVisibilityRequestLog</c> via <c>Create.Table&lt;RequestLogEntry&gt;()</c>
/// because the entity's <c>[TableName]</c> already points there. This step
/// then drops <c>llmsTxtRequestLog</c> (no-op — never existed) and
/// short-circuits the create via <c>TableExists</c> (table from step 1
/// matches). End-state: a fresh install ends up with a single
/// <c>aiVisibilityRequestLog</c> table.
/// </para>
/// <para>
/// <b>Alpha-adopter behaviour:</b> step 1 was already marked complete in
/// <c>umbracoMigrations</c> on the alpha DB (it created
/// <c>llmsTxtRequestLog</c> when it ran originally). This step now drops
/// that table + creates <c>aiVisibilityRequestLog</c>. No row data is
/// preserved — pre-1.0 acceptable per spec.
/// </para>
/// </remarks>
public sealed class RenameRequestLogTable_2_0 : AsyncMigrationBase
{
    internal const string OldTableName = "llmsTxtRequestLog";
    internal const string NewTableName = "aiVisibilityRequestLog";

    private readonly DatabaseSchemaCreatorFactory _schemaCreatorFactory;
    private readonly ILogger<RenameRequestLogTable_2_0> _logger;

    public RenameRequestLogTable_2_0(
        IMigrationContext context,
        DatabaseSchemaCreatorFactory schemaCreatorFactory,
        ILogger<RenameRequestLogTable_2_0> logger)
        : base(context)
    {
        _schemaCreatorFactory = schemaCreatorFactory;
        _logger = logger;
    }

    protected override Task MigrateAsync()
    {
        var schemaCreator = _schemaCreatorFactory.Create(Database);

        if (schemaCreator.TableExists(OldTableName))
        {
            Delete.Table(OldTableName).Do();
            _logger.LogInformation(
                "AiVisibility: RenameRequestLogTable_2_0 — dropped legacy table {OldTableName}.",
                OldTableName);
        }
        else
        {
            _logger.LogDebug(
                "AiVisibility: RenameRequestLogTable_2_0 — legacy table {OldTableName} not present; nothing to drop.",
                OldTableName);
        }

        if (schemaCreator.TableExists(NewTableName))
        {
            _logger.LogDebug(
                "AiVisibility: RenameRequestLogTable_2_0 — table {NewTableName} already exists; short-circuiting create.",
                NewTableName);
            return Task.CompletedTask;
        }

        Create.Table<RequestLogEntry>().Do();

        _logger.LogInformation(
            "AiVisibility: RenameRequestLogTable_2_0 — created table {NewTableName}.",
            NewTableName);

        return Task.CompletedTask;
    }
}
