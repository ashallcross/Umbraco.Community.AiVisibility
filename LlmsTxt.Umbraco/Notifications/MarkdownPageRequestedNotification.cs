using LlmsTxt.Umbraco.Persistence;
using Umbraco.Cms.Core.Notifications;

namespace LlmsTxt.Umbraco.Notifications;

/// <summary>
/// Story 5.1 — fires after a successful (200) Markdown response. Published
/// from <see cref="LlmsTxt.Umbraco.Controllers.MarkdownController"/> on the
/// <c>.md</c> route AND from
/// <see cref="LlmsTxt.Umbraco.Routing.AcceptHeaderNegotiationMiddleware"/>
/// on the <c>Accept: text/markdown</c> diverted path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fire-and-forget.</b> Adopters subscribe via
/// <c>INotificationHandler&lt;MarkdownPageRequestedNotification&gt;</c>
/// (sync) or <c>INotificationAsyncHandler&lt;MarkdownPageRequestedNotification&gt;</c>
/// (async, recommended), registered via
/// <c>builder.AddNotificationAsyncHandler&lt;TNotification, THandler&gt;()</c>.
/// Adopter-handler exceptions are caught at dispatch and logged at
/// <c>Warning</c> — they never break the response.
/// </para>
/// <para>
/// <b>Skipped on:</b> 304 Not Modified responses (revalidation; same body
/// already delivered), 404 (excluded page or route-resolution failure), 500
/// (extraction error). Per Story 5.1 AC1.
/// </para>
/// <para>
/// <b>PII discipline (NFR11 + project-context.md § Critical Don't-Miss):</b>
/// the payload carries ONLY <see cref="Path"/> (canonical, query-string-stripped),
/// <see cref="ContentKey"/>, <see cref="Culture"/>,
/// <see cref="UserAgentClassification"/> (a coarse enum value — never the
/// raw UA string), and <see cref="ReferrerHost"/> (host segment only — never
/// path / query / fragment). Cookies, tokens, session IDs, and full
/// referrer URLs are never captured.
/// </para>
/// </remarks>
public sealed class MarkdownPageRequestedNotification : INotification
{
    public MarkdownPageRequestedNotification(
        string path,
        Guid contentKey,
        string? culture,
        UserAgentClass userAgentClassification,
        string? referrerHost)
    {
        Path = path;
        ContentKey = contentKey;
        Culture = culture;
        UserAgentClassification = userAgentClassification;
        ReferrerHost = referrerHost;
    }

    /// <summary>
    /// Canonical request path with the <c>.md</c> / <c>/index.html.md</c>
    /// suffix stripped (per <c>MarkdownPathNormaliser</c>) AND query string
    /// excluded. Example: <c>/about-us</c> for a request to
    /// <c>/about-us.md?secret=xyz</c>.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// The published content node's key (<see cref="Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent.Key"/>).
    /// </summary>
    public Guid ContentKey { get; }

    /// <summary>
    /// Resolved culture for the request, or <c>null</c> when invariant.
    /// BCP-47 form (e.g. <c>en-US</c>, <c>en-GB</c>, <c>cy</c>).
    /// </summary>
    public string? Culture { get; }

    /// <summary>
    /// Coarse classification of the request's User-Agent header. See
    /// <see cref="UserAgentClass"/>.
    /// </summary>
    public UserAgentClass UserAgentClassification { get; }

    /// <summary>
    /// Host segment of the <c>Referer</c> header (e.g. <c>google.com</c>) or
    /// <c>null</c> when no <c>Referer</c> was sent or it could not be parsed.
    /// Never carries the full referrer URL.
    /// </summary>
    public string? ReferrerHost { get; }
}
