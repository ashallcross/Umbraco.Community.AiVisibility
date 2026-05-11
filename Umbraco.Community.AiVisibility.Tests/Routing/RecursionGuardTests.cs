using System.Net;
using Microsoft.AspNetCore.Http;
using Umbraco.Community.AiVisibility;
using Umbraco.Community.AiVisibility.Routing;

namespace Umbraco.Community.AiVisibility.Tests.Routing;

[TestFixture]
public class RecursionGuardTests
{
    // AC10 — Header absent → false (hot path on real traffic).
    [Test]
    public void IsRecursion_HeaderAbsent_ReturnsFalse()
    {
        var ctx = BuildHttpContext(markerHeaderValue: null, remoteIp: IPAddress.Loopback);

        var result = new RecursionGuard().IsRecursion(ctx);

        Assert.That(result, Is.False);
    }

    // AC10 — Header + IPv4 loopback → true. Architectural happy-path proof.
    // Fixture uses Constants.Http.LoopbackMarkerHeaderName (not a hardcoded string)
    // so a future rename of the constant flips both the writer and this test in
    // lockstep per AC3 single-source-of-truth.
    [Test]
    public void IsRecursion_HeaderPresent_LoopbackIPv4_ReturnsTrue()
    {
        var ctx = BuildHttpContext(markerHeaderValue: "1", remoteIp: IPAddress.Loopback);

        var result = new RecursionGuard().IsRecursion(ctx);

        Assert.That(result, Is.True);
    }

    // AC10 — IPv6 loopback (::1) coverage per AC8.
    [Test]
    public void IsRecursion_HeaderPresent_LoopbackIPv6_ReturnsTrue()
    {
        var ctx = BuildHttpContext(markerHeaderValue: "1", remoteIp: IPAddress.IPv6Loopback);

        var result = new RecursionGuard().IsRecursion(ctx);

        Assert.That(result, Is.True);
    }

    // AC10 + AC7 spoof-defence: header + non-loopback source IP → false.
    // Five [TestCase] rows cover RFC1918 private (10/8 + 192.168/16), link-local
    // (169.254/16), public IPv4 (8.8.8.8), and IPv6 documentation-range
    // (2001:db8::/32) per AC7 + Failure case 2 + Failure case 5/6.
    [TestCase("10.0.0.1")]
    [TestCase("192.168.1.1")]
    [TestCase("169.254.1.1")]
    [TestCase("8.8.8.8")]
    [TestCase("2001:db8::1")]
    public void IsRecursion_HeaderPresent_NonLoopbackIP_ReturnsFalse(string sourceIp)
    {
        var ctx = BuildHttpContext(markerHeaderValue: "1", remoteIp: IPAddress.Parse(sourceIp));

        var result = new RecursionGuard().IsRecursion(ctx);

        Assert.That(result, Is.False);
    }

    // AC10 + AC2 step 2 — defensive null branch: header present but Connection
    // metadata didn't surface a source IP (rare Kestrel edge configs). Treated
    // as if the marker is absent — Failure case 3 documents the bounded
    // imperfection.
    [Test]
    public void IsRecursion_HeaderPresent_RemoteIpAddressNull_ReturnsFalse()
    {
        var ctx = BuildHttpContext(markerHeaderValue: "1", remoteIp: null);

        var result = new RecursionGuard().IsRecursion(ctx);

        Assert.That(result, Is.False);
    }

    private static HttpContext BuildHttpContext(string? markerHeaderValue, IPAddress? remoteIp)
    {
        var ctx = new DefaultHttpContext();
        if (markerHeaderValue is not null)
        {
            ctx.Request.Headers[Constants.Http.LoopbackMarkerHeaderName] = markerHeaderValue;
        }
        ctx.Connection.RemoteIpAddress = remoteIp;
        return ctx;
    }
}
