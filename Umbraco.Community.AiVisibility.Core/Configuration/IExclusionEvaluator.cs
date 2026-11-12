using Umbraco.Cms.Core.Models.PublishedContent;

namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Single source of truth for the per-page exclusion check across every
/// LlmsTxt route surface: the <c>.md</c> controller, the Accept-header
/// negotiation middleware, the discoverability header middleware, the
/// <c>&lt;llms-link /&gt;</c> / <c>&lt;llms-hint /&gt;</c> Razor TagHelpers,
/// the <c>/llms.txt</c> and <c>/llms-full.txt</c> manifests (evaluated per
/// page on a manifest cache miss only), and the Settings dashboard's
/// excluded-pages Management API (called with the backoffice request host and
/// <c>culture: null</c>).
/// <para>
/// <b>Contract:</b> three exclusion sources consulted in order — (1) per-page
/// <c>excludeFromLlmExports</c> bool, plus any property listed in
/// <see cref="AiVisibilitySettings.ExclusionPropertyAliases"/> (boolean
/// <c>true</c> only; most explicit adopter signal, short-circuits first);
/// (2) Umbraco public-access protection via
/// <c>IPublicAccessService.IsProtected(content.Path)</c> — member-protected
/// pages are excluded so they never render their login-redirect HTML as
/// Markdown content; (3) <see cref="ISettingsResolver"/> doctype-alias
/// exclusion list (case-insensitive). A public-access throw is caught +
/// logged <c>Warning</c> + treated as not-protected (fail-open); a resolver
/// throw is logged <c>Warning</c> and the appsettings
/// <see cref="AiVisibilitySettings.ExcludedDoctypeAliases"/> list is used
/// instead (the same degraded path the manifests used).
/// <see cref="OperationCanceledException"/> re-throws.
/// </para>
/// <para>
/// <b>Lifetime: Scoped.</b> Depends on the request-scoped
/// <see cref="ISettingsResolver"/>; Singleton would form a captive
/// dependency at the root provider. The Umbraco-core
/// <c>IPublicAccessService</c> dependency is consumed transparently at
/// whatever lifetime Umbraco core ships (Singleton/Scoped) and does not
/// change the captive-dep shape. Pinned by
/// <c>Compose_StartupValidation_DiscoverabilityHeaderMiddleware_NoCaptiveDependency</c>.
/// </para>
/// </summary>
public interface IExclusionEvaluator
{
    /// <summary>
    /// Returns <c>true</c> when the page should be omitted from LLM exports
    /// (Markdown route, manifests, discoverability header, TagHelpers all
    /// consult the same answer).
    /// </summary>
    Task<bool> IsExcludedAsync(IPublishedContent content, string? culture, string? host, CancellationToken cancellationToken);
}
