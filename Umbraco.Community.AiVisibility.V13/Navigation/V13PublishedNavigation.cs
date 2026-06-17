using Umbraco.Cms.Core.Models.PublishedContent;

namespace Umbraco.Community.AiVisibility.V13.Navigation;

/// <summary>
/// v13 published-content descendant enumeration (Story 9.2, hybrid shell — see
/// Spec Drift SDN-1).
///
/// <para>
/// The v17 manifest controllers walk descendants via
/// <c>IDocumentNavigationQueryService.TryGetDescendantsKeys(rootKey, out keys)</c>
/// and then re-fetch each key from the <c>IPublishedContentCache</c>. That service
/// does not exist on Umbraco v13. Because the controllers already hold the root as
/// an <see cref="IPublishedContent"/> (resolved from the published cache inside an
/// <c>EnsureUmbracoContext()</c> scope), v13 can walk the published tree directly
/// via the <see cref="IPublishedContent.Children"/> property — which is published-
/// cache-backed and returns the same published nodes the v17 key round-trip would
/// have resurfaced. Tree-order (depth-first, parent before children) is preserved,
/// matching the v17 collection order.
/// </para>
///
/// <para>
/// Culture note: <see cref="IPublishedContent.Children"/> resolves against the
/// ambient variation context, so on a multi-culture site this enumerates pages
/// published in the current culture — which is exactly what a per-culture manifest
/// should contain. For single-culture sites (the common case) the result is
/// identical to the v17 walk.
/// </para>
/// </summary>
internal static class V13PublishedNavigation
{
    /// <summary>
    /// Published descendants of <paramref name="root"/> in depth-first tree order,
    /// EXCLUDING the root itself (mirrors <c>TryGetDescendantsKeys</c> semantics).
    /// </summary>
    public static IEnumerable<IPublishedContent> Descendants(IPublishedContent root)
    {
        foreach (var child in root.Children ?? Enumerable.Empty<IPublishedContent>())
        {
            yield return child;

            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
