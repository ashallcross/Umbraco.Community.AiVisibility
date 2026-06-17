using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Robots;

namespace Umbraco.Community.AiVisibility.V13.Jobs;

/// <summary>
/// v13 recurring background job (Story 9.2) that re-runs the robots audit for
/// every bound hostname on the configured cadence
/// (<see cref="RobotsAuditorSettings.RefreshIntervalHours"/>). The v13 counterpart
/// of the v17 <c>RobotsAuditRefreshJob</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coordination — honest best-effort, NOT exactly-once.</b> v13 has no
/// <c>IDistributedBackgroundJob</c>, so this implements
/// <see cref="IRecurringBackgroundJob"/> and gates on
/// <see cref="IServerRoleAccessor.CurrentServerRole"/> being
/// <see cref="ServerRole.SchedulingPublisher"/> or <see cref="ServerRole.Single"/>
/// (re-read every cycle, so the boot-window <see cref="ServerRole.Unknown"/> skips
/// cleanly). On a load-balanced v13 backoffice more than one server may hold the
/// SchedulingPublisher role, so duplicate execution is <b>possible</b>. The cached
/// audit state stays consistent under last-write-wins, BUT a duplicate cycle still
/// performs duplicate <b>origin work</b> — a second <c>/robots.txt</c> fetch + audit
/// per hostname. That is wasteful, not corrupting; documented as a v13-vs-v17
/// coordination difference in the v13 README (Story 9.4). v17's host-DB lock makes
/// this exactly-once; v13 cannot, and says so honestly.
/// </para>
/// <para>
/// <b>Self-contained (Story 9.2 Option B).</b> The per-hostname loop is thin wiring
/// over Core's <see cref="IRobotsAuditor.RefreshAsync"/> (no business logic
/// duplicated here), kept inside the V13 project so the SKU sunsets as a clean
/// delete.
/// </para>
/// </remarks>
public sealed class RobotsAuditRefreshJob : IRecurringBackgroundJob
{
    internal const int MaxRefreshIntervalHours = 24 * 365;
    internal const int MaxRefreshIntervalSecondsOverride = 86_400;

    private const string RunLogTemplate =
        "AiVisibility (v13) robots audit refresh job RUN — InstanceId={InstanceId} ServerRole={ServerRole} CycleStart={CycleStart}";

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly IServerRoleAccessor _serverRoleAccessor;
    private readonly ILogger<RobotsAuditRefreshJob> _logger;
    private readonly TimeProvider _timeProvider;

    private int _cycleInFlight;

    public RobotsAuditRefreshJob(
        IServiceProvider services,
        IOptionsMonitor<AiVisibilitySettings> settings,
        IServerRoleAccessor serverRoleAccessor,
        ILogger<RobotsAuditRefreshJob> logger,
        TimeProvider? timeProvider = null)
    {
        _services = services;
        _settings = settings;
        _serverRoleAccessor = serverRoleAccessor;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _settings.OnChange(_ => PeriodChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <inheritdoc />
    public event EventHandler? PeriodChanged;

    /// <inheritdoc />
    public TimeSpan Period
    {
        get
        {
            var auditorSettings = _settings.CurrentValue.RobotsAuditor;
            if (auditorSettings.RefreshIntervalSecondsOverride is { } secs and > 0)
            {
                var clampedSecs = Math.Min(secs, MaxRefreshIntervalSecondsOverride);
                return TimeSpan.FromSeconds(clampedSecs);
            }

            var hours = auditorSettings.RefreshIntervalHours;
            if (hours <= 0)
            {
                return Timeout.InfiniteTimeSpan;
            }
            return TimeSpan.FromHours(Math.Min(hours, MaxRefreshIntervalHours));
        }
    }

    /// <inheritdoc />
    public TimeSpan Delay => TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public async Task RunJobAsync()
    {
        var role = _serverRoleAccessor.CurrentServerRole;
        if (role is not (ServerRole.SchedulingPublisher or ServerRole.Single))
        {
            _logger.LogTrace(
                "AiVisibility (v13) robots audit refresh job: server role {ServerRole} is not SchedulingPublisher/Single, skipping.",
                role);
            return;
        }

        var auditorSettings = _settings.CurrentValue.RobotsAuditor;
        var hasSecondsOverride = auditorSettings.RefreshIntervalSecondsOverride is > 0;
        var hours = auditorSettings.RefreshIntervalHours;
        if (!hasSecondsOverride && hours <= 0)
        {
            _logger.LogTrace(
                "AiVisibility (v13) robots audit refresh job — RefreshIntervalHours <= 0 and no seconds override, refresh disabled.");
            return;
        }

        if (Interlocked.CompareExchange(ref _cycleInFlight, 1, 0) != 0)
        {
            _logger.LogTrace("AiVisibility (v13) robots audit refresh job: prior cycle still in flight, skipping this tick.");
            return;
        }

        try
        {
            _logger.LogInformation(RunLogTemplate, ResolveInstanceId(), role, _timeProvider.GetUtcNow());

            using var scope = _services.CreateScope();
            var auditor = scope.ServiceProvider.GetRequiredService<IRobotsAuditor>();
            var domainService = scope.ServiceProvider.GetRequiredService<IDomainService>();

            var hostnames = ResolveHostnames(domainService, _logger);
            if (hostnames.Count == 0)
            {
                _logger.LogTrace("AiVisibility (v13) robots audit refresh job: no hostnames to audit (no IDomain bindings).");
                return;
            }

            foreach (var (hostname, scheme) in hostnames)
            {
                try
                {
                    await auditor.RefreshAsync(hostname, scheme, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("AiVisibility (v13) robots audit refresh job: cancellation observed mid-cycle, exiting.");
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AiVisibility (v13) robots audit refresh job: hostname {Hostname} failed", hostname);
                }
            }
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
                        "AiVisibility (v13) robots audit refresh job: skipping IDomain {DomainName} — not parseable as a URL",
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
            logger.LogTrace(
                ex,
                "AiVisibility (v13) robots audit refresh job: IDomainService.GetAll failed; this cycle has no hostnames to audit");
        }

        return hosts.Select(kv => (kv.Key, kv.Value)).ToArray();
    }
}
