using LlmsTxt.Umbraco.Configuration;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace LlmsTxt.Umbraco.Builders;

/// <summary>
/// Input record for <see cref="ILlmsTxtBuilder.BuildAsync"/>. Carries the per-request
/// state the builder needs without dragging an <c>HttpContext</c> dependency into
/// <c>Builders/</c> (architecture's folder-boundary rule — see
/// <c>architecture.md</c> § Architectural Dependency Boundaries: <c>Builders/</c>
/// must not depend on HTTP or controllers). The controller resolves these inputs
/// and hands them in.
/// </summary>
/// <param name="Hostname">
/// Lower-case, port-stripped host the request arrived on. Used by the builder to
/// stamp into log messages (the hostname is NOT emitted into the manifest body
/// itself — links inside the manifest are root-relative per the llms.txt spec).
/// </param>
/// <param name="Culture">
/// BCP-47 culture the matched <see cref="IDomain"/> binding declared, or the
/// site's default culture if no domain matched. Lower-cased for stability.
/// </param>
/// <param name="RootContent">
/// Root <see cref="IPublishedContent"/> the manifest scopes against. Convenience
/// access to the H1's source name and metadata; the builder walks
/// <see cref="Pages"/> for the body, NOT the root's descendants directly.
/// </param>
/// <param name="Pages">
/// Ordered list of pages the controller already walked via
/// <see cref="Umbraco.Cms.Core.Services.Navigation.IDocumentNavigationQueryService"/>
/// from the published snapshot — root first, then descendants in tree-order.
/// Pre-collecting in the controller keeps <c>Builders/</c> HTTP- and
/// snapshot-agnostic per the architecture's folder-boundary rules and makes
/// adopter override implementations testable as pure functions over pages.
/// Adopters that want custom traversal logic can register their own
/// <see cref="ILlmsTxtBuilder"/> and ignore this list.
/// </param>
/// <param name="Settings">
/// Snapshot of <see cref="LlmsTxtSettings"/> at the time the request arrived.
/// Snapshot semantics keep a single manifest build internally consistent even if
/// <c>IOptionsMonitor</c> ticks during the build.
/// </param>
public sealed record LlmsTxtBuilderContext(
    string Hostname,
    string Culture,
    IPublishedContent RootContent,
    IReadOnlyList<IPublishedContent> Pages,
    LlmsTxtSettings Settings);
