using Umbraco.Community.AiVisibility.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;

namespace Umbraco.Community.AiVisibility.Notifications;

/// <summary>
/// Story 5.1 — default <see cref="INotificationPublisher"/>. Singleton
/// (stateless; UA classifier + event aggregator are framework-injected
/// Singletons).
/// </summary>
/// <remarks>
/// <b>Defence-in-depth (AC2):</b> any exception from
/// <see cref="IEventAggregator.PublishAsync"/> (or from individual handler
/// dispatch) is caught + logged at <c>Warning</c>. The response was
/// already written by the caller; a notification publication failure must
/// never propagate.
/// </remarks>
public sealed class DefaultNotificationPublisher : INotificationPublisher
{
    private readonly IEventAggregator _eventAggregator;
    private readonly IUserAgentClassifier _classifier;
    private readonly ILogger<DefaultNotificationPublisher> _logger;

    public DefaultNotificationPublisher(
        IEventAggregator eventAggregator,
        IUserAgentClassifier classifier,
        ILogger<DefaultNotificationPublisher> logger)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task PublishMarkdownPageAsync(
        HttpContext context,
        string canonicalPath,
        Guid contentKey,
        string? culture,
        CancellationToken cancellationToken)
    {
        return PublishSafely(
            () =>
            {
                var (uaClass, referrerHost) = ExtractRequestSignals(context);
                return new MarkdownPageRequestedNotification(
                    path: canonicalPath,
                    contentKey: contentKey,
                    culture: culture,
                    userAgentClassification: uaClass,
                    referrerHost: referrerHost);
            },
            canonicalPath,
            cancellationToken);
    }

    public Task PublishLlmsTxtAsync(
        HttpContext context,
        string hostname,
        string? culture,
        CancellationToken cancellationToken)
    {
        return PublishSafely(
            () =>
            {
                var (uaClass, referrerHost) = ExtractRequestSignals(context);
                return new LlmsTxtRequestedNotification(
                    hostname: hostname,
                    culture: culture,
                    userAgentClassification: uaClass,
                    referrerHost: referrerHost);
            },
            "/llms.txt",
            cancellationToken);
    }

    public Task PublishLlmsFullTxtAsync(
        HttpContext context,
        string hostname,
        string? culture,
        int bytesServed,
        CancellationToken cancellationToken)
    {
        return PublishSafely(
            () =>
            {
                var (uaClass, referrerHost) = ExtractRequestSignals(context);
                return new LlmsFullTxtRequestedNotification(
                    hostname: hostname,
                    culture: culture,
                    userAgentClassification: uaClass,
                    referrerHost: referrerHost,
                    bytesServed: bytesServed);
            },
            "/llms-full.txt",
            cancellationToken);
    }

    private (UserAgentClass UaClass, string? ReferrerHost) ExtractRequestSignals(HttpContext context)
    {
        var headers = context.Request.Headers;
        var uaHeader = headers.UserAgent.ToString();
        var uaClass = _classifier.Classify(uaHeader);
        var referrerHost = ExtractReferrerHost(headers.Referer.ToString());
        return (uaClass, referrerHost);
    }

    /// <summary>
    /// Parse the <c>Referer</c> header into a host segment only.
    /// Malformed URLs / null / empty / non-web schemes → <c>null</c>.
    /// Never carries path, query, or fragment per AC1's PII discipline.
    /// </summary>
    /// <remarks>
    /// Schemes are allowlisted to <c>http</c> / <c>https</c> only —
    /// <c>file://</c>, <c>ftp://</c>, <c>ssh://</c> etc. would surface
    /// internal hostnames from spec-violating clients into adopter
    /// analytics. RFC 7231 §5.5.2 scopes <c>Referer</c> to web URLs.
    /// </remarks>
    internal static string? ExtractReferrerHost(string? refererHeader)
    {
        if (string.IsNullOrWhiteSpace(refererHeader))
        {
            return null;
        }

        if (!Uri.TryCreate(refererHeader, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return string.IsNullOrEmpty(uri.Host) ? null : uri.Host;
    }

    private async Task PublishSafely<TNotification>(
        Func<TNotification> factory,
        string contextPath,
        CancellationToken cancellationToken)
        where TNotification : global::Umbraco.Cms.Core.Notifications.INotification
    {
        try
        {
            // Build inside the try/catch so a faulty IUserAgentClassifier
            // override (or any other exception during signal extraction)
            // is contained — the response was already written by the
            // caller; a publication-path failure must never propagate.
            var notification = factory();
            await _eventAggregator.PublishAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled mid-publish — quiet exit. The response was
            // already written.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Notification publication failed for {NotificationType} {Path}",
                typeof(TNotification).Name,
                contextPath);
        }
    }
}
