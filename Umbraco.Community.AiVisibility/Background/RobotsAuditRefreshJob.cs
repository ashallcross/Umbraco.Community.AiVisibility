using LlmsTxt.Umbraco.Configuration;
using Umbraco.Community.AiVisibility.Robots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace LlmsTxt.Umbraco.Background;

/// <summary>
/// Story 4.2 — recurring distributed background job that re-runs the robots
/// audit for every bound hostname on the configured cadence
/// (<see cref="RobotsAuditorSettings.RefreshIntervalHours"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="IDistributedBackgroundJob"/> and not
/// <see cref="IRecurringBackgroundJob"/>:</b> the audit is NOT
/// duplicate-safe. Two instances both fetching the same host's
/// <c>/robots.txt</c> per cycle would double-fetch and double-write the
/// cached state, doubling load against the adopter's origin. Umbraco's
/// <see cref="IDistributedBackgroundJob"/> coordinates a single executor
/// across instances via the host-DB lock
/// (<c>IDistributedJobService.TryTakeRunnableAsync</c>); empirically
/// validated by Spike 0.B against shared SQL Server 2022.
/// </para>
/// <para>
/// <b>Method shape:</b> <see cref="ExecuteAsync"/> is parameterless per the
/// canonical 17.3.2 surface (verified at
/// <c>~/.nuget/packages/umbraco.cms.infrastructure/17.3.2/lib/net10.0/Umbraco.Infrastructure.xml</c>
/// lines 60-64). <b>Architect note A5 references <c>RunJobAsync(CancellationToken)</c>
/// — that's drift</b> against the actual interface. Cancellation flows in
/// via <see cref="CancellationToken.None"/> per Umbraco-shipped distributed
/// job convention; the host's app-stopping signal is observed at the
/// hosted service / runner level above us.
/// </para>
/// <para>
/// <b>Idempotency:</b> re-running mid-cycle is safe. The job rewrites the
/// cached state at <c>llms:robots:{hostname}</c>; the Health Check view
/// reads cached state on demand, never on a schedule.
/// </para>
/// </remarks>
public sealed class RobotsAuditRefreshJob : IDistributedBackgroundJob
{
    /// <summary>
    /// Hard upper bound on RefreshIntervalHours. One year — well beyond any
    /// reasonable adopter cadence. Prevents <c>int.MaxValue</c> from
    /// overflowing <see cref="TimeSpan.FromHours"/>.
    /// </summary>
    internal const int MaxRefreshIntervalHours = 24 * 365;

    /// <summary>
    /// Hard upper bound on RefreshIntervalSecondsOverride. One day — long
    /// enough for any dev cadence, short enough to prevent overflow / silly
    /// values mistakenly committed to production appsettings.
    /// </summary>
    internal const int MaxRefreshIntervalSecondsOverride = 86_400;

    private const string RunLogTemplate =
        "Robots audit refresh job RUN — InstanceId={InstanceId} CycleStart={CycleStart}";

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<LlmsTxtSettings> _settings;
    private readonly ILogger<RobotsAuditRefreshJob> _logger;
    private readonly TimeProvider _timeProvider;

    // Concurrent-cycle guard — overlapping ExecuteAsync invocations (e.g.
    // when Period is shorter than the audit walk under a multi-host install)
    // short-circuit the second runner so cache writes don't race.
    private int _cycleInFlight;

    public RobotsAuditRefreshJob(
        IServiceProvider services,
        IOptionsMonitor<LlmsTxtSettings> settings,
        ILogger<RobotsAuditRefreshJob> logger,
        TimeProvider? timeProvider = null)
    {
        _services = services;
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "LlmsTxt:RobotsAuditRefresh";

    /// <inheritdoc />
    public TimeSpan Period
    {
        get
        {
            var auditorSettings = _settings.CurrentValue.RobotsAuditor;
            // Dev/test override has precedence — seconds-precision cycles for
            // the architect-A5 two-instance gate. See RobotsAuditorSettings.
            if (auditorSettings.RefreshIntervalSecondsOverride is { } secs and > 0)
            {
                var clampedSecs = Math.Min(secs, MaxRefreshIntervalSecondsOverride);
                return TimeSpan.FromSeconds(clampedSecs);
            }

            var hours = auditorSettings.RefreshIntervalHours;
            // Disabled → InfiniteTimeSpan, NOT TimeSpan.Zero. The Umbraco
            // distributed-job runner treats Zero as "fire immediately" which
            // produces a hot-loop of Trace-logged early-returns; Infinite is
            // the documented "never fire" sentinel for periodic-timer APIs.
            if (hours <= 0)
            {
                return Timeout.InfiniteTimeSpan;
            }
            // Clamp to a year so int.MaxValue doesn't overflow TimeSpan.FromHours.
            return TimeSpan.FromHours(Math.Min(hours, MaxRefreshIntervalHours));
        }
    }

    /// <inheritdoc />
    public async Task ExecuteAsync()
    {
        var auditorSettings = _settings.CurrentValue.RobotsAuditor;
        var hasSecondsOverride = auditorSettings.RefreshIntervalSecondsOverride is > 0;
        var hours = auditorSettings.RefreshIntervalHours;
        if (!hasSecondsOverride && hours <= 0)
        {
            _logger.LogTrace(
                "Robots audit refresh job — RefreshIntervalHours <= 0 and no seconds override, refresh disabled.");
            return;
        }

        // Concurrent-cycle guard. If a prior ExecuteAsync is still running
        // (long audit walk + short Period — e.g. multi-tenant install with
        // RefreshIntervalSecondsOverride), short-circuit the new invocation
        // so the auditor's per-host cache writes don't race themselves.
        if (Interlocked.CompareExchange(ref _cycleInFlight, 1, 0) != 0)
        {
            _logger.LogTrace(
                "Robots audit refresh job: prior cycle still in flight, skipping this tick.");
            return;
        }

        try
        {
            _logger.LogInformation(
                RunLogTemplate,
                ResolveInstanceId(),
                _timeProvider.GetUtcNow());

            using var scope = _services.CreateScope();
            var auditor = scope.ServiceProvider.GetRequiredService<IRobotsAuditor>();
            var domainService = scope.ServiceProvider.GetRequiredService<IDomainService>();

            // RefreshAsync is the public force-fresh contract on IRobotsAuditor.
            // DefaultRobotsAuditor bypasses cache on entry + re-inserts on exit;
            // adopter overrides that don't implement RefreshAsync fall through to
            // the default-method delegation to AuditAsync (their cache semantics,
            // their problem).
            var hostnames = ResolveHostnames(domainService, _logger);
            if (hostnames.Count == 0)
            {
                _logger.LogTrace(
                    "Robots audit refresh job: no hostnames to audit (no IDomain bindings).");
                return;
            }

            foreach (var (hostname, scheme) in hostnames)
            {
                try
                {
                    await auditor
                        .RefreshAsync(hostname, scheme, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Host is shutting down — fast-exit rather than logging
                    // warnings for each remaining host.
                    _logger.LogInformation(
                        "Robots audit refresh job: cancellation observed mid-cycle, exiting.");
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Robots audit refresh job: hostname {Hostname} failed",
                        hostname);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _cycleInFlight, 0);
        }
    }

    /// <summary>
    /// <see cref="Environment.MachineName"/> can throw or return empty in
    /// some sandboxed / containerised environments. The architect-A5 gate
    /// marker log line MUST emit a non-empty InstanceId for the
    /// <c>grep RUN</c> verification to work.
    /// </summary>
    private static string ResolveInstanceId()
    {
        try
        {
            var machine = Environment.MachineName;
            return string.IsNullOrEmpty(machine) ? "unknown" : machine;
        }
        catch
        {
            return "unknown";
        }
    }

    private static IReadOnlyList<(string Host, string Scheme)> ResolveHostnames(
        IDomainService domainService,
        ILogger logger)
    {
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
#pragma warning disable CS0618
            foreach (var domain in domainService.GetAll(includeWildcards: true))
#pragma warning restore CS0618
            {
                if (string.IsNullOrWhiteSpace(domain.DomainName))
                {
                    continue;
                }

                if (!Uri.TryCreate(domain.DomainName, UriKind.Absolute, out var absolute) &&
                    !Uri.TryCreate($"https://{domain.DomainName}", UriKind.Absolute, out absolute))
                {
                    logger.LogTrace(
                        "Robots audit refresh job: skipping IDomain {DomainName} — not parseable as a URL",
                        domain.DomainName);
                    continue;
                }

                var host = absolute.Host;
                if (!string.IsNullOrEmpty(host))
                {
                    hosts.TryAdd(host, absolute.Scheme);
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort — transient DB errors during the refresh shouldn't
            // kill the job. LogTrace so a permanent failure is observable in
            // verbose logs without spamming Warning on every cycle.
            logger.LogTrace(
                ex,
                "Robots audit refresh job: IDomainService.GetAll failed; this cycle has no hostnames to audit");
        }

        return hosts.Select(kv => (kv.Key, kv.Value)).ToArray();
    }
}
