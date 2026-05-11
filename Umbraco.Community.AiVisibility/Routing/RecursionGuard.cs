using System.Net;
using Microsoft.AspNetCore.Http;

namespace Umbraco.Community.AiVisibility.Routing;

/// <summary>
/// Default <see cref="IRecursionGuard"/> implementation. Decides recursion
/// based on the inbound request bearing the
/// <see cref="Constants.Http.LoopbackMarkerHeaderName"/> header AND the
/// connection's <see cref="ConnectionInfo.RemoteIpAddress"/> being a
/// loopback IP. The check is invoked from
/// <see cref="Controllers.MarkdownController.Render"/> before route
/// normalisation, exclusion, or extraction fire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime: Singleton.</b> The class is stateless — it reads only
/// the <see cref="HttpContext"/> argument received per call and consults
/// the package constant and the <see cref="IPAddress.IsLoopback"/> BCL
/// primitive. There are no fields and no injected dependencies, so
/// captive-graph concerns trivially clear.
/// </para>
/// <para>
/// <b>Three-branch decision tree.</b>
/// <list type="number">
///   <item>Marker header absent → return <see langword="false"/>. Cheapest
///   check first; the negative branch is the steady-state hot path on
///   real requests (the marker only appears on traffic the package's own
///   Loopback strategy issues).</item>
///   <item>Marker present but <see cref="ConnectionInfo.RemoteIpAddress"/>
///   is <see langword="null"/> → return <see langword="false"/>. Defensive:
///   the marker alone carries no signal about origin, and without a
///   verified loopback IP we cannot distinguish a package-originated
///   loopback request from an external client that learned the header
///   name from this package's source.</item>
///   <item>Marker present AND source IP is loopback → return
///   <see langword="true"/>. The <see cref="IPAddress.IsLoopback"/> check
///   covers IPv4 (any address in <c>127.0.0.0/8</c>), IPv6 (the canonical
///   <c>::1</c> literal), and IPv4-mapped IPv6 addresses whose embedded
///   IPv4 portion is in <c>127.0.0.0/8</c> (e.g. <c>::ffff:127.0.0.1</c>).
///   The primitive rejects RFC1918 private addresses
///   (<c>10.0.0.0/8</c>, <c>192.168.0.0/16</c>) and link-local addresses
///   (<c>169.254.0.0/16</c>) — those are not loopback in the kernel's
///   sense and reverse-proxy / load-balancer scenarios commonly surface
///   them as <c>RemoteIpAddress</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Single source of truth.</b> The header name is read from
/// <see cref="Constants.Http.LoopbackMarkerHeaderName"/> — the SAME
/// constant the outbound writer
/// (<c>LoopbackPageRendererStrategy</c>) consumes when stamping the
/// marker on every loopback request. A rename touches
/// <see cref="Constants"/> once and both sides stay in lockstep.
/// </para>
/// <para>
/// <b>Inversion of control.</b> The guard returns a <see cref="bool"/>
/// rather than producing an <c>IActionResult</c> or writing to the
/// response stream — the caller (<see cref="Controllers.MarkdownController"/>)
/// owns the 500-or-continue decision and the structured log line. This
/// keeps the guard testable as a pure function over <see cref="HttpContext"/>
/// state and avoids tangling response-shape policy into the routing-layer
/// abstraction.
/// </para>
/// </remarks>
internal sealed class RecursionGuard : IRecursionGuard
{
    public bool IsRecursion(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey(Constants.Http.LoopbackMarkerHeaderName))
        {
            return false;
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null)
        {
            return false;
        }

        return IPAddress.IsLoopback(remoteIp);
    }
}
