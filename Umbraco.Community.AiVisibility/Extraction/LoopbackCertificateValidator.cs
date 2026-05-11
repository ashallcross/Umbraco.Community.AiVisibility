using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.2 — cert-validation callback for the named loopback
/// <see cref="HttpClient"/>. Bypass fires <b>only</b> when the request's
/// transport-target host resolves to a loopback IP via
/// <c>IPAddress.IsLoopback</c>; otherwise the default chain runs. <b>NEVER
/// blanket-bypass.</b>
/// </summary>
/// <remarks>
/// <para>
/// Static for testability — the validator is a pure function of
/// <c>request.RequestUri.Host</c> and <c>sslPolicyErrors</c>; no DI needed.
/// The composer wires it as <c>HttpClientHandler.ServerCertificateCustomValidationCallback
/// = LoopbackCertificateValidator.Validate</c>.
/// </para>
/// <para>
/// <b>Why scoped to loopback hosts only.</b> The HttpClient handler chain
/// runs the callback for every cert validation. Returning <c>true</c>
/// unconditionally would silently bypass cert validation for any
/// <c>HttpClient</c> instance the named-client factory hands out — including
/// adopters who configure
/// <c>AiVisibility:RenderStrategy:LoopbackBaseUrl</c> to point at an
/// internal load balancer with a mis-issued cert (the factory's contract is
/// "configured client name", not "guaranteed loopback target"). The
/// loopback-IP check is the canonical "safe" bypass scope.
/// </para>
/// </remarks>
internal static class LoopbackCertificateValidator
{
    /// <summary>
    /// Cert-validation callback. Returns <c>true</c> when validation should
    /// succeed despite the SSL policy errors; <c>false</c> defers to the
    /// default chain's verdict.
    /// </summary>
    /// <param name="request">The outbound HTTP request whose
    /// <c>RequestUri.Host</c> is the transport-target host. The
    /// <c>Headers.Host</c> value is irrelevant here — bypass scope is
    /// decided by the actual TCP target, not the published Host header.</param>
    /// <param name="certificate">The server cert; ignored — we look only at
    /// the host + policy errors.</param>
    /// <param name="chain">The cert chain; ignored — same reason.</param>
    /// <param name="sslPolicyErrors">The errors the default chain
    /// observed. <see cref="SslPolicyErrors.None"/> short-circuits to
    /// <c>true</c> (no bypass needed; chain succeeded already).</param>
    /// <returns>
    /// <c>true</c> when bypass is warranted (no errors, OR loopback target
    /// with errors); <c>false</c> when the default chain's verdict should
    /// stand (non-loopback target with errors, OR null transport URI as
    /// a fail-closed defensive guard).
    /// </returns>
    internal static bool Validate(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // Default chain succeeded — no bypass needed; cert is fine.
        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        // Defensive: a missing RequestUri means we cannot reason about the
        // transport target. Fail closed rather than guess.
        if (request.RequestUri is null)
        {
            return false;
        }

        var host = request.RequestUri.Host;

        // RFC 6761 reserves the "localhost" name to resolve only to loopback
        // addresses; Kestrel dev installations conventionally bind
        // "https://localhost:5001" with a dev cert. Treat the literal
        // hostname as a loopback alias so the bypass works for the typical
        // dev shape (otherwise the dev cert chain runs and fails for
        // self-signed certs in dev environments).
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Strip IPv6 brackets so IPAddress.TryParse can read the address.
        // The Uri.Host accessor returns "[::1]" with brackets for IPv6
        // literals; IPAddress wants the bare address.
        var bare = host.StartsWith('[') && host.EndsWith(']')
            ? host[1..^1]
            : host;

        if (IPAddress.TryParse(bare, out var ip) && IPAddress.IsLoopback(ip))
        {
            return true;
        }

        // External / non-loopback host with cert errors → fail per default
        // chain. NEVER blanket bypass.
        return false;
    }
}
