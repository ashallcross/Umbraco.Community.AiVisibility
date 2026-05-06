using System.Data;
using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Umbraco.Community.AiVisibility.Telemetry;

/// <summary>
/// Story 5.1 — recurring distributed background job that deletes
/// <c>llmsTxtRequestLog</c> rows older than
/// <see cref="LogRetentionSettings.DurationDays"/> on every cycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="IDistributedBackgroundJob"/>:</b> the DELETE is a
/// shared-host-DB write. With <see cref="IRecurringBackgroundJob"/> N
/// instances would each run the DELETE per cycle, dog-piling the host
/// DB with redundant work. Umbraco's distributed-job runner serialises
/// the cycle to a single instance via host-DB-lock coordination — same
/// shape Story 4.2's <c>RobotsAuditRefreshJob</c> proves.
/// </para>
/// <para>
/// <b>Method shape:</b> <see cref="ExecuteAsync"/> is parameterless per
/// the canonical Umbraco.Infrastructure 17.3.2 surface. Architect note A5
/// references <c>RunJobAsync(CancellationToken)</c> — that's drift; same
/// drift Story 4.2 Spec Drift Note #1 codified.
/// </para>
/// <para>
/// <b>Direct DB access via Infrastructure-flavour
/// <see cref="IScopeProvider"/>.</b> The DELETE goes through
/// <c>scope.Database.Execute(sql, args)</c> — <see cref="IScope.Database"/>
/// only lives on the Infrastructure scope. Architect note A5 step 2 +
/// architecture.md line 350 + Story 4.2 Spec Drift Note #2 reconciliation
/// converge on this.
/// </para>
/// <para>
/// <b>Disable contract:</b> <see cref="LogRetentionSettings.DurationDays"/>
/// or <see cref="LogRetentionSettings.RunIntervalHours"/> ≤ 0 returns
/// <see cref="Timeout.InfiniteTimeSpan"/> from <see cref="Period"/> (per
/// Story 4.2 chunk-3 P2 ratification — <see cref="TimeSpan.Zero"/> causes
/// the runner hot-loop). <see cref="ExecuteAsync"/> defensively
/// short-circuits in case the runtime config flipped between Period read
/// and ExecuteAsync.
/// </para>
/// </remarks>
public sealed class LogRetentionJob : IDistributedBackgroundJob
{
    internal const string TableName = "llmsTxtRequestLog";
    internal const int MinDurationDays = 1;
    internal const int MaxDurationDays = 3650;
    internal const int MinRunIntervalHours = 1;
    internal const int MaxRunIntervalHours = 8760;
    internal const int MinRunIntervalSecondsOverride = 1;
    internal const int MaxRunIntervalSecondsOverride = 86_400;

    private const string RunLogTemplate =
        "LlmsTxt log retention job RUN — InstanceId={InstanceId} CycleStart={CycleStart} RowsDeleted={RowsDeleted}";

    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly IScopeProvider _scopeProvider;
    private readonly ILogger<LogRetentionJob> _logger;
    private readonly TimeProvider _timeProvider;

    private int _cycleInFlight;

    public LogRetentionJob(
        IOptionsMonitor<AiVisibilitySettings> settings,
        IScopeProvider scopeProvider,
        ILogger<LogRetentionJob> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "LlmsTxt:LogRetention";

    /// <inheritdoc />
    public TimeSpan Period
    {
        get
        {
            var retention = _settings.CurrentValue.LogRetention;

            // DurationDays <= 0 disables the job entirely (no DELETE at all).
            if (retention.DurationDays <= 0)
            {
                return Timeout.InfiniteTimeSpan;
            }

            // Dev/test seconds override has precedence — same shape as Story 4.2.
            if (retention.RunIntervalSecondsOverride is { } secs and > 0)
            {
                var clampedSecs = Math.Clamp(secs, MinRunIntervalSecondsOverride, MaxRunIntervalSecondsOverride);
                return TimeSpan.FromSeconds(clampedSecs);
            }

            var hours = retention.RunIntervalHours;
            if (hours <= 0)
            {
                return Timeout.InfiniteTimeSpan;
            }
            return TimeSpan.FromHours(Math.Clamp(hours, MinRunIntervalHours, MaxRunIntervalHours));
        }
    }

    /// <inheritdoc />
    public Task ExecuteAsync()
    {
        var retention = _settings.CurrentValue.LogRetention;

        // Defensive: a runtime config flip between Period and ExecuteAsync
        // could land us here even when DurationDays is 0. Skip cleanly.
        if (retention.DurationDays <= 0)
        {
            _logger.LogTrace(
                "LlmsTxt log retention job: DurationDays <= 0, retention disabled.");
            return Task.CompletedTask;
        }

        // Concurrent-cycle guard — short Period + slow DELETE could
        // overlap; second runner short-circuits.
        if (Interlocked.CompareExchange(ref _cycleInFlight, 1, 0) != 0)
        {
            _logger.LogTrace(
                "LlmsTxt log retention job: prior cycle still in flight, skipping this tick.");
            return Task.CompletedTask;
        }

        try
        {
            var cycleStart = _timeProvider.GetUtcNow();
            var clampedDays = Math.Clamp(retention.DurationDays, MinDurationDays, MaxDurationDays);
            var cutoff = cycleStart.UtcDateTime - TimeSpan.FromDays(clampedDays);

            int rowsDeleted;
            try
            {
                // AC9 — Infrastructure-flavour scope opens with
                // ReadCommitted isolation + Default repository cache mode.
                using var scope = _scopeProvider.CreateScope(IsolationLevel.ReadCommitted);
                rowsDeleted = scope.Database.Execute(
                    $"DELETE FROM {TableName} WHERE createdUtc < @0",
                    cutoff);
                scope.Complete();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "LlmsTxt log retention job: DELETE failed (cutoff {Cutoff:o}). " +
                    "Cycle will retry next tick.",
                    cutoff);
                return Task.CompletedTask;
            }

            _logger.LogInformation(
                RunLogTemplate,
                ResolveInstanceId(),
                cycleStart,
                rowsDeleted);

            return Task.CompletedTask;
        }
        finally
        {
            Interlocked.Exchange(ref _cycleInFlight, 0);
        }
    }

    private static string ResolveInstanceId()
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return "unknown";
        }
    }
}
