namespace LlmsTxt.Umbraco.Routing;

/// <summary>
/// Story 4.1 — maps a canonical page URL to its Markdown alternate per the
/// llms.txt trailing-slash convention. A trailing-slash URL receives the
/// <c>/index.html.md</c> suffix; everything else gets <c>.md</c>.
/// Already-mapped URLs pass through (idempotent, case-insensitive).
/// <para>
/// Reused by <c>DiscoverabilityHeaderMiddleware</c> (Link header),
/// <c>LlmsLinkTagHelper</c> (HTML <c>&lt;link rel="alternate"&gt;</c>), and
/// <c>LlmsHintTagHelper</c> (visually-hidden anchor) so all three surfaces
/// agree on the same shape.
/// </para>
/// </summary>
internal static class MarkdownAlternateUrl
{
    public static string Append(string? canonicalUrl)
    {
        // Site root — null, empty, or "/" all collapse to the
        // same alternate per the llms.txt trailing-slash convention.
        if (string.IsNullOrEmpty(canonicalUrl) || canonicalUrl == "/")
        {
            return Constants.Routes.IndexHtmlMdSuffix;
        }

        if (canonicalUrl.EndsWith(Constants.Routes.MarkdownSuffix, StringComparison.OrdinalIgnoreCase)
            || canonicalUrl.EndsWith(Constants.Routes.IndexHtmlMdSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return canonicalUrl;
        }

        return canonicalUrl.EndsWith('/')
            ? canonicalUrl + Constants.Routes.IndexHtmlMdSuffix.TrimStart('/')
            : canonicalUrl + Constants.Routes.MarkdownSuffix;
    }
}
