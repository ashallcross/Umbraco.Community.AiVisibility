using Umbraco.Cms.Core.Models.PublishedContent;

namespace LlmsTxt.Umbraco.Configuration;

/// <summary>
/// Story 4.1 — single source of truth for the per-page exclusion check across
/// every LlmsTxt route surface: the <c>.md</c> controller (Story 1.1+3.1), the
/// Accept-header negotiation middleware (Story 1.3+3.1), the discoverability
/// header middleware (Story 4.1), and the <c>&lt;llms-link /&gt;</c> /
/// <c>&lt;llms-hint /&gt;</c> Razor TagHelpers (Story 4.1).
/// <para>
/// <b>Contract:</b> per-page <c>excludeFromLlmExports</c> bool first; if not
/// excluded by bool, fall through to <see cref="ILlmsSettingsResolver"/> for
/// the resolved doctype-alias exclusion list (case-insensitive). Resolver
/// throws are caught + logged <c>Warning</c> + treated as not-excluded
/// (fail-open) — same shape Story 2.3 hreflang resolver established for
/// non-essential resolver glitches.
/// </para>
/// <para>
/// <b>Lifetime: Scoped.</b> Depends on the request-scoped
/// <see cref="ILlmsSettingsResolver"/>; Singleton would form a captive
/// dependency at the root provider. Pinned by
/// <c>Compose_StartupValidation_DiscoverabilityHeaderMiddleware_NoCaptiveDependency</c>.
/// </para>
/// </summary>
public interface ILlmsExclusionEvaluator
{
    /// <summary>
    /// Returns <c>true</c> when the page should be omitted from LLM exports
    /// (Markdown route, manifests, discoverability header, TagHelpers all
    /// consult the same answer).
    /// </summary>
    Task<bool> IsExcludedAsync(IPublishedContent content, string? culture, string? host, CancellationToken cancellationToken);
}
