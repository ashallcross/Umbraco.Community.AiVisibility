using System.Net.Http;
using System.Net.Security;
using Umbraco.Community.AiVisibility.Extraction;

namespace Umbraco.Community.AiVisibility.Tests.Extraction;

/// <summary>
/// Story 7.2 AC8 + AC11 — pure-function unit tests for the loopback
/// cert-validation callback. Pins (a) early-return on no SSL errors,
/// (b) loopback-IP / "localhost" hostname bypass with errors, and
/// (c) external-host with errors → default chain runs (NEVER blanket bypass).
/// </summary>
[TestFixture]
public class LoopbackCertificateValidatorTests
{
    /// <summary>
    /// AC8 — when the default chain succeeded already (no SSL errors),
    /// the callback returns true without inspecting the host. Cert is fine
    /// per the default validation; no bypass needed.
    /// </summary>
    [Test]
    public void Validate_NoSslErrors_ReturnsTrue()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://contoso.example/"));

        var result = LoopbackCertificateValidator.Validate(
            request,
            certificate: null,
            chain: null,
            sslPolicyErrors: SslPolicyErrors.None);

        Assert.That(result, Is.True,
            "no SSL errors → bypass not needed; cert is fine per default chain");
    }

    /// <summary>
    /// AC8 — IPv4 loopback host with SSL errors → bypass. Adopters running
    /// dev sites with a self-signed cert against <c>https://127.0.0.1:5001</c>
    /// expect the loopback render to succeed.
    /// </summary>
    [Test]
    public void Validate_LoopbackIPv4Host_BypassesErrors()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://127.0.0.1:5001/"));

        var result = LoopbackCertificateValidator.Validate(
            request,
            certificate: null,
            chain: null,
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(result, Is.True,
            "127.0.0.1 is a loopback IP — bypass cert errors safely");
    }

    /// <summary>
    /// AC8 — IPv6 loopback host with SSL errors → bypass. Mirrors the IPv4
    /// case for the <c>[::1]</c> bracketed-literal host shape that
    /// <see cref="Uri.Host"/> returns for IPv6 URIs.
    /// </summary>
    [Test]
    public void Validate_LoopbackIPv6Host_BypassesErrors()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://[::1]:5001/"));

        var result = LoopbackCertificateValidator.Validate(
            request,
            certificate: null,
            chain: null,
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.That(result, Is.True,
            "[::1] is the IPv6 loopback address — bypass cert errors safely");
    }

    /// <summary>
    /// AC8 — non-loopback host with SSL errors → default chain's verdict
    /// stands. The strategy's catch block surfaces the resulting
    /// <c>HttpRequestException</c> as a render failure. NEVER blanket
    /// bypass — adopters who set
    /// <c>LoopbackBaseUrl: "https://internal-loadbalancer.contoso.local"</c>
    /// with a mis-issued cert get the failure they should expect.
    /// </summary>
    [Test]
    public void Validate_NonLoopbackHost_DefaultChainRuns()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://contoso.example/"));

        var result = LoopbackCertificateValidator.Validate(
            request,
            certificate: null,
            chain: null,
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.That(result, Is.False,
            "external host with cert errors → fail per default chain. NEVER blanket bypass.");
    }

    /// <summary>
    /// Defensive — a missing <see cref="HttpRequestMessage.RequestUri"/>
    /// means we cannot reason about the transport target. Fail closed
    /// rather than guess.
    /// </summary>
    [Test]
    public void Validate_NullRequestUri_ReturnsFalse()
    {
        var request = new HttpRequestMessage(); // RequestUri left null

        var result = LoopbackCertificateValidator.Validate(
            request,
            certificate: null,
            chain: null,
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(result, Is.False,
            "null RequestUri → cannot identify transport target; fail closed");
    }

    /// <summary>
    /// Localhost hostname is reserved per RFC 6761 to resolve only to
    /// loopback addresses; Kestrel dev installations conventionally bind
    /// <c>https://localhost:5001</c> with a dev cert. Treat the literal as
    /// a loopback alias so the bypass works for the typical dev shape.
    /// </summary>
    [Test]
    public void Validate_LocalhostHost_BypassesErrors()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://localhost:5001/"));

        var result = LoopbackCertificateValidator.Validate(
            request,
            certificate: null,
            chain: null,
            sslPolicyErrors: SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(result, Is.True,
            "localhost is a reserved loopback alias (RFC 6761) — bypass dev-cert errors");
    }
}
