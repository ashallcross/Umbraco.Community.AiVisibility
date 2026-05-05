using Umbraco.Community.AiVisibility.Persistence;
using Umbraco.Cms.Core.Notifications;

namespace LlmsTxt.Umbraco.Notifications;

/// <summary>
/// Story 5.1 — fires after a successful (200) <c>/llms-full.txt</c>
/// response. Published from
/// <see cref="LlmsTxt.Umbraco.Controllers.LlmsFullTxtController"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fire-and-forget.</b> See
/// <see cref="MarkdownPageRequestedNotification"/> remarks for adopter
/// subscription pattern + adopter-handler exception isolation.
/// </para>
/// <para>
/// <b>Skipped on:</b> 304 Not Modified responses, 404 (no resolvable root),
/// 500 (builder error). Per Story 5.1 AC1.
/// </para>
/// <para>
/// <b>Empty-body 200s ARE published</b> (Decision D2 from Story 5.1 code
/// review). When scope filtering rejects every page the controller logs a
/// Warning, clears the cache, and serves an empty 200; this notification
/// fires with <see cref="BytesServed"/> = 0. Adopters can use that signal
/// to detect and alert on scope misconfiguration without polling logs.
/// </para>
/// <para>
/// <b><see cref="BytesServed"/>.</b> UTF-8 byte count of the manifest body
/// actually written to the response (captured via
/// <c>Encoding.UTF8.GetByteCount(body)</c>; multi-byte characters correctly
/// accounted). HEAD requests write no body and report
/// <see cref="BytesServed"/> = 0 (Decision D1 from Story 5.1 code review).
/// </para>
/// <para>
/// <b>PII discipline:</b> see
/// <see cref="MarkdownPageRequestedNotification"/> remarks.
/// </para>
/// </remarks>
public sealed class LlmsFullTxtRequestedNotification : INotification
{
    public LlmsFullTxtRequestedNotification(
        string hostname,
        string? culture,
        UserAgentClass userAgentClassification,
        string? referrerHost,
        int bytesServed)
    {
        Hostname = hostname;
        Culture = culture;
        UserAgentClassification = userAgentClassification;
        ReferrerHost = referrerHost;
        BytesServed = bytesServed;
    }

    /// <summary>
    /// Request hostname, normalised via <c>AiVisibilityCacheKeys.NormaliseHost</c>
    /// (lowercased + port-stripped). Matches the cache-key shape so adopter
    /// analytics group requests under the same key the cache uses.
    /// </summary>
    public string Hostname { get; }

    /// <summary>
    /// Effective culture per <c>IHostnameRootResolver</c>. <c>null</c> when
    /// the resolver did not produce a culture (rare; logged as 404 elsewhere).
    /// </summary>
    public string? Culture { get; }

    /// <summary>
    /// Result of <see cref="IUserAgentClassifier.Classify(string?)"/>
    /// against the <c>User-Agent</c> request header. Null / empty UA returns
    /// <see cref="UserAgentClass.Unknown"/>.
    /// </summary>
    public UserAgentClass UserAgentClassification { get; }

    /// <summary>
    /// Host segment of the <c>Referer</c> request header. Path / query /
    /// fragment are NEVER carried (PII discipline). Malformed or non-web
    /// schemes (<c>file://</c>, <c>ftp://</c>) → <c>null</c>.
    /// </summary>
    public string? ReferrerHost { get; }

    /// <summary>
    /// UTF-8 byte count of the manifest body actually written to the
    /// response. Adopter analytics consume this to surface manifest-size
    /// pressure / cap-hit events. Story 2.2's <c>MaxLlmsFullSizeKb</c> cap
    /// is enforced by the builder before this value is computed; if the cap
    /// fired, the truncated body's byte count is reported here. HEAD
    /// requests report 0 (no body written; Decision D1).
    /// </summary>
    public int BytesServed { get; }
}
