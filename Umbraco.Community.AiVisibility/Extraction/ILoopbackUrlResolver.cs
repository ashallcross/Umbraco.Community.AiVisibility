namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.2 — resolves the local TCP transport target the
/// <c>LoopbackPageRendererStrategy</c> should hit when issuing an inbound HTTP
/// request against the package's own host. Decoupled from the request
/// pipeline so the strategy can stay testable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy resolution.</b> Implementations MUST NOT walk
/// <c>IServer.Features</c> from the constructor. Resolution happens on the
/// first <see cref="Resolve"/> call. This is load-bearing — adopters pinned
/// to <c>RenderStrategyMode.Razor</c> never call into the loopback strategy
/// and therefore never call into <see cref="Resolve"/>; an environment
/// without a usable Kestrel binding (no listening addresses, headless
/// hosting, exotic networking topology) MUST NOT brick application startup
/// for those adopters.
/// </para>
/// <para>
/// <b>Caching.</b> The first successful <see cref="Resolve"/> call should
/// cache its result for the process lifetime. <c>IServer.Features</c> is
/// stable post-startup (Kestrel binding changes require a restart) so
/// re-walking on every render adds cost without value. Concurrent first-call
/// races are race-tolerant — two threads both walking the bindings produce
/// the same answer; last write wins, no correctness impact.
/// </para>
/// <para>
/// <b>Failure shape.</b> When neither the configured override nor any
/// <c>IServer.Features</c> binding produces a usable target, implementations
/// throw <see cref="System.InvalidOperationException"/> at the
/// <see cref="Resolve"/> call site, NOT at startup. The diagnostic should
/// name the bound addresses observed (or report none) AND the
/// <c>AiVisibility:RenderStrategy:LoopbackBaseUrl</c> escape-hatch setting.
/// </para>
/// <para>
/// <b>Extension point.</b> Adopters with exotic networking topologies who
/// need bespoke resolution can register their own implementation via
/// <c>services.TryAddSingleton&lt;ILoopbackUrlResolver, MyResolver&gt;()</c>
/// — the package's default registration uses <c>TryAdd*</c> so adopter
/// substitutions win without removing-then-re-adding. The interface is
/// package-internal; tests and adopter overrides reach it via
/// <c>InternalsVisibleTo("Umbraco.Community.AiVisibility.Tests")</c> already
/// configured in the project.
/// </para>
/// </remarks>
internal interface ILoopbackUrlResolver
{
    /// <summary>
    /// Resolves the loopback transport target. Lazy: first call may walk
    /// <c>IServer.Features</c> or parse the configured override; subsequent
    /// calls return the cached result. Throws
    /// <see cref="System.InvalidOperationException"/> when no usable target
    /// resolves.
    /// </summary>
    /// <returns>
    /// A <see cref="LoopbackTarget"/> carrying the resolved transport
    /// <see cref="LoopbackTarget.TransportUri"/> AND a flag indicating
    /// whether the resolved host is a loopback IP (in which case the
    /// <c>HttpClient</c>'s cert-validation callback may bypass the default
    /// chain — see <c>LoopbackCertificateValidator</c>).
    /// </returns>
    LoopbackTarget Resolve();
}

/// <summary>
/// Story 7.2 — value carrying both the loopback transport <see cref="Uri"/>
/// and a flag indicating whether the resolved host qualifies for cert
/// validation bypass. Cert-bypass eligibility is decided by the resolver
/// (which knows the host) and consumed by the cert callback (which sees
/// the request); inlining the flag here avoids the callback having to re-run
/// <c>IPAddress.IsLoopback</c> for every render.
/// </summary>
/// <param name="TransportUri">
/// Absolute URI for the loopback transport target. <see cref="Uri.Host"/> is
/// the local Kestrel binding (e.g. <c>127.0.0.1</c>, <c>[::1]</c>) or the
/// host parsed from <c>AiVisibility:RenderStrategy:LoopbackBaseUrl</c> when
/// configured.
/// </param>
/// <param name="CertBypassEligible">
/// <c>true</c> when <see cref="TransportUri"/>'s host is a loopback IP per
/// <c>IPAddress.IsLoopback</c>; <c>false</c> otherwise (in which case the
/// default cert validation chain runs, even when the strategy speaks HTTPS
/// to the resolved target).
/// </param>
internal readonly record struct LoopbackTarget(Uri TransportUri, bool CertBypassEligible);
