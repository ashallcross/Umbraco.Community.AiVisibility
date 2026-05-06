using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace Umbraco.Community.AiVisibility.TestSite.Spikes.DistributedJob;

// SPIKE 0.B — implements `Umbraco.Cms.Infrastructure.BackgroundJobs.IDistributedBackgroundJob`
// (canonical v17 namespace, validated against Umbraco.Cms.Infrastructure 17.3.2 via
// `/tmp/probebuild` reflection probe — architecture.md said `Umbraco.Cms.Core.Sync.*`,
// which was wrong; that drift is captured in `0-b-spike-outcome.md`).
//
// AC4 contract:
//   - Two TestSite instances pointing at the same host DB
//   - Run for ≥ 130 seconds (≥ two cycles at 60s)
//   - Exactly two rows total — Umbraco's `IDistributedJobService` coordinates
//     `TryTakeRunnableAsync` over a host-DB lock so only one instance executes
//     each cycle
//
// Instance identity is captured per-row so the two-instance run can verify
// which node executed each cycle without relying on log inspection.
public sealed class SpikeDistributedJob : IDistributedBackgroundJob
{
    private readonly SpikeJobLogStore _store;
    private readonly ILogger<SpikeDistributedJob> _logger;
    private readonly IOptionsMonitor<SpikeDistributedJobOptions> _options;
    private static readonly string s_instanceId =
        $"{Environment.MachineName}/{Environment.ProcessId}";

    public SpikeDistributedJob(
        SpikeJobLogStore store,
        ILogger<SpikeDistributedJob> logger,
        IOptionsMonitor<SpikeDistributedJobOptions> options)
    {
        _store = store;
        _logger = logger;
        _options = options;
    }

    public string Name => "LlmsTxt.Spike.DistributedJob";

    public TimeSpan Period => _options.CurrentValue.Period;

    public Task ExecuteAsync()
    {
        DateTime now = DateTime.UtcNow;
        long cycleSequence = (long)Math.Floor((now - DateTime.UnixEpoch).TotalSeconds / Math.Max(1, Period.TotalSeconds));

        SpikeJobLogEntry entry = new()
        {
            CycleSequence = cycleSequence,
            ExecutedAt = now,
            InstanceId = s_instanceId,
        };

        _store.Insert(entry);

        _logger.LogInformation(
            "Spike 0.B distributed job executed {Cycle} on {InstanceId} at {ExecutedAt:o}",
            cycleSequence,
            s_instanceId,
            now);

        return Task.CompletedTask;
    }
}
