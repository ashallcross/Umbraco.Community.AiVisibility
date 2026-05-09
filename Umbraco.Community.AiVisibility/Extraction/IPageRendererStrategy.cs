using Umbraco.Cms.Core.Models.PublishedContent;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.1 — strategy seam for the in-process / loopback / auto rendering
/// of Umbraco published content to HTML before extraction. <c>PageRenderer</c>
/// is now a thin orchestrator that dispatches to one of these by
/// <c>AiVisibility:RenderStrategy:Mode</c>; strategies own the entire HTML
/// production path for their mode.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime: Transient.</b> The Razor strategy
/// (<see cref="RazorPageRendererStrategy"/>) mutates
/// <c>IVariationContextAccessor.VariationContext</c> in a try/finally during
/// each render to apply the requested culture; a Singleton instance would
/// race that mutation across concurrent renders. Other strategies follow the
/// same lifetime by default until a captive-dependency validation test
/// proves otherwise. Adopters replacing a strategy via
/// <c>services.TryAddKeyedTransient</c> MUST keep Transient lifetime unless
/// they have explicit per-class justification + a
/// <c>NoCaptiveDependency</c> test.
/// </para>
/// <para>
/// <b>Implementations (Story 7.1 onwards):</b>
/// <list type="bullet">
/// <item><see cref="RazorPageRendererStrategy"/> — Story 7.1; the v1.0
/// in-process Razor render path extracted verbatim. Registered keyed by
/// <c>RenderStrategyMode.Razor</c>.</item>
/// <item><c>LoopbackPageRendererStrategy</c> — Story 7.2; HTTP loopback
/// against the package's own host. Will register keyed by
/// <c>RenderStrategyMode.Loopback</c>.</item>
/// <item><c>AutoPageRendererStrategy</c> — Story 7.3; composes Razor +
/// Loopback with a <c>ModelBindingException</c> fallback and a
/// <c>(ContentTypeAlias, TemplateAlias)</c> decision cache. Will register
/// keyed by <c>RenderStrategyMode.Auto</c> and become the default Mode
/// when shipped.</item>
/// </list>
/// </para>
/// <para>
/// The interface is package-internal —
/// <c>InternalsVisibleTo("Umbraco.Community.AiVisibility.Tests")</c> is
/// configured in the project so the test fixtures can substitute it.
/// Promotion to public API is a one-way decision deferred until adopter
/// demand surfaces.
/// </para>
/// <para>
/// <b>Lifetime-safety constraint imposed by the orchestrator's captured
/// <c>IServiceProvider</c>.</b> <c>PageRenderer</c> resolves strategies
/// through a captured <see cref="System.IServiceProvider"/> that — depending
/// on where <c>PageRenderer</c> itself is resolved — may be the root
/// container. Implementations therefore MUST be Singleton- or Transient-safe.
/// A strategy registered with <c>Scoped</c> lifetime resolves cleanly only
/// when <c>PageRenderer</c> is itself resolved from a request scope; relying
/// on captive resolution is fragile and breaks under
/// <c>ServiceProviderOptions.ValidateScopes = true</c>. Strategies that
/// genuinely need per-request scope (for example, an
/// <c>IHttpClientFactory</c>-managed handler with per-request handler
/// lifetime) MUST create their own scope via
/// <c>IServiceScopeFactory.CreateScope()</c> rather than depending on the
/// orchestrator to deliver one.
/// </para>
/// </remarks>
internal interface IPageRendererStrategy
{
    /// <summary>
    /// Render the resolved <paramref name="content"/> to HTML for
    /// extraction. Implementations are responsible for honouring
    /// <paramref name="culture"/>, propagating
    /// <paramref name="cancellationToken"/>, and packaging the outcome
    /// (success or failure) into a <see cref="PageRenderResult"/>.
    /// </summary>
    /// <param name="content">Already-resolved <see cref="IPublishedContent"/>
    /// — the controller resolves the route in Story 1.2 and hands the
    /// content to the renderer; strategies do NOT re-route.</param>
    /// <param name="absoluteUri">Absolute request URI used by the Razor
    /// strategy's <c>PublishedRequestBuilder</c> and by the Loopback
    /// strategy as the inbound HTTP target.</param>
    /// <param name="culture">Optional culture string applied to
    /// <c>IPublishedRequestBuilder.SetCulture</c> + the
    /// <c>VariationContext</c>; <c>null</c> falls back to the invariant
    /// path.</param>
    /// <param name="cancellationToken">Honoured end-to-end.
    /// <see cref="System.OperationCanceledException"/> propagates without
    /// being wrapped into a <see cref="PageRenderResult.Failed"/>
    /// outcome.</param>
    Task<PageRenderResult> RenderAsync(
        IPublishedContent content,
        Uri absoluteUri,
        string? culture,
        CancellationToken cancellationToken);
}
