using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Persistence;
using Umbraco.Community.AiVisibility.Persistence.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;

namespace Umbraco.Community.AiVisibility.Notifications;

/// <summary>
/// Story 5.1 — the package's default notification handler. Subscribes to
/// all three Story 5.1 notifications and forwards them to the registered
/// <see cref="IRequestLog"/> as <see cref="RequestLogEntry"/>
/// rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the seam (AC3).</b> Notifications stay public events even
/// when <see cref="RequestLogSettings.Enabled"/> is <c>false</c>; the
/// short-circuit lives here, in the package's own handler. Adopter
/// handlers continue to receive events regardless.
/// </para>
/// <para>
/// <b>Lifetime: Scoped.</b> Registered via Umbraco's
/// <c>AddNotificationAsyncHandler&lt;TNotification, THandler&gt;</c>
/// extension which hands the handler over as Scoped. All transitive deps
/// here are Singleton (<see cref="IOptionsMonitor{TOptions}"/>,
/// <see cref="IRequestLog"/>, <see cref="TimeProvider"/>,
/// <see cref="ILogger{TCategoryName}"/>) — Scoped is correct per
/// Umbraco's notification-dispatch convention rather than driven by our
/// captive-dep concerns.
/// </para>
/// <para>
/// <b>Adopter-handler exception isolation (AC2):</b> Umbraco's
/// <see cref="IEventAggregator"/> dispatcher catches handler exceptions
/// and logs them — the package never lets adopter (or its own) handler
/// exceptions bubble back to the route's response. The handler itself
/// adopts a try/catch around <see cref="IRequestLog.EnqueueAsync"/>
/// as defence-in-depth.
/// </para>
/// </remarks>
public sealed class DefaultRequestLogHandler :
    INotificationAsyncHandler<MarkdownPageRequestedNotification>,
    INotificationAsyncHandler<LlmsTxtRequestedNotification>,
    INotificationAsyncHandler<LlmsFullTxtRequestedNotification>
{
    private readonly IRequestLog _requestLog;
    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DefaultRequestLogHandler> _logger;

    public DefaultRequestLogHandler(
        IRequestLog requestLog,
        IOptionsMonitor<AiVisibilitySettings> settings,
        TimeProvider timeProvider,
        ILogger<DefaultRequestLogHandler> logger)
    {
        _requestLog = requestLog ?? throw new ArgumentNullException(nameof(requestLog));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task HandleAsync(MarkdownPageRequestedNotification notification, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return Task.CompletedTask;
        }

        return EnqueueSafely(new RequestLogEntry
        {
            CreatedUtc = _timeProvider.GetUtcNow().UtcDateTime,
            Path = notification.Path,
            ContentKey = notification.ContentKey,
            Culture = notification.Culture ?? string.Empty,
            UserAgentClass = notification.UserAgentClassification.ToString(),
            ReferrerHost = notification.ReferrerHost,
        }, cancellationToken);
    }

    public Task HandleAsync(LlmsTxtRequestedNotification notification, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return Task.CompletedTask;
        }

        return EnqueueSafely(new RequestLogEntry
        {
            CreatedUtc = _timeProvider.GetUtcNow().UtcDateTime,
            Path = "/llms.txt",
            ContentKey = null,
            Culture = notification.Culture ?? string.Empty,
            UserAgentClass = notification.UserAgentClassification.ToString(),
            ReferrerHost = notification.ReferrerHost,
        }, cancellationToken);
    }

    public Task HandleAsync(LlmsFullTxtRequestedNotification notification, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return Task.CompletedTask;
        }

        // BytesServed is on the notification only — the table doesn't
        // carry it (per AC4 column list). Adopters needing per-byte
        // analytics consume the notification directly via their own
        // handler.
        return EnqueueSafely(new RequestLogEntry
        {
            CreatedUtc = _timeProvider.GetUtcNow().UtcDateTime,
            Path = "/llms-full.txt",
            ContentKey = null,
            Culture = notification.Culture ?? string.Empty,
            UserAgentClass = notification.UserAgentClassification.ToString(),
            ReferrerHost = notification.ReferrerHost,
        }, cancellationToken);
    }

    private bool IsEnabled() => _settings.CurrentValue.RequestLog.Enabled;

    private async Task EnqueueSafely(RequestLogEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _requestLog.EnqueueAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled — quiet exit, the response was already written.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DefaultRequestLogHandler: IRequestLog.EnqueueAsync threw for {Path}. " +
                "Entry dropped; the response is unaffected.",
                entry.Path);
        }
    }
}
