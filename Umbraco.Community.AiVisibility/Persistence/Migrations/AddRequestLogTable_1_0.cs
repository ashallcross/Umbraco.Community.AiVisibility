using Umbraco.Community.AiVisibility.Persistence.Entities;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Install;

namespace Umbraco.Community.AiVisibility.Persistence.Migrations;

/// <summary>
/// Story 5.1 — creates the <c>llmsTxtRequestLog</c> table for the package's
/// default <see cref="IRequestLog"/> writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency:</b> short-circuits via
/// <see cref="DatabaseSchemaCreator.TableExists(string)"/> when the table
/// already exists (uSync re-run, prior install with state-record loss).
/// </para>
/// <para>
/// <b>Spec Drift Note (Story 5.1):</b> the canonical pre-flight identified
/// <c>UmbracoDatabaseExtensions.HasTable(IUmbracoDatabase, string)</c> as
/// the idempotency check, sourced from <c>Umbraco.Infrastructure.xml</c>
/// line 8357. At implementation time the type proved to be marked
/// <c>internal</c> in the compiled assembly (xml docs lied about
/// visibility), so we pivoted to <see cref="DatabaseSchemaCreatorFactory"/>
/// + <see cref="DatabaseSchemaCreator.TableExists(string)"/> — both public
/// per reflection probe.
/// </para>
/// <para>
/// <b>Schema source of truth:</b> <see cref="RequestLogEntry"/>'s
/// NPoco annotations
/// (<see cref="Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations"/>).
/// </para>
/// <para>
/// <b>Immutable once shipped (AR7).</b> Future schema changes go in a new
/// <c>AddXxx_1_1</c> migration class chained into
/// <see cref="AiVisibilityPackageMigrationPlan"/>. Story 6.0c (2026-05-06)
/// kept <see cref="TableName"/>'s value <c>llmsTxtRequestLog</c> verbatim
/// per project-context.md immutability; the entity's <c>[TableName]</c>
/// binding flipped to <c>aiVisibilityRequestLog</c> in lockstep with
/// <see cref="RenameRequestLogTable_2_0"/> so the actual schema
/// landed by <c>Create.Table&lt;RequestLogEntry&gt;()</c> reflects the
/// post-rename name on fresh installs.
/// </para>
/// </remarks>
public sealed class AddRequestLogTable_1_0 : AsyncMigrationBase
{
    internal const string TableName = "llmsTxtRequestLog";

    private readonly DatabaseSchemaCreatorFactory _schemaCreatorFactory;
    private readonly ILogger<AddRequestLogTable_1_0> _logger;

    public AddRequestLogTable_1_0(
        IMigrationContext context,
        DatabaseSchemaCreatorFactory schemaCreatorFactory,
        ILogger<AddRequestLogTable_1_0> logger)
        : base(context)
    {
        _schemaCreatorFactory = schemaCreatorFactory;
        _logger = logger;
    }

    protected override Task MigrateAsync()
    {
        var schemaCreator = _schemaCreatorFactory.Create(Database);
        if (schemaCreator.TableExists(TableName))
        {
            _logger.LogDebug(
                "AiVisibility: AddRequestLogTable_1_0 — table {TableName} already exists; short-circuiting.",
                TableName);
            return Task.CompletedTask;
        }

        Create.Table<RequestLogEntry>().Do();

        _logger.LogInformation(
            "AiVisibility: AddRequestLogTable_1_0 — created table {TableName}.",
            TableName);

        return Task.CompletedTask;
    }
}
