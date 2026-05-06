using NPoco;

namespace Umbraco.Community.AiVisibility.TestSite.Spikes.DistributedJob;

// SPIKE 0.B — NPoco POCO for the TestSite-only execution-log table.
// Lives in TestSite, NOT in the package project. The package's real
// log table (`llmsTxtRequestLog`) ships in Story 5.1 with a proper
// `MigrationPlan`; this POCO is throwaway scaffolding for AC4.
[TableName(SpikeJobLogTable.TableName)]
[PrimaryKey("id", AutoIncrement = true)]
public sealed class SpikeJobLogEntry
{
    [Column("id")]
    public int Id { get; set; }

    [Column("cycleSequence")]
    public long CycleSequence { get; set; }

    [Column("executedAt")]
    public DateTime ExecutedAt { get; set; }

    [Column("instanceId")]
    public string InstanceId { get; set; } = string.Empty;
}

internal static class SpikeJobLogTable
{
    public const string TableName = "llmsSpikeJobLog";
}
