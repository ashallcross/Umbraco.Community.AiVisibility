using Umbraco.Community.AiVisibility.Persistence;
using Umbraco.Cms.Core.Notifications;

namespace LlmsTxt.Umbraco.Notifications;

/// <summary>
/// Story 5.1 — fires after a successful (200) <c>/llms.txt</c> response.
/// Published from <see cref="LlmsTxt.Umbraco.Controllers.LlmsTxtController"/>.
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
/// <b>No <c>ContentKey</c>.</b> The manifest is whole-site; per-page
/// content key is meaningless at this surface.
/// </para>
/// <para>
/// <b>PII discipline:</b> see
/// <see cref="MarkdownPageRequestedNotification"/> remarks. The five fields
/// here are the same locked subset minus <c>ContentKey</c>.
/// </para>
/// </remarks>
public sealed class LlmsTxtRequestedNotification : INotification
{
    public LlmsTxtRequestedNotification(
        string hostname,
        string? culture,
        UserAgentClass userAgentClassification,
        string? referrerHost)
    {
        Hostname = hostname;
        Culture = culture;
        UserAgentClassification = userAgentClassification;
        ReferrerHost = referrerHost;
    }

    /// <summary>
    /// Request hostname, lowercased + port-stripped (matches the
    /// <see cref="Umbraco.Community.AiVisibility.Caching.AiVisibilityCacheKeys.NormaliseHost"/>
    /// shape). Empty when the request had no Host header.
    /// </summary>
    public string Hostname { get; }

    /// <summary>
    /// Resolved culture for the manifest, or <c>null</c> when invariant.
    /// </summary>
    public string? Culture { get; }

    /// <summary>
    /// Coarse User-Agent classification.
    /// </summary>
    public UserAgentClass UserAgentClassification { get; }

    /// <summary>
    /// Host segment of the <c>Referer</c> header, or <c>null</c>.
    /// </summary>
    public string? ReferrerHost { get; }
}
