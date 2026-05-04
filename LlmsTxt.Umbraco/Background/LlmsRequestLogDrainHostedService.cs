using System.Data;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Persistence;
using LlmsTxt.Umbraco.Persistence.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.Scoping;

namespace LlmsTxt.Umbraco.Background;

/// <summary>
/// Story 5.1 — drains <see cref="DefaultLlmsRequestLog"/>'s bounded channel
/// to <c>llmsTxtRequestLog</c> in batches. Singleton lifetime via
/// <c>services.AddHostedService&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Boot is never blocked.</b> <see cref="StartAsync"/> spins the drain
/// loop on the thread pool and returns immediately — same shape Story 4.2
/// <c>StartupRobotsAuditRunner</c> locked.
/// </para>
/// <para>
/// <b>Server-role gate:</b> drains only when
/// <see cref="IServerRoleAccessor.CurrentServerRole"/> is
/// <see cref="ServerRole.SchedulingPublisher"/> or
/// <see cref="ServerRole.Single"/>. Multi-instance front-end servers all
/// hold their own per-process <see cref="DefaultLlmsRequestLog"/> channel
/// — the writer's <c>DropOldest</c> semantics shed entries on those
/// instances; only the scheduling instance's drain reaches the host DB.
/// Documented in <c>docs/extension-points.md</c> as "best-effort logging
/// by design — adopters needing durable analytics override
/// <see cref="ILlmsRequestLog"/>".
/// </para>
/// <para>
/// <b>Adopter override:</b> when an adopter registers a custom
/// <see cref="ILlmsRequestLog"/> (e.g. App Insights writer), the runtime
/// resolves their type, NOT <see cref="DefaultLlmsRequestLog"/>. The
/// drainer's cast at <see cref="StartAsync"/> sees a non-default writer,
/// logs Trace, and exits — the adopter's writer owns its own persistence
/// path. No drain runs against an unknown channel.
/// </para>
/// <para>
/// <b>DB writes via Infrastructure-flavour <see cref="IScopeProvider"/>.</b>
/// <see cref="IScope.Database"/> exposes the host's
/// <see cref="Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase"/>
/// (which is also <c>NPoco.IDatabase</c>). The drainer calls
/// <c>scope.Database.InsertBulk(batch)</c> — NPoco picks the bulk-insert
/// strategy per DB provider (parameterised statement on SQLite, INSERT
/// INTO ... VALUES on SQL Server). Architect-A5 + Story 4.2 Spec Drift
/// Note #2 reconciliation: direct DB access mandates Infrastructure
/// flavour, NOT Core.
/// </para>
/// </remarks>
public sealed class LlmsRequestLogDrainHostedService : IHostedService, IAsyncDisposable
{
    internal const int MinBatchSize = 1;
    internal const int MaxBatchSize = 1000;
    internal const int MinMaxBatchIntervalSeconds = 1;
    internal const int MaxMaxBatchIntervalSeconds = 60;
    internal static readonly TimeSpan StopGraceWindow = TimeSpan.FromSeconds(5);

    private readonly ILlmsRequestLog _requestLog;
    private readonly IScopeProvider _scopeProvider;
    private readonly IOptionsMonitor<LlmsTxtSettings> _settings;
    private readonly IServerRoleAccessor _serverRoleAccessor;
    private readonly ILogger<LlmsRequestLogDrainHostedService> _logger;

    private CancellationTokenSource? _drainCts;
    private Task? _drainLoop;

    public LlmsRequestLogDrainHostedService(
        ILlmsRequestLog requestLog,
        IScopeProvider scopeProvider,
        IOptionsMonitor<LlmsTxtSettings> settings,
        IServerRoleAccessor serverRoleAccessor,
        ILogger<LlmsRequestLogDrainHostedService> logger)
    {
        _requestLog = requestLog ?? throw new ArgumentNullException(nameof(requestLog));
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _serverRoleAccessor = serverRoleAccessor ?? throw new ArgumentNullException(nameof(serverRoleAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue.RequestLog;
        if (!settings.Enabled)
        {
            _logger.LogInformation(
                "LlmsTxt request log drainer suppressed (LlmsTxt:RequestLog:Enabled is false). " +
                "Notifications still fire to all subscribed handlers; only the default DB writer is parked.");
            return Task.CompletedTask;
        }

        var role = _serverRoleAccessor.CurrentServerRole;
        // Permit Unknown alongside SchedulingPublisher / Single. Umbraco's
        // ServerRegistrationService heartbeat doesn't run until ~15s after
        // boot (`TouchServerJob` Delay), so StartAsync sees Unknown on
        // every fresh boot. Treating Unknown as drain-permitted is safe:
        // (a) single-instance dev: there's no other instance to compete;
        // (b) multi-instance broken-cluster: each instance drains its own
        //     per-process channel — distinct rows, no duplicate writes.
        // Subscriber explicitly excluded because the elected scheduler will
        // own the drain on a healthy cluster.
        if (role == ServerRole.Subscriber)
        {
            _logger.LogInformation(
                "LlmsTxt request log drainer suppressed on server role {Role} — only the SchedulingPublisher / Single instance drains its channel to the host DB.",
                role);
            return Task.CompletedTask;
        }

        // Adopter override path: a custom ILlmsRequestLog owns its own
        // persistence — there's no channel for us to drain.
        if (_requestLog is not DefaultLlmsRequestLog defaultLog)
        {
            _logger.LogInformation(
                "LlmsTxt request log drainer not started: a custom ILlmsRequestLog ({TypeName}) is registered; " +
                "the adopter's writer owns its own persistence path.",
                _requestLog.GetType().FullName);
            return Task.CompletedTask;
        }

        _drainCts = new CancellationTokenSource();
        var drainerToken = _drainCts.Token;
        _drainLoop = Task.Run(
            () => DrainLoopAsync(defaultLog, drainerToken),
            drainerToken);

        _logger.LogInformation(
            "LlmsTxt request log drainer started (server role {Role}, batch size {BatchSize}, max batch interval {Interval}s).",
            role,
            Math.Clamp(settings.BatchSize, MinBatchSize, MaxBatchSize),
            Math.Clamp(settings.MaxBatchIntervalSeconds, MinMaxBatchIntervalSeconds, MaxMaxBatchIntervalSeconds));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_drainCts is null || _drainLoop is null)
        {
            return;
        }

        try
        {
            _drainCts.Cancel();
            // Give the drainer up to StopGraceWindow to flush its current
            // in-flight batch before the host shuts down.
            using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            graceCts.CancelAfter(StopGraceWindow);
            await Task.WhenAny(_drainLoop, Task.Delay(StopGraceWindow, graceCts.Token))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop completed via cancellation — nothing to do.
        }
    }

    private async Task DrainLoopAsync(DefaultLlmsRequestLog log, CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue.RequestLog;
        var batchSize = Math.Clamp(settings.BatchSize, MinBatchSize, MaxBatchSize);
        var maxBatchInterval = TimeSpan.FromSeconds(Math.Clamp(
            settings.MaxBatchIntervalSeconds,
            MinMaxBatchIntervalSeconds,
            MaxMaxBatchIntervalSeconds));

        var batch = new List<LlmsTxtRequestLogEntry>(batchSize);
        var channelCompleted = false;

        while (!cancellationToken.IsCancellationRequested && !channelCompleted)
        {
            try
            {
                using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                batchCts.CancelAfter(maxBatchInterval);

                try
                {
                    while (batch.Count < batchSize)
                    {
                        var canRead = await log.Reader.WaitToReadAsync(batchCts.Token).ConfigureAwait(false);
                        if (!canRead)
                        {
                            // Channel writer was completed — drain anything
                            // remaining and exit the outer loop. Avoids the
                            // tight CPU spin where WaitToReadAsync returns
                            // false synchronously every iteration.
                            channelCompleted = true;
                            while (batch.Count < batchSize && log.Reader.TryRead(out var entry))
                            {
                                batch.Add(entry);
                            }
                            break;
                        }

                        while (batch.Count < batchSize && log.Reader.TryRead(out var entry))
                        {
                            batch.Add(entry);
                        }
                    }
                }
                catch (OperationCanceledException) when (batchCts.IsCancellationRequested
                                                         && !cancellationToken.IsCancellationRequested)
                {
                    // Max-batch-interval elapsed — flush whatever we have.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                FlushBatch(batch);
                batch.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "LlmsTxt request log drainer: unhandled exception during drain cycle. " +
                    "Discarding {BatchCount} entries; loop continues.",
                    batch.Count);
                batch.Clear();
            }
        }

        // Final flush on shutdown — best-effort, no exception escape.
        try
        {
            while (log.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
                if (batch.Count >= batchSize)
                {
                    FlushBatch(batch);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                FlushBatch(batch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LlmsTxt request log drainer: shutdown flush failed.");
        }
    }

    private void FlushBatch(IReadOnlyList<LlmsTxtRequestLogEntry> batch)
    {
        try
        {
            // AC9 — Infrastructure-flavour scope opens with ReadCommitted
            // isolation + Default repository cache mode.
            using var scope = _scopeProvider.CreateScope(IsolationLevel.ReadCommitted);
            scope.Database.InsertBulk(batch);
            scope.Complete();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LlmsTxt request log drainer: DB write failed for batch of {BatchCount}. " +
                "Current batch entries are dropped; subsequent cycles' entries are unaffected " +
                "(this is best-effort logging by design).",
                batch.Count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_drainCts is not null)
        {
            try
            {
                _drainCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed — nothing to do.
            }
        }

        if (_drainLoop is not null)
        {
            try
            {
                await _drainLoop.ConfigureAwait(false);
            }
            catch
            {
                // The loop never throws (per outer try/catch above) but
                // defensively swallow on dispose.
            }
        }

        _drainCts?.Dispose();
    }
}
