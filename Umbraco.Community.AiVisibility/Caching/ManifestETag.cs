using System.Security.Cryptography;
using System.Text;

namespace Umbraco.Community.AiVisibility.Caching;

/// <summary>
/// Story 2.3 — manifest body-derived ETag computer. The manifest's content
/// version IS the body itself (any change to settings / pages / hreflang flag
/// produces a different body), so a content hash is the simplest and most
/// stable validator.
/// <para>
/// Diverges from <see cref="LlmsTxt.Umbraco.Routing.MarkdownResponseWriter"/>'s
/// per-page ETag (which hashes <c>(host | route | culture | updatedUtc)</c>):
/// the per-page writer needs <see cref="IPublishedContent.UpdateDate"/> as the
/// version stamp because the body is computed lazily downstream of the writer;
/// the manifest controller already has the body in hand by the time it cares
/// about an ETag, and hashing the body sidesteps the
/// <c>DateTimeKind.Unspecified</c> + multi-TZ-load-balanced concerns Epic 1
/// retro § A5 raised. <b>Do NOT collapse the two ETag computers into one helper</b>
/// — the inputs are deliberately different.
/// </para>
/// <para>
/// Format: SHA-256(body) → first 12 bytes → base64-url → quoted strong
/// validator (16 chars between the quotes; 18 with quotes). Same shape as
/// <c>MarkdownResponseWriter.ComputeETag</c> for inspectability consistency.
/// </para>
/// </summary>
internal static class ManifestETag
{
    /// <summary>
    /// Compute the body-derived ETag. <paramref name="body"/> may be empty
    /// (Story 2.2's "scope rejects everything → 200 + empty body" path) — the
    /// hash of zero bytes is well-defined; the result is a stable non-null
    /// quoted strong validator.
    /// </summary>
    public static string Compute(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        var hash = Convert.ToBase64String(bytes, 0, 12)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return $"\"{hash}\"";
    }
}
