namespace Umbraco.Community.AiVisibility.Robots;

/// <summary>
/// Story 4.2 — public extension point for the LlmsTxt robots.txt audit. The
/// default implementation (<see cref="DefaultRobotsAuditor"/>) fetches the
/// host's <c>/robots.txt</c>, parses User-agent / Disallow blocks, and
/// cross-references against the embedded
/// <c>ai-robots-txt/ai.robots.txt</c> snapshot loaded by
/// <see cref="AiBotList"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime:</b> Singleton (registered via
/// <c>RobotsComposer.TryAddSingleton&lt;IRobotsAuditor, DefaultRobotsAuditor&gt;()</c>).
/// The default implementation is stateless + thread-safe; results are
/// cached in <see cref="Umbraco.Cms.Core.Cache.AppCaches.RuntimeCache"/>
/// per hostname.
/// </para>
/// <para>
/// <b>Adopter override:</b> register a Singleton implementation BEFORE the
/// package's composer runs (<c>services.AddSingleton&lt;IRobotsAuditor, MyImpl&gt;()</c>)
/// to replace the default. <b>Do not register a Scoped or Transient
/// override</b> — the singleton <see cref="Umbraco.Community.AiVisibility.Background.RobotsAuditRefreshJob"/>
/// captures the auditor by constructor and a Scoped/Transient lifetime
/// would form a captive dependency caught at composition time by
/// <c>ServiceProviderOptions.ValidateScopes = ValidateOnBuild = true</c>.
/// </para>
/// </remarks>
public interface IRobotsAuditor
{
    /// <summary>
    /// Audit the named host's <c>/robots.txt</c> and return the result.
    /// Implementations may return a cached result if one is current.
    /// </summary>
    /// <param name="hostname">Host portion only — <c>example.com</c>, not
    /// <c>https://example.com/</c>. Lowercased on use.</param>
    /// <param name="scheme">HTTP scheme to fetch with — <c>"https"</c> or
    /// <c>"http"</c>. Defaults to <c>"https"</c> at the consumer layer when
    /// the caller has no specific signal.</param>
    /// <param name="cancellationToken">Honoured through every awaited call
    /// (HTTP fetch, cache factory).</param>
    Task<RobotsAuditResult> AuditAsync(
        string hostname,
        string scheme,
        CancellationToken cancellationToken);

    /// <summary>
    /// Force a fresh audit, bypassing any cached state on the way IN, and
    /// re-inserting the result on the way OUT. Used by the Backoffice Health
    /// Check view (where the editor's intent is "show me current state") and
    /// by <see cref="Umbraco.Community.AiVisibility.Background.RobotsAuditRefreshJob"/>.
    /// <para>
    /// Default implementation forwards to <see cref="AuditAsync"/> for
    /// backward compatibility with adopter implementations that don't
    /// distinguish cached / fresh paths.
    /// </para>
    /// </summary>
    Task<RobotsAuditResult> RefreshAsync(
        string hostname,
        string scheme,
        CancellationToken cancellationToken)
        => AuditAsync(hostname, scheme, cancellationToken);
}
