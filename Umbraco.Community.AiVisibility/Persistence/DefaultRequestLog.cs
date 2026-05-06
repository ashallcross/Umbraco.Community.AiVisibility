using System.Threading.Channels;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Persistence.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Umbraco.Community.AiVisibility.Persistence;

/// <summary>
/// Story 5.1 — default <see cref="IRequestLog"/> implementation.
/// Owns a process-wide bounded
/// <see cref="Channel{T}"/> of <see cref="RequestLogEntry"/>; the
/// background <c>LlmsRequestLogDrainHostedService</c> reads from
/// <see cref="Reader"/> and batch-writes to
/// <c>llmsTxtRequestLog</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton lifetime, thread-safe by composition.</b> The channel
/// itself is thread-safe; the only mutable per-instance state is the
/// overflow throttle counter, guarded by <see cref="Interlocked"/>.
/// </para>
/// <para>
/// <b>Capacity / FullMode:</b> capacity comes from
/// <see cref="RequestLogSettings.QueueCapacity"/> at construction time
/// (clamped to <c>[64, 65536]</c>); full-mode is
/// <see cref="BoundedChannelFullMode.DropOldest"/> so adopters debugging
/// recent traffic see fresh entries even under sustained crawl load.
/// </para>
/// </remarks>
public sealed class DefaultRequestLog : IRequestLog
{
    internal const int MinQueueCapacity = 64;
    internal const int MaxQueueCapacity = 65_536;
    internal const int MinOverflowLogInterval = 5;
    internal const int MaxOverflowLogInterval = 3600;

    private readonly Channel<RequestLogEntry> _channel;
    private readonly int _capacity;
    private readonly ILogger<DefaultRequestLog> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _overflowLogInterval;

    // Throttle the overflow Warning log so a sustained crawl-driven full
    // queue doesn't flood the host's log sink. Stored as ticks rather
    // than DateTimeOffset for atomic Interlocked.* compare-exchange.
    private long _lastOverflowLogTicks;
    private long _droppedSinceLastLog;

    public DefaultRequestLog(
        IOptionsMonitor<AiVisibilitySettings> settings,
        ILogger<DefaultRequestLog> logger,
        TimeProvider timeProvider)
    {
        var snapshot = settings?.CurrentValue
            ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _capacity = Math.Clamp(snapshot.RequestLog.QueueCapacity, MinQueueCapacity, MaxQueueCapacity);
        var overflowSeconds = Math.Clamp(
            snapshot.RequestLog.OverflowLogIntervalSeconds,
            MinOverflowLogInterval,
            MaxOverflowLogInterval);
        _overflowLogInterval = TimeSpan.FromSeconds(overflowSeconds);

        _channel = Channel.CreateBounded<RequestLogEntry>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>
    /// Channel reader exposed for the drainer hosted service. Internal —
    /// adopters never read from this directly; a custom
    /// <see cref="IRequestLog"/> impl owns its own write path.
    /// </summary>
    internal ChannelReader<RequestLogEntry> Reader => _channel.Reader;

    public Task EnqueueAsync(RequestLogEntry entry, CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        // Detect overflow BEFORE TryWrite. Under
        // BoundedChannelFullMode.DropOldest the channel always silently
        // sheds the head and TryWrite returns true — so a post-call
        // false-check would never fire and the overflow Warning would be
        // dead code. Instead we read Reader.Count: if it's already at
        // capacity, the upcoming TryWrite will drop the head, and we
        // increment the throttled-Warning counter accordingly.
        var atCapacity = _channel.Reader.Count >= _capacity;

        // Non-blocking write — under DropOldest this still returns true,
        // even when atCapacity. The false branch only fires on a closed
        // channel (defence-in-depth; the writer is never explicitly
        // completed in normal operation).
        if (!_channel.Writer.TryWrite(entry))
        {
            return Task.CompletedTask;
        }

        if (!atCapacity)
        {
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _droppedSinceLastLog);
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        var lastTicks = Interlocked.Read(ref _lastOverflowLogTicks);
        if (nowTicks - lastTicks < _overflowLogInterval.Ticks)
        {
            return Task.CompletedTask;
        }

        if (Interlocked.CompareExchange(ref _lastOverflowLogTicks, nowTicks, lastTicks) != lastTicks)
        {
            // Another caller won the race — let them log.
            return Task.CompletedTask;
        }

        var droppedSnapshot = Interlocked.Exchange(ref _droppedSinceLastLog, 0);
        try
        {
            _logger.LogWarning(
                "LlmsTxt request log overflow — dropped {DroppedCount} entries in the last {IntervalSeconds}s. " +
                "Queue capacity is {QueueCapacity}; raising LlmsTxt:RequestLog:QueueCapacity " +
                "or LlmsTxt:RequestLog:BatchSize may help.",
                droppedSnapshot,
                (int)_overflowLogInterval.TotalSeconds,
                _capacity);
        }
        catch (Exception)
        {
            // Defence-in-depth — if the logger throws (custom sink with a
            // transient fault), restore the dropped counter and roll back
            // the ticks marker so the next caller can re-attempt logging
            // rather than have the throttle window suppress it. Swallow
            // the exception: the writer must never propagate a logging
            // failure into the response path.
            Interlocked.Add(ref _droppedSinceLastLog, droppedSnapshot);
            Interlocked.Exchange(ref _lastOverflowLogTicks, lastTicks);
        }
        return Task.CompletedTask;
    }
}
