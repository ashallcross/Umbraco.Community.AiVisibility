using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Community.AiVisibility.Configuration;

namespace Umbraco.Community.AiVisibility.V13.Jobs;

/// <summary>
/// v13 recurring background job (Story 9.2) that deletes
/// <c>aiVisibilityRequestLog</c> rows older than
/// <see cref="LogRetentionSettings.DurationDays"/> on every cycle. The v13
/// counterpart of the v17 <c>LogRetentionJob</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coordination — honest best-effort, NOT exactly-once.</b> Umbraco v13 has no
/// <c>IDistributedBackgroundJob</c> (the host-DB-lock primitive ships in v17+), so
/// this implements <see cref="IRecurringBackgroundJob"/> and gates work on
/// <see cref="IServerRoleAccessor.CurrentServerRole"/> being
/// <see cref="ServerRole.SchedulingPublisher"/> OR <see cref="ServerRole.Single"/>.
/// On a load-balanced v13 backoffice the SchedulingPublisher role CAN be held by
/// more than one server, so duplicate execution is <b>possible</b>. That is
/// acceptable here because the write is idempotent: the DELETE removes the same
/// rows whichever instance runs it, and a second concurrent pass is a no-op (the
/// rows are already gone). Documented as a v13-vs-v17 coordination difference in
/// the v13 README (Story 9.4). The role is re-read every cycle (not gated once at
/// startup) so the boot-window <see cref="ServerRole.Unknown"/> state simply skips
/// an early tick until the heartbeat coordinator resolves roles
/// (project-context.md § Hosted services boot-time gating).
/// </para>
/// <para>
/// <b>Self-contained (Story 9.2 Option B).</b> The DELETE is co-located here rather
/// than extracted to a shared Core seam, so the v13 SKU sunsets as a clean
/// project-delete without disturbing Core/v17. The only shared knowledge is the
/// table name, which still lives in Core's <c>RequestLogEntry</c> annotation
/// (mirrored as a constant here for the raw DELETE).
/// </para>
/// </remarks>
public sealed class LogRetentionJob : IRecurringBackgroundJob
{
    internal const string TableName = "aiVisibilityRequestLog";
    internal const int MinDurationDays = 1;
    internal const int MaxDurationDays = 3650;
    internal const int MinRunIntervalHours = 1;
    internal const int MaxRunIntervalHours = 8760;
    internal const int MinRunIntervalSecondsOverride = 1;
    internal const int MaxRunIntervalSecondsOverride = 86_400;

    private const string RunLogTemplate =
        "AiVisibility (v13) log retention job RUN — InstanceId={InstanceId} ServerRole={ServerRole} CycleStart={CycleStart} RowsDeleted={RowsDeleted}";

    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly IScopeProvider _scopeProvider;
    private readonly IServerRoleAccessor _serverRoleAccessor;
    private readonly ILogger<LogRetentionJob> _logger;
    private readonly TimeProvider _timeProvider;

    private int _cycleInFlight;

    public LogRetentionJob(
        IOptionsMonitor<AiVisibilitySettings> settings,
        IScopeProvider scopeProvider,
        IServerRoleAccessor serverRoleAccessor,
        ILogger<LogRetentionJob> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
        _serverRoleAccessor = serverRoleAccessor ?? throw new ArgumentNullException(nameof(serverRoleAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Raise PeriodChanged when the run interval changes so the recurring-job
        // runner reschedules (optional enhancement per AC5; no v17 precedent —
        // v17 reads CurrentValue per cycle and never reschedules).
        _settings.OnChange(_ => PeriodChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <inheritdoc />
    public event EventHandler? PeriodChanged;

    /// <inheritdoc />
    public TimeSpan Period
    {
        get
        {
            var retention = _settings.CurrentValue.LogRetention;

            if (retention.DurationDays <= 0)
            {
                return Timeout.InfiniteTimeSpan;
            }

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
    public TimeSpan Delay => TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public Task RunJobAsync()
    {
        // v13 best-effort coordination — run only on the scheduling publisher or a
        // single-server install. Re-read every cycle (NOT a StartAsync-once gate)
        // so the boot-window Unknown role skips cleanly until roles resolve.
        var role = _serverRoleAccessor.CurrentServerRole;
        if (role is not (ServerRole.SchedulingPublisher or ServerRole.Single))
        {
            _logger.LogTrace(
                "AiVisibility (v13) log retention job: server role {ServerRole} is not SchedulingPublisher/Single, skipping.",
                role);
            return Task.CompletedTask;
        }

        var retention = _settings.CurrentValue.LogRetention;
        if (retention.DurationDays <= 0)
        {
            _logger.LogTrace("AiVisibility (v13) log retention job: DurationDays <= 0, retention disabled.");
            return Task.CompletedTask;
        }

        if (Interlocked.CompareExchange(ref _cycleInFlight, 1, 0) != 0)
        {
            _logger.LogTrace("AiVisibility (v13) log retention job: prior cycle still in flight, skipping this tick.");
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
                using var scope = _scopeProvider.CreateScope(IsolationLevel.ReadCommitted);
                rowsDeleted = scope.Database.Execute(
                    $"DELETE FROM {TableName} WHERE createdUtc < @0",
                    cutoff);
                scope.Complete();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AiVisibility (v13) log retention job: DELETE failed (cutoff {Cutoff:o}). Cycle will retry next tick.",
                    cutoff);
                return Task.CompletedTask;
            }

            _logger.LogInformation(RunLogTemplate, ResolveInstanceId(), role, cycleStart, rowsDeleted);
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
