using System.Net;

namespace LlmsTxt.Umbraco.Routing;

/// <summary>
/// Canonicalises an inbound URL path so the <c>.md</c> controller and the
/// Accept-negotiation middleware compose identical ETag inputs for the same
/// logical resource. Without a shared normalisation, percent-encoded paths
/// (<c>/caf%C3%A9</c> ↔ <c>/café.md</c>) and trailing-slash variants
/// (<c>/foo/.md</c> normalises to <c>/foo/</c>; <c>/foo</c> Accept-negotiation
/// stays <c>/foo</c>) produce different ETag inputs and break AC1's
/// "same ETag, same cache entry consulted" guarantee across the two surfaces.
/// </summary>
internal static class LlmsCanonicalPath
{
    /// <summary>
    /// URL-decodes (idempotent — already-decoded inputs pass through), ensures a
    /// leading slash, and strips a trailing slash unless the path is the root
    /// "/". The output is the canonical-path key fed into the ETag hash.
    /// </summary>
    public static string Normalise(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        var decoded = WebUtility.UrlDecode(path);
        if (!decoded.StartsWith('/')) decoded = "/" + decoded;
        if (decoded.Length > 1 && decoded.EndsWith('/'))
        {
            decoded = decoded[..^1];
        }
        return decoded;
    }
}
