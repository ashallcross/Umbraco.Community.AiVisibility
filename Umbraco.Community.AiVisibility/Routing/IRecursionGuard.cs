using Microsoft.AspNetCore.Http;

namespace Umbraco.Community.AiVisibility.Routing;

/// <summary>
/// Inbound-side recursion guard for the package's own loopback render path.
/// Called at the top of <see cref="Controllers.MarkdownController.Render"/> so
/// a recursion attempt short-circuits before route resolution, exclusion, and
/// extraction fire.
/// <para>
/// <b>Defence in depth, not the primary mechanism.</b> The primary
/// recursion-prevention mechanism is the outbound <c>Accept: text/html</c>
/// override that <c>LoopbackPageRendererStrategy</c> writes on every
/// loopback request — that override means the package's <c>.md</c> route
/// is not the loopback target in normal operation, so this guard never
/// fires. This guard is secondary insurance against adopter middleware
/// that aliases routes such that the loopback hit lands back on
/// <c>.md</c> despite the Accept override (Umbraco Redirects rule, custom
/// route alias, agency-supplied URL-rewrite filter).
/// </para>
/// <para>
/// <b>Recursion signal = marker header + loopback source IP, together.</b>
/// The outbound writer sets
/// <see cref="Constants.Http.LoopbackMarkerHeaderName"/>
/// (<c>X-AiVisibility-Loopback</c>) on every request it issues. The
/// inbound matcher confirms recursion only when that header is present
/// AND <c>HttpContext.Connection.RemoteIpAddress</c> is a loopback IP
/// (any IPv4 in <c>127.0.0.0/8</c>, the IPv6 literal <c>::1</c>, or
/// IPv4-mapped IPv6 loopback). Header alone is insufficient: an external
/// client that learns the header name from this package's source cannot
/// trigger a 500 by spoofing the marker — their source IP is not
/// loopback, so the guard treats the marker as absent and the request is
/// rendered normally.
/// </para>
/// <para>
/// <b>Extension point.</b> Adopters who want to harden detection (for
/// example, cross-check a forwarded-for chain, fingerprint via a
/// process-shared secret) register their own implementation via
/// <c>services.TryAddSingleton&lt;IRecursionGuard, MyGuard&gt;()</c>
/// before the package composer runs, or replace the package default
/// via <c>services.Replace(...)</c>.
/// </para>
/// </summary>
public interface IRecursionGuard
{
    /// <summary>
    /// Returns <see langword="true"/> when the inbound request bears the
    /// outbound-loopback marker header AND originates from a loopback IP
    /// address — together signalling that the <c>.md</c> route is being
    /// re-entered from the package's own Loopback strategy. Returns
    /// <see langword="false"/> in every other case (header absent, source
    /// IP non-loopback, source IP undetermined).
    /// </summary>
    bool IsRecursion(HttpContext context);
}
