using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Install;
using Umbraco.Community.AiVisibility.Persistence.Entities;

namespace Umbraco.Community.AiVisibility.V13.Persistence.Migrations;

/// <summary>
/// v13 variant (Story 9.2) — creates the <c>aiVisibilityRequestLog</c> table for
/// the package's default <c>IRequestLog</c> writer + the v13 LogRetentionJob.
/// </summary>
/// <remarks>
/// <para>
/// Synchronous <see cref="MigrationBase"/> (v13 has no <c>AsyncMigrationBase</c>).
/// Unlike the v17 <c>AddRequestLogTable_1_0</c> — which probes the historical
/// <c>llmsTxtRequestLog</c> constant and relies on a follow-up rename step — the
/// v13 SKU is brand new with no pre-rename adopters, so this checks the FINAL name
/// <c>aiVisibilityRequestLog</c> directly and there is no rename step in the v13
/// plan (see <see cref="AiVisibilityPackageMigrationPlan"/>). Per AC6 / Spec Drift
/// SDN-1.
/// </para>
/// <para>
/// Schema source of truth is Core's <see cref="RequestLogEntry"/> NPoco
/// annotations (its <c>[TableName]</c> already binds <c>aiVisibilityRequestLog</c>),
/// reused unchanged so the v13 and v17 tables are byte-identical.
/// </para>
/// </remarks>
public sealed class AddRequestLogTable : MigrationBase
{
    internal const string TableName = "aiVisibilityRequestLog";

    private readonly DatabaseSchemaCreatorFactory _schemaCreatorFactory;
    private readonly ILogger<AddRequestLogTable> _logger;

    public AddRequestLogTable(
        IMigrationContext context,
        DatabaseSchemaCreatorFactory schemaCreatorFactory,
        ILogger<AddRequestLogTable> logger)
        : base(context)
    {
        _schemaCreatorFactory = schemaCreatorFactory;
        _logger = logger;
    }

    protected override void Migrate()
    {
        var schemaCreator = _schemaCreatorFactory.Create(Database);
        if (schemaCreator.TableExists(TableName))
        {
            _logger.LogDebug(
                "AiVisibility (v13): AddRequestLogTable — table {TableName} already exists; short-circuiting.",
                TableName);
            return;
        }

        Create.Table<RequestLogEntry>().Do();

        _logger.LogInformation(
            "AiVisibility (v13): AddRequestLogTable — created table {TableName}.",
            TableName);
    }
}
