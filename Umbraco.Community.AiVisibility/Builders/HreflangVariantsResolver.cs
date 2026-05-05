using Umbraco.Community.AiVisibility.Caching;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace LlmsTxt.Umbraco.Builders;

/// <summary>
/// Story 2.3 — resolves sibling-culture variants for the matched manifest pages
/// when <c>LlmsTxt:Hreflang:Enabled</c> is <c>true</c> (FR25). Only invoked from
/// the <see cref="Controllers.LlmsTxtController"/> hot path; <c>/llms-full.txt</c>
/// is hreflang-blind by design (AC3 last bullet — the full manifest is a
/// single-culture concatenated dump consumed off-site).
/// <para>
/// Design: walks <see cref="IDomainService.GetAll"/> to discover sibling-culture
/// domains bound to the same root content as the matched culture, then for each
/// page in the manifest looks up its variants via
/// <see cref="IPublishedContent.Cultures"/> + <see cref="IPublishedUrlProvider.GetUrl(IPublishedContent, UrlMode, string?, Uri?)"/>.
/// </para>
/// <para>
/// Failures are non-fatal — one bad variant must not 500 the manifest. URL
/// provider exceptions, missing cultures, etc., are caught + logged + that
/// variant is omitted; other variants on the same page (and other pages)
/// continue normally.
/// </para>
/// <para>
/// DI lifetime: <b>Singleton</b>. Stateless — all state is in the
/// <see cref="ResolveAsync"/> parameters. Dependencies are Umbraco singleton
/// abstractions (<see cref="IDomainService"/>, <see cref="IPublishedUrlProvider"/>);
/// the resolver always runs inside an <see cref="IUmbracoContextFactory.EnsureUmbracoContext"/>
/// scope set up by the controller, so the URL provider and cache snapshot are
/// safely accessible.
/// </para>
/// </summary>
public interface IHreflangVariantsResolver
{
    /// <summary>
    /// Resolve sibling-culture variants for each page. Returns a dictionary
    /// keyed by <see cref="IPublishedContent.Key"/> mapping to the page's
    /// variants. Returns an empty (non-null) dictionary when no variants exist
    /// (single-culture site, or all sibling-bound roots differ from the matched
    /// root).
    /// </summary>
    /// <param name="pages">Pages already collected by the controller (root + descendants).</param>
    /// <param name="matchedCulture">BCP-47 lowercased culture for the request's matched <see cref="IDomain"/>.</param>
    /// <param name="matchedRootKey"><see cref="IPublishedContent.Key"/> of the matched site root.</param>
    /// <param name="umbracoContext">Ambient Umbraco context the controller already opened.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>> ResolveAsync(
        IReadOnlyList<IPublishedContent> pages,
        string matchedCulture,
        Guid matchedRootKey,
        IUmbracoContext umbracoContext,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IHreflangVariantsResolver"/>
public sealed class HreflangVariantsResolver : IHreflangVariantsResolver
{
    private readonly IDomainService _domainService;
    private readonly IPublishedUrlProvider _publishedUrlProvider;
    private readonly ILogger<HreflangVariantsResolver> _logger;

    public HreflangVariantsResolver(
        IDomainService domainService,
        IPublishedUrlProvider publishedUrlProvider,
        ILogger<HreflangVariantsResolver> logger)
    {
        _domainService = domainService;
        _publishedUrlProvider = publishedUrlProvider;
        _logger = logger;
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>> ResolveAsync(
        IReadOnlyList<IPublishedContent> pages,
        string matchedCulture,
        Guid matchedRootKey,
        IUmbracoContext umbracoContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(matchedCulture);
        ArgumentNullException.ThrowIfNull(umbracoContext);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = umbracoContext.Content;
        if (snapshot is null)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>>(
                EmptyResult);
        }

        // Step 1 — discover sibling cultures for the matched root (any IDomain
        // binding whose RootContentId resolves to the matched root, EXCLUDING
        // the matched culture itself).
        IReadOnlyList<string> siblingCultures;
        try
        {
            siblingCultures = DiscoverSiblingCultures(snapshot, matchedRootKey, matchedCulture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Hreflang — failed to enumerate sibling-culture domains for root {RootKey}; emitting no variants",
                matchedRootKey);
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>>(
                EmptyResult);
        }

        if (siblingCultures.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>>(
                EmptyResult);
        }

        // Step 2 — for each page, look up variants in each sibling culture.
        var result = new Dictionary<Guid, IReadOnlyList<HreflangVariant>>(pages.Count);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variants = ResolveVariantsForPage(page, siblingCultures);
            if (variants.Count > 0)
            {
                result[page.Key] = variants;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>>(result);
    }

    private IReadOnlyList<string> DiscoverSiblingCultures(
        IPublishedContentCache snapshot,
        Guid matchedRootKey,
        string matchedCulture)
    {
        var siblings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include wildcards: Umbraco serialises culture-only path-bound second
        // cultures under a node as `*<rootId>` (IsWildcard=true). Excluding
        // wildcards would miss those — the most common multi-culture pattern
        // when adopters use a single hostname with culture sub-paths.
        // Subdomain wildcards (`*.example.com` shape) are filtered below.
        foreach (var domain in _domainService.GetAll(includeWildcards: true))
        {
            if (domain.RootContentId is not int rootId
                || string.IsNullOrWhiteSpace(domain.LanguageIsoCode))
            {
                continue;
            }

            // Skip true subdomain wildcards — `*.example.com`-shape bindings
            // describe a wildcard hostname match, NOT a culture-bound sibling
            // of the matched root.
            var raw = domain.DomainName;
            if (!string.IsNullOrEmpty(raw) && raw.StartsWith("*.", StringComparison.Ordinal))
            {
                continue;
            }

            var domainRoot = snapshot.GetById(rootId);
            if (domainRoot is null || domainRoot.Key != matchedRootKey)
            {
                continue;
            }

            var culture = domain.LanguageIsoCode!.ToLowerInvariant();
            if (string.Equals(culture, matchedCulture, StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(culture))
            {
                siblings.Add(culture);
            }
        }

        return siblings;
    }

    private IReadOnlyList<HreflangVariant> ResolveVariantsForPage(
        IPublishedContent page,
        IReadOnlyList<string> siblingCultures)
    {
        List<HreflangVariant>? variants = null;

        foreach (var culture in siblingCultures)
        {
            // Skip cultures the page is not published in. IPublishedContent.Cultures
            // returns the cultures in which this node is currently published; if
            // a culture isn't present, GetUrl would either return empty or throw.
            // Defensively check both.
            try
            {
                // Case-insensitive lookup: Umbraco's `IPublishedContent.Cultures`
                // keys preserve the casing the language was registered with
                // (e.g. `cy-GB` from `umbracoLanguage.languageISOCode`), while
                // our `siblingCultures` are lowercased BCP-47 (`cy-gb`). A naive
                // `ContainsKey(culture)` on an ordinal-comparer dictionary would
                // miss; iterate keys with case-insensitive comparison instead.
                if (page.Cultures is { Count: > 0 }
                    && !page.Cultures.Keys.Any(k => string.Equals(k, culture, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Hreflang — IPublishedContent.Cultures threw for {ContentKey} {Culture}; skipping variant",
                    page.Key,
                    culture);
                continue;
            }

            string? relative;
            try
            {
                relative = _publishedUrlProvider.GetUrl(page, UrlMode.Relative, culture);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Hreflang — IPublishedUrlProvider.GetUrl threw for {ContentKey} {Culture}; skipping variant",
                    page.Key,
                    culture);
                continue;
            }

            if (string.IsNullOrWhiteSpace(relative) || relative == "#")
            {
                // Umbraco returns "#" when a page isn't published in the requested
                // culture or no URL is bound (e.g. a culture-variant `IDomain` is
                // registered for the root but has no concrete hostname or path
                // prefix — the `*<rootId>` placeholder shape). Skip silently —
                // common steady-state for pages that exist in the matched culture
                // only OR sites whose multi-culture binding is incomplete.
                continue;
            }

            // Skip absolute URIs regardless of scheme — multi-site misconfig
            // OR an explicit `file://` / `http(s)://` leak from a custom URL
            // provider. The variant slot is for paths relative to the matched
            // site root only.
            //
            // Discriminator: real absolute URIs start with a scheme prefix
            // (`https:`, `file:`, etc.); relative paths start with `/`. We
            // must distinguish because on Unix `Uri.TryCreate("/path", Absolute)`
            // succeeds and produces `file:///path` — without the leading-slash
            // gate, every relative path on Linux/macOS would be misclassified
            // as absolute.
            if (!relative.StartsWith('/')
                && Uri.TryCreate(relative, UriKind.Absolute, out _))
            {
                continue;
            }

            // Restrict the variant URL alphabet — D2 from code review (2026-04-30).
            // The output line shape is `({culture}: {url})` (DefaultLlmsTxtBuilder.AppendLink).
            // URLs containing `(`, `)`, `?`, or `#` ambiguate the parenthesised
            // variant grouping for downstream parsers. Skip silently and log
            // Information so an operator who configures unusual URLs sees the
            // omission. Spec AC3 defines the format but no escape rule; this
            // restriction preserves verbatim-URL readability without imposing
            // percent-encoding inconsistency between primary and variant slots.
            if (relative.AsSpan().IndexOfAny('(', ')', '?') >= 0
                || relative.IndexOf('#') >= 0)
            {
                _logger.LogInformation(
                    "Hreflang — variant URL contains unsafe character ({Url}) for {ContentKey} {Culture}; skipping variant",
                    relative,
                    page.Key,
                    culture);
                continue;
            }

            var trimmed = relative.TrimEnd();
            var withSuffix = trimmed.EndsWith('/')
                ? trimmed + "index.html.md"
                : trimmed + Constants.Routes.MarkdownSuffix;

            variants ??= new List<HreflangVariant>();
            variants.Add(new HreflangVariant(AiVisibilityCacheKeys.NormaliseCulture(culture), withSuffix));
        }

        return (IReadOnlyList<HreflangVariant>?)variants ?? Array.Empty<HreflangVariant>();
    }

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>> EmptyResult
        = new Dictionary<Guid, IReadOnlyList<HreflangVariant>>(0);
}
