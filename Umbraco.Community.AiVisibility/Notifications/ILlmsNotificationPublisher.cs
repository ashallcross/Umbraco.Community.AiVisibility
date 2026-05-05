using Microsoft.AspNetCore.Http;

namespace LlmsTxt.Umbraco.Notifications;

/// <summary>
/// Story 5.1 — internal helper that centralises the four publication
/// sites' shared work:
/// <list type="bullet">
/// <item>Reading <c>User-Agent</c> + <c>Referer</c> from the request
/// headers.</item>
/// <item>Classifying the UA via <see cref="Umbraco.Community.AiVisibility.Persistence.IUserAgentClassifier"/>.</item>
/// <item>Parsing the referrer host segment (with malformed-URL tolerance).</item>
/// <item>Publishing through Umbraco's
/// <see cref="Umbraco.Cms.Core.Events.IEventAggregator"/> with try/catch
/// defence so a publisher fault never escapes to the response path.</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Internal contract.</b> Not an extension point — adopters who want
/// custom publish behaviour subscribe via
/// <c>INotificationAsyncHandler&lt;TNotification&gt;</c> rather than
/// replacing this helper. The interface exists purely to keep the
/// controllers + middleware test-mockable without exposing
/// <see cref="Umbraco.Cms.Core.Events.IEventAggregator"/> as a constructor
/// dependency on every publication site.
/// </remarks>
public interface ILlmsNotificationPublisher
{
    Task PublishMarkdownPageAsync(
        HttpContext context,
        string canonicalPath,
        Guid contentKey,
        string? culture,
        CancellationToken cancellationToken);

    Task PublishLlmsTxtAsync(
        HttpContext context,
        string hostname,
        string? culture,
        CancellationToken cancellationToken);

    Task PublishLlmsFullTxtAsync(
        HttpContext context,
        string hostname,
        string? culture,
        int bytesServed,
        CancellationToken cancellationToken);
}
