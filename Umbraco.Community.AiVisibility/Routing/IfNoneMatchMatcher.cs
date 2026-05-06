using Microsoft.AspNetCore.Http;

namespace Umbraco.Community.AiVisibility.Routing;

/// <summary>
/// Story 2.3 — RFC 7232 § 3.2 <c>If-None-Match</c> matcher. Lifted from
/// <see cref="MarkdownResponseWriter"/>'s private static (Story 1.3 + 1.5
/// shipped the original) so both manifest controllers and the per-page writer
/// share one implementation.
/// <para>
/// Accepts strong (<c>"abc"</c>) or weak (<c>W/"abc"</c>) validator forms on
/// input — the package always emits strong validators, but adopter CDNs may
/// rewrite. Honours the bare <c>*</c> wildcard. <c>W/*</c> is malformed per
/// RFC 7232 § 3.2 and returns <c>false</c>.
/// </para>
/// </summary>
internal static class IfNoneMatchMatcher
{
    /// <summary>
    /// Returns <c>true</c> when the request's <c>If-None-Match</c> header
    /// contains <paramref name="etag"/> (strong or weak form), OR contains the
    /// bare <c>*</c> wildcard. Returns <c>false</c> when the header is absent,
    /// empty, or every entry mismatches.
    /// </summary>
    public static bool Matches(HttpRequest request, string etag)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(etag);

        if (!request.Headers.TryGetValue(Constants.HttpHeaders.IfNoneMatch, out var values)
            || values.Count == 0)
        {
            return false;
        }

        // RFC 7232 § 3.2 — If-None-Match can carry a comma-separated list. Match against any.
        // We always emit strong validators (no W/ prefix); accept either form for input.
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var candidate in raw.Split(','))
            {
                var trimmed = candidate.Trim();

                // Bare wildcard FIRST — RFC 7232 § 3.2: only the bare `*` token is
                // the wildcard. `W/*` is malformed; checking `*` before stripping the
                // `W/` prefix prevents a weak-wildcard from surfacing as "match anything".
                if (string.Equals(trimmed, "*", StringComparison.Ordinal))
                {
                    return true;
                }

                if (trimmed.StartsWith("W/", StringComparison.Ordinal))
                {
                    trimmed = trimmed[2..];
                }

                if (string.Equals(trimmed, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
