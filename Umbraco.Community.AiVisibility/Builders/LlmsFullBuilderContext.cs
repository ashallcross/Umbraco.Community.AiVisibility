using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace LlmsTxt.Umbraco.Builders;

/// <summary>
/// Input record for <see cref="ILlmsFullBuilder.BuildAsync"/> (Story 2.2). Carries
/// the per-request state the builder needs without dragging an <c>HttpContext</c>
/// dependency into <c>Builders/</c> (architecture's folder-boundary rule — see
/// <c>architecture.md</c> § Architectural Dependency Boundaries: <c>Builders/</c>
/// must not depend on HTTP or controllers). The controller resolves and filters
/// these inputs and hands them in.
/// </summary>
/// <param name="Hostname">
/// Lower-case, port-stripped host the request arrived on. Used by the builder when
/// <see cref="Umbraco.Cms.Core.Routing.IPublishedUrlProvider.GetUrl"/> returns
/// null/empty and the builder needs to construct an absolute fallback URL for the
/// per-page <c>_Source:</c> line.
/// </param>
/// <param name="Culture">
/// BCP-47 culture the matched <see cref="Umbraco.Cms.Core.Models.IDomain"/>
/// binding declared, or the site's default culture if no domain matched.
/// Lower-cased for stability.
/// </param>
/// <param name="RootContent">
/// Root <see cref="IPublishedContent"/> the manifest scopes against — the matched
/// hostname's root, OR (when
/// <see cref="LlmsFullScopeSettings.RootContentTypeAlias"/> narrows the scope) the
/// first descendant matching that alias.
/// </param>
/// <param name="Pages">
/// Ordered list of pages the controller already walked and scope-filtered:
/// <c>IDocumentNavigationQueryService.TryGetDescendantsKeys</c> + the
/// <see cref="LlmsFullScopeSettings"/> include / exclude filters. The scope root is
/// included as the first element. Pre-collecting in the controller keeps
/// <c>Builders/</c> HTTP- and snapshot-agnostic per the architecture's
/// folder-boundary rules and makes adopter override implementations testable as
/// pure functions over the page list. Adopters that want custom traversal logic
/// can register their own <see cref="ILlmsFullBuilder"/> and ignore this list.
/// </param>
/// <param name="Settings">
/// Story 3.1 — <see cref="ResolvedLlmsSettings"/> overlay record. The full
/// builder reads <see cref="ResolvedLlmsSettings.BaseSettings"/> for the
/// non-overlaid fields it consumes (<c>MaxLlmsFullSizeKb</c>,
/// <c>LlmsFullBuilder.Order</c>); per-page exclusion is applied by the
/// controller before the page list reaches the builder.
/// <para>
/// <b>Breaking change from Story 2.x:</b> the type changed from
/// <c>AiVisibilitySettings</c> to <see cref="ResolvedLlmsSettings"/>. Adopter
/// implementations of <see cref="ILlmsFullBuilder"/> that read
/// <c>context.Settings.MaxLlmsFullSizeKb</c> must update to
/// <c>context.Settings.BaseSettings.MaxLlmsFullSizeKb</c>. Documented in the
/// v0.4 change log.
/// </para>
/// </param>
public sealed record LlmsFullBuilderContext(
    string Hostname,
    string Culture,
    IPublishedContent RootContent,
    IReadOnlyList<IPublishedContent> Pages,
    ResolvedLlmsSettings Settings);
