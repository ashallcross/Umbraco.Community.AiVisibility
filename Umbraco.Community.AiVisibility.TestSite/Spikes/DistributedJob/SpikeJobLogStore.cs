using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Umbraco.Community.AiVisibility.TestSite.Spikes.DistributedJob;

// SPIKE 0.B — wraps NPoco access for the spike's execution-log table.
// Creates the table on demand using flavor-aware DDL (SQLite + SQL Server),
// which keeps the harness usable both on the default TestSite SQLite DB
// (single-instance smoke test) and on a shared SQL Server DB (the AC4
// two-instance run). Story 5.1's production log table uses a proper
// `MigrationPlan` instead of try-create-on-first-write — this is spike scope.
//
// The `Database` property lives on `Umbraco.Cms.Infrastructure.Scoping.IScope`
// (NOT `Umbraco.Cms.Core.Scoping.ICoreScope`), so the harness depends on the
// Infrastructure-flavoured `IScopeProvider` rather than the Core-flavoured
// `ICoreScopeProvider`. Probe-confirmed against Umbraco 17.3.2.
public sealed class SpikeJobLogStore
{
    private readonly IScopeProvider _scopeProvider;
    private readonly ILogger<SpikeJobLogStore> _logger;
    private int _ensured;

    public SpikeJobLogStore(IScopeProvider scopeProvider, ILogger<SpikeJobLogStore> logger)
    {
        _scopeProvider = scopeProvider;
        _logger = logger;
    }

    public void EnsureTable()
    {
        if (Volatile.Read(ref _ensured) == 1)
        {
            return;
        }

        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        IUmbracoDatabase db = scope.Database;
        string flavour = db.DatabaseType.GetType().Name;

        if (flavour.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
        {
            db.Execute($@"CREATE TABLE IF NOT EXISTS {SpikeJobLogTable.TableName} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                cycleSequence INTEGER NOT NULL,
                executedAt TEXT NOT NULL,
                instanceId TEXT NOT NULL)");
        }
        else
        {
            db.Execute($@"IF OBJECT_ID(N'dbo.{SpikeJobLogTable.TableName}') IS NULL
                CREATE TABLE dbo.{SpikeJobLogTable.TableName} (
                    id INT IDENTITY(1,1) PRIMARY KEY,
                    cycleSequence BIGINT NOT NULL,
                    executedAt DATETIME2 NOT NULL,
                    instanceId NVARCHAR(128) NOT NULL)");
        }

        // Flip the cache flag only after the DDL succeeds — if `db.Execute`
        // throws (perms, race, network), the next call must retry rather than
        // skip and then fail forever with "table missing".
        Interlocked.Exchange(ref _ensured, 1);

        _logger.LogInformation(
            "Spike 0.B distributed-job log table ensured on {DatabaseType}",
            flavour);
    }

    public void Insert(SpikeJobLogEntry entry)
    {
        EnsureTable();
        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        scope.Database.Insert(entry);
    }

    public IReadOnlyList<SpikeJobLogEntry> ReadAll()
    {
        EnsureTable();
        using IScope scope = _scopeProvider.CreateScope(autoComplete: true);
        return scope.Database
            .Fetch<SpikeJobLogEntry>($"SELECT * FROM {SpikeJobLogTable.TableName} ORDER BY id ASC");
    }
}
