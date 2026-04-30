using System.Text;
using LlmsTxt.Umbraco.Builders;
using LlmsTxt.Umbraco.Caching;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace LlmsTxt.Umbraco.Controllers;

/// <summary>
/// Story 2.2 — emits the <c>/llms-full.txt</c> manifest. Mirrors
/// <see cref="LlmsTxtController"/>'s shape: resolves the request hostname to an
/// Umbraco root via <see cref="IHostnameRootResolver"/>, applies the configured
/// <see cref="LlmsFullScopeSettings"/> filter (root narrowing + include/exclude
/// doctype filter) inside the active <c>IUmbracoContext</c>, checks the in-memory
/// cache (<c>llms:llmsfull:{host}:{culture}</c>), and on a miss delegates to
/// <see cref="ILlmsFullBuilder"/> for the body. Headers:
/// <c>Content-Type: text/markdown; charset=utf-8</c>, <c>Cache-Control: public,
/// max-age={LlmsFullBuilder.CachePolicySeconds}</c>, <c>Vary: Accept</c>.
/// <para>
/// Story 2.3 — emits an <c>ETag</c> header (<see cref="ManifestETag"/>) and
/// honours <c>If-None-Match</c> revalidation by short-circuiting to
/// <c>304 Not Modified</c> via <see cref="IfNoneMatchMatcher"/>. <b>Hreflang is
/// NOT applied here</b> (AC3 last bullet — the full manifest is a single-culture
/// concatenated dump consumed off-site). Single-flight on cache miss is provided
/// by <see cref="IAppPolicyCache.GetCacheItem"/>'s factory-delegate serialisation
/// per key per instance (Story 1.2 contract).
/// </para>
/// </summary>
public sealed class LlmsFullTxtController : Controller
{
    private readonly ILlmsFullBuilder _builder;
    private readonly IHostnameRootResolver _hostnameResolver;
    private readonly IUmbracoContextFactory _umbracoContextFactory;
    private readonly IDocumentNavigationQueryService _navigation;
    private readonly AppCaches _appCaches;
    private readonly IOptionsMonitor<LlmsTxtSettings> _settings;
    private readonly ILogger<LlmsFullTxtController> _logger;

    public LlmsFullTxtController(
        ILlmsFullBuilder builder,
        IHostnameRootResolver hostnameResolver,
        IUmbracoContextFactory umbracoContextFactory,
        IDocumentNavigationQueryService navigation,
        AppCaches appCaches,
        IOptionsMonitor<LlmsTxtSettings> settings,
        ILogger<LlmsFullTxtController> logger)
    {
        _builder = builder;
        _hostnameResolver = hostnameResolver;
        _umbracoContextFactory = umbracoContextFactory;
        _navigation = navigation;
        _appCaches = appCaches;
        _settings = settings;
        _logger = logger;
    }

    [HttpGet]
    [HttpHead]
    public async Task<IActionResult> Render(CancellationToken cancellationToken)
    {
        var host = HttpContext.Request.Host.HasValue
            ? HttpContext.Request.Host.Host
            : null;

        var settingsSnapshot = _settings.CurrentValue;

        HostnameRootResolution resolution;
        IPublishedContent scopeRoot;
        IReadOnlyList<IPublishedContent> pages;
        using (var ctxRef = _umbracoContextFactory.EnsureUmbracoContext())
        {
            resolution = _hostnameResolver.Resolve(host ?? string.Empty, ctxRef.UmbracoContext);
            if (resolution.Root is null || resolution.Culture is null)
            {
                _logger.LogInformation(
                    "/llms-full.txt — no resolvable root for {Host}; returning 404",
                    host);
                return Problem(
                    title: "/llms-full.txt — no resolvable root",
                    statusCode: StatusCodes.Status404NotFound);
            }

            scopeRoot = ResolveScopeRoot(
                resolution.Root,
                ctxRef.UmbracoContext.Content,
                settingsSnapshot.LlmsFullScope.RootContentTypeAlias,
                host);

            pages = CollectAndFilterPages(
                scopeRoot,
                ctxRef.UmbracoContext.Content,
                settingsSnapshot.LlmsFullScope);
        }

        var culture = resolution.Culture;
        var cacheKey = LlmsCacheKeys.LlmsFull(host, culture);

        // CachePolicySeconds policy: 0 disables the manifest cache entirely (matches
        // LlmsTxtSettings.CachePolicySeconds xmldoc — "0 effectively disables
        // caching"). Negative values are an operator typo — log Warning, treat as 0
        // (mirrors MaxLlmsFullSizeKb's "≤ 0 → unlimited + Warning" defensive policy).
        var policySeconds = settingsSnapshot.LlmsFullBuilder.CachePolicySeconds;
        if (policySeconds < 0)
        {
            _logger.LogWarning(
                "/llms-full.txt — CachePolicySeconds {Value} is negative for {Host}; treating as 0 (cache disabled)",
                policySeconds,
                host);
            policySeconds = 0;
        }

        // Capture closure inputs — see LlmsTxtController for the rationale on
        // passing CancellationToken.None into the builder via the cache factory
        // (the cached body is shared across requests; one caller's cancellation
        // must not poison parked callers).
        var hostForBuild = LlmsCacheKeys.NormaliseHost(host);
        var rootForBuild = scopeRoot;
        var pagesForBuild = pages;

        ManifestCacheEntry Build()
        {
            var ctx = new LlmsFullBuilderContext(
                Hostname: hostForBuild,
                Culture: culture,
                RootContent: rootForBuild,
                Pages: pagesForBuild,
                Settings: settingsSnapshot);
            var body = _builder.BuildAsync(ctx, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            // Story 2.3 — ETag computed once at build time and reused across
            // cache hits (AC6). Empty body still emits a stable ETag (hash of
            // zero bytes) so the empty-manifest path can still be conditionally
            // requested.
            return new ManifestCacheEntry(body, ManifestETag.Compute(body));
        }

        ManifestCacheEntry? entry;
        try
        {
            if (policySeconds == 0)
            {
                entry = Build();
            }
            else
            {
                entry = _appCaches.RuntimeCache.GetCacheItem<ManifestCacheEntry>(
                    cacheKey,
                    Build,
                    timeout: TimeSpan.FromSeconds(policySeconds));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "/llms-full.txt — builder threw for {Host} {Culture}",
                hostForBuild,
                culture);
            return Problem(
                title: "/llms-full.txt — builder failed",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        entry ??= new ManifestCacheEntry(string.Empty, ManifestETag.Compute(string.Empty));

        // Empty body is a valid manifest when scope filtering rejected every page
        // (200 OK + empty body, NOT 404 — 404 is reserved for resolver-level
        // failures). But empty usually signals a misconfig (include/exclude collision,
        // alias typo); log Warning so it's observable and clear the cache entry so
        // an operator who fixes the config doesn't have to wait CachePolicySeconds
        // for recovery.
        if (string.IsNullOrEmpty(entry.Body))
        {
            _logger.LogWarning(
                "/llms-full.txt — manifest empty for {Host} {Culture} (likely scope misconfiguration); not caching",
                hostForBuild,
                culture);
            if (policySeconds > 0)
            {
                _appCaches.RuntimeCache.ClearByKey(cacheKey);
            }
        }

        var response = HttpContext.Response;
        VaryHeaderHelper.AppendAccept(HttpContext);
        response.Headers[Constants.HttpHeaders.CacheControl] =
            $"public, max-age={Math.Max(0, settingsSnapshot.LlmsFullBuilder.CachePolicySeconds)}";
        response.Headers[Constants.HttpHeaders.ETag] = entry.ETag;

        // Story 2.3 AC1 — If-None-Match revalidation. RFC 7232 § 4.1: 304 carries
        // the same Vary/Cache-Control/ETag as a 200 would, but NOT Content-Type.
        if (IfNoneMatchMatcher.Matches(HttpContext.Request, entry.ETag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            response.ContentType = null;
            return new EmptyResult();
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = Constants.HttpHeaders.MarkdownContentType;

        if (!HttpMethods.IsHead(HttpContext.Request.Method))
        {
            await response.WriteAsync(entry.Body, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Resolve the manifest scope root from the configured
    /// <see cref="LlmsFullScopeSettings.RootContentTypeAlias"/>. Null/empty alias
    /// → use the hostname's resolved root verbatim. Non-null alias → walk the
    /// hostname tree (root + descendants in document order) and return the FIRST
    /// node whose <c>ContentType.Alias</c> matches case-insensitively. No match
    /// → fall back to the hostname root + log <c>Warning</c> (per Failure &amp;
    /// Edge Cases).
    /// </summary>
    private IPublishedContent ResolveScopeRoot(
        IPublishedContent hostnameRoot,
        IPublishedContentCache? snapshot,
        string? rootContentTypeAlias,
        string? host)
    {
        if (string.IsNullOrEmpty(rootContentTypeAlias))
        {
            return hostnameRoot;
        }
        if (string.IsNullOrWhiteSpace(rootContentTypeAlias))
        {
            _logger.LogWarning(
                "/llms-full.txt — RootContentTypeAlias is whitespace-only for {Host}; treating as no narrowing (likely operator typo)",
                host);
            return hostnameRoot;
        }

        // The hostname root itself may match the alias.
        if (string.Equals(hostnameRoot.ContentType.Alias, rootContentTypeAlias, StringComparison.OrdinalIgnoreCase))
        {
            return hostnameRoot;
        }

        if (snapshot is not null
            && _navigation.TryGetDescendantsKeys(hostnameRoot.Key, out var descendants))
        {
            foreach (var descendantKey in descendants)
            {
                var page = snapshot.GetById(descendantKey);
                if (page is not null
                    && string.Equals(page.ContentType.Alias, rootContentTypeAlias, StringComparison.OrdinalIgnoreCase))
                {
                    return page;
                }
            }
        }

        _logger.LogWarning(
            "/llms-full.txt — RootContentTypeAlias {Alias} matched no descendant under {Host}; using hostname root",
            rootContentTypeAlias,
            host);
        return hostnameRoot;
    }

    /// <summary>
    /// Walk the published content under <paramref name="scopeRoot"/> in tree-order
    /// (root first, then descendants per
    /// <see cref="IDocumentNavigationQueryService.TryGetDescendantsKeys"/>) and
    /// apply the <see cref="LlmsFullScopeSettings.IncludedDocTypeAliases"/> /
    /// <see cref="LlmsFullScopeSettings.ExcludedDocTypeAliases"/> filters.
    /// <see cref="LlmsFullScopeSettings.ExcludedDocTypeAliases"/> always wins over
    /// <see cref="LlmsFullScopeSettings.IncludedDocTypeAliases"/> on overlap.
    /// <para>
    /// Pre-collecting in the controller keeps
    /// <see cref="DefaultLlmsFullBuilder"/> pure-function over its inputs (the
    /// <c>Builders/</c> folder boundary forbids HTTP / context dependencies).
    /// </para>
    /// </summary>
    private IReadOnlyList<IPublishedContent> CollectAndFilterPages(
        IPublishedContent scopeRoot,
        IPublishedContentCache? snapshot,
        LlmsFullScopeSettings scope)
    {
        var includeSet = (scope.IncludedDocTypeAliases ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludeSet = (scope.ExcludedDocTypeAliases ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool IsIncluded(IPublishedContent p)
        {
            var alias = p.ContentType.Alias;
            if (excludeSet.Contains(alias))
            {
                return false;
            }
            if (includeSet.Count > 0 && !includeSet.Contains(alias))
            {
                return false;
            }
            return true;
        }

        var result = new List<IPublishedContent>();
        if (IsIncluded(scopeRoot))
        {
            result.Add(scopeRoot);
        }

        if (snapshot is null || !_navigation.TryGetDescendantsKeys(scopeRoot.Key, out var descendants))
        {
            return result;
        }

        foreach (var descendantKey in descendants)
        {
            if (descendantKey == scopeRoot.Key)
            {
                // Defensive — skip if navigation includes the root in descendants.
                continue;
            }

            var page = snapshot.GetById(descendantKey);
            if (page is not null && IsIncluded(page))
            {
                result.Add(page);
            }
        }

        return result;
    }
}
