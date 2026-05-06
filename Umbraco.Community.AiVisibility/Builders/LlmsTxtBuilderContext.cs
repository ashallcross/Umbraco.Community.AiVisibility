using Umbraco.Community.AiVisibility.Configuration;
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
/// Story 3.1 — <see cref="ResolvedLlmsSettings"/> overlay record (Settings
/// doctype values overlaid onto the appsettings snapshot). The builder reads
/// <see cref="ResolvedLlmsSettings.SiteName"/> /
/// <see cref="ResolvedLlmsSettings.SiteSummary"/> directly for the H1 /
/// blockquote (overlaid values); fields the resolver doesn't overlay
/// (<c>LlmsTxtBuilder.SectionGrouping</c>, <c>LlmsTxtBuilder.PageSummaryPropertyAlias</c>,
/// <c>Hreflang.Enabled</c>) are reachable via
/// <see cref="ResolvedLlmsSettings.BaseSettings"/>.
/// <para>
/// <b>Breaking change from Story 2.x:</b> the type changed from
/// <c>AiVisibilitySettings</c> to <see cref="ResolvedLlmsSettings"/>. Adopter
/// implementations of <see cref="ILlmsTxtBuilder"/> that read
/// <c>context.Settings.LlmsTxtBuilder.SectionGrouping</c> must update to
/// <c>context.Settings.BaseSettings.LlmsTxtBuilder.SectionGrouping</c>
/// (etc.). Documented in the v0.4 change log.
/// </para>
/// </param>
/// <param name="HreflangVariants">
/// Story 2.3 — sibling-culture variants per page key, or <c>null</c> when
/// hreflang is disabled (default) OR no variants exist. Builder treats
/// <c>null</c> and empty dictionary identically — both produce Story 2.1's
/// byte-identical output. The controller resolves variants via the matched
/// <see cref="IDomain"/> set (one domain per <c>(root, culture)</c> pair) and
/// stuffs the result here, keeping <c>Builders/</c> HTTP-agnostic. See
/// <see cref="HreflangVariant"/> for the per-variant payload shape.
/// </param>
public sealed record LlmsTxtBuilderContext(
    string Hostname,
    string Culture,
    IPublishedContent RootContent,
    IReadOnlyList<IPublishedContent> Pages,
    ResolvedLlmsSettings Settings,
    IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>? HreflangVariants = null);

/// <summary>
/// Story 2.3 — one sibling-culture variant of a manifest page. Emitted by
/// <see cref="DefaultLlmsTxtBuilder"/> as <c>(culture: relativeMarkdownUrl)</c>
/// after each link line when <see cref="HreflangSettings.Enabled"/> is <c>true</c>.
/// </summary>
/// <param name="Culture">
/// BCP-47 culture for the variant, lowercased (e.g. <c>fr-fr</c>). Lexicographic
/// ordering of variants in the manifest is by this field.
/// </param>
/// <param name="RelativeMarkdownUrl">
/// The variant's <c>.md</c>-suffixed root-relative URL, ready to embed verbatim
/// in the manifest (e.g. <c>/fr/about.md</c>). The controller does the suffixing
/// before constructing this record so the builder stays a pure transform.
/// </param>
public sealed record HreflangVariant(string Culture, string RelativeMarkdownUrl);
