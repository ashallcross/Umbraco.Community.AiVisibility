using Umbraco.Community.AiVisibility.Persistence.Migrations;

namespace Umbraco.Community.AiVisibility.Tests.Persistence.Migrations;

[TestFixture]
public class RenameRequestLogTable_2_0Tests
{
    // Story 6.0c — migration step that renames the request-log table from
    // the pre-rename name `llmsTxtRequestLog` to `aiVisibilityRequestLog`.
    // Behavioural gate (drop+recreate against a real DB) is exercised by
    // Manual Gate Step 5 — same shape as AddRequestLogTable_1_0Tests
    // (the DDL-emitting layer is not unit-testable via NSubstitute because
    // DatabaseSchemaCreator's ctor isn't DynamicProxy-friendly, per Story
    // 5.1's Spec Drift Note).
    //
    // Per Story 6.0c DoD #3 ("near-zero net new tests; single test pinning
    // the table-name + schema for the new step"), this file deliberately
    // ships ONLY the table-name-constants pin — ctor + type-shape pins are
    // discharged by the symmetry with AddRequestLogTable_1_0's existing
    // tests (which were established at Story 5.1 baseline and survive the
    // rename). The schema pin (column shape, indexes) is exercised by
    // `Create.Table<RequestLogEntry>().Do()` reading the entity's NPoco
    // annotations — pinned indirectly through Story 5.1's tests + the
    // manual gate Step 5 walk.

    [Test]
    public void TableNameConstants_PinPreAndPostRenameNames()
    {
        // Pinning both names: a typo on either becomes a visible test
        // failure rather than a silent host-DB schema corruption against
        // adopter installs (the OLD name is the legacy table that Step 2
        // drops; the NEW name must match RequestLogEntry's [TableName]
        // binding so post-rename runtime queries succeed).
        Assert.Multiple(() =>
        {
            Assert.That(RenameRequestLogTable_2_0.OldTableName, Is.EqualTo("llmsTxtRequestLog"));
            Assert.That(RenameRequestLogTable_2_0.NewTableName, Is.EqualTo("aiVisibilityRequestLog"));
        });
    }
}
