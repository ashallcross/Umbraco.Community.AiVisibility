using System.Text;
using LlmsTxt.Umbraco.Builders;
using LlmsTxt.Umbraco.Caching;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Notifications;
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
/// Story 2.1 — emits the <c>/llms.txt</c> manifest. Resolves the request hostname
/// to an Umbraco root via <see cref="HostnameRootResolver"/> (
/// <see cref="Umbraco.Cms.Core.Services.IDomainService"/> per AR10), checks the
/// in-memory cache (<c>llms:llmstxt:{host}:{culture}</c>), and on a miss delegates
/// to <see cref="ILlmsTxtBuilder"/> for the body. Headers:
/// <c>Content-Type: text/markdown; charset=utf-8</c>, <c>Cache-Control: public,
/// max-age={LlmsTxtBuilder.CachePolicySeconds}</c>, <c>Vary: Accept</c>.
/// <para>
/// Story 2.3 — emits an <c>ETag</c> header (<see cref="ManifestETag"/>) and
/// honours <c>If-None-Match</c> revalidation by short-circuiting to
/// <c>304 Not Modified</c> via <see cref="IfNoneMatchMatcher"/>. Resolves
/// hreflang variant suffixes when <see cref="HreflangSettings.Enabled"/> is
/// <c>true</c> via <see cref="IHreflangVariantsResolver"/>. Single-flight on
/// cache miss is provided by <see cref="IAppPolicyCache.GetCacheItem"/>'s
/// factory-delegate serialisation per key per instance (Story 1.2 contract;
/// cross-instance pre-warm deferred to v1.1 per architecture § Anti-Scope).
/// </para>
/// </summary>
public sealed class LlmsTxtController : Controller
{
    internal const string ExcludeFromLlmExportsAlias = "excludeFromLlmExports";

    private readonly ILlmsTxtBuilder _builder;
    private readonly IHostnameRootResolver _hostnameResolver;
    private readonly IHreflangVariantsResolver _hreflangResolver;
    private readonly ILlmsSettingsResolver _settingsResolver;
    private readonly IUmbracoContextFactory _umbracoContextFactory;
    private readonly IDocumentNavigationQueryService _navigation;
    private readonly AppCaches _appCaches;
    private readonly IOptionsMonitor<LlmsTxtSettings> _settings;
    private readonly ILlmsNotificationPublisher _notificationPublisher;
    private readonly ILogger<LlmsTxtController> _logger;

    public LlmsTxtController(
        ILlmsTxtBuilder builder,
        IHostnameRootResolver hostnameResolver,
        IHreflangVariantsResolver hreflangResolver,
        ILlmsSettingsResolver settingsResolver,
        IUmbracoContextFactory umbracoContextFactory,
        IDocumentNavigationQueryService navigation,
        AppCaches appCaches,
        IOptionsMonitor<LlmsTxtSettings> settings,
        ILlmsNotificationPublisher notificationPublisher,
        ILogger<LlmsTxtController> logger)
    {
        _builder = builder;
        _hostnameResolver = hostnameResolver;
        _hreflangResolver = hreflangResolver;
        _settingsResolver = settingsResolver;
        _umbracoContextFactory = umbracoContextFactory;
        _navigation = navigation;
        _appCaches = appCaches;
        _settings = settings;
        _notificationPublisher = notificationPublisher;
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
        IReadOnlyList<IPublishedContent> allPages;
        ResolvedLlmsSettings resolvedSettings;
        using (var ctxRef = _umbracoContextFactory.EnsureUmbracoContext())
        {
            resolution = _hostnameResolver.Resolve(host ?? string.Empty, ctxRef.UmbracoContext);
            allPages = resolution.Root is null
                ? Array.Empty<IPublishedContent>()
                : CollectPages(resolution.Root, ctxRef.UmbracoContext.Content);

            // Story 3.1 — resolve effective settings (Settings doctype overlay
            // + appsettings fallback) inside the same EnsureUmbracoContext scope
            // the rest of the controller uses. Resolver-throw graceful
            // degradation: log Warning + fall back to appsettings-only record
            // (matches Story 2.3 hreflang resolver pattern, line 143-156).
            try
            {
                resolvedSettings = await _settingsResolver
                    .ResolveAsync(host, resolution.Culture, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "/llms.txt — ILlmsSettingsResolver threw for {Host} {Culture}; falling back to appsettings",
                    host,
                    resolution.Culture);
                resolvedSettings = BuildAppsettingsFallback(settingsSnapshot);
            }
        }

        if (resolution.Root is null || resolution.Culture is null)
        {
            _logger.LogInformation(
                "/llms.txt — no resolvable root for {Host}; returning 404",
                host);
            return Problem(
                title: "/llms.txt — no resolvable root",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Story 3.1 — filter pages by the resolved exclusion list (doctype
        // alias OR per-page excludeFromLlmExports composition property).
        // Filtering in the controller before the cache factory keeps the
        // builder a pure transform (Builders/ folder boundary).
        var pages = FilterExcludedPages(allPages, resolvedSettings, resolution.Culture);

        var cacheKey = LlmsCacheKeys.LlmsTxt(host, resolution.Culture);

        // Story 6.0a (Codex finding #3) — CachePolicySeconds policy mirrors
        // LlmsFullTxtController: 0 disables the manifest cache entirely
        // (matches LlmsTxtSettings.CachePolicySeconds xmldoc — "0 effectively
        // disables caching"). Negative values are an operator typo — log
        // Warning, treat as 0. The pre-6.0a path always called
        // GetCacheItem with TimeSpan.Zero on disable, which ObjectCacheAppCache
        // rejects/misbehaves on; bypass the cache entirely instead.
        var policySeconds = settingsSnapshot.LlmsTxtBuilder.CachePolicySeconds;
        if (policySeconds < 0)
        {
            _logger.LogWarning(
                "/llms.txt — CachePolicySeconds {Value} is negative for {Host}; treating as 0 (cache disabled)",
                policySeconds,
                host);
            policySeconds = 0;
        }

        // Capture root + culture + pages for the factory closure (cancellation token
        // is per-call; the cached body itself is shared across requests so we must
        // not let one caller's cancellation poison parked callers — pass
        // CancellationToken.None into the inner builder. Same pattern Story 1.2
        // locked for the per-page extractor decorator.)
        // Story 2.3 — single-flight is provided by IAppPolicyCache.GetCacheItem's
        // factory-delegate serialisation per key per instance (Story 1.2 contract).
        var root = resolution.Root;
        var culture = resolution.Culture;
        var hostForBuild = LlmsCacheKeys.NormaliseHost(host);
        var pagesForBuild = pages;
        var resolvedForBuild = resolvedSettings;

        ManifestCacheEntry Build()
        {
            // Code-review P1 (2026-04-30) — hreflang resolution moved
            // INSIDE the cache factory. Previously ran on every request
            // (including cache hits), defeating AC2 single-flight intent
            // for the hreflang work even though the cached body already
            // contained the variants. Now runs exactly once per cache
            // miss, in lock-step with the builder.
            //
            // We open a fresh EnsureUmbracoContext scope here because the
            // outer scope (used for hostname resolution + page collection)
            // is closed by the time the factory runs on a cache miss.
            IReadOnlyDictionary<Guid, IReadOnlyList<HreflangVariant>>? hreflangVariants = null;
            if (settingsSnapshot.Hreflang.Enabled && pagesForBuild.Count > 0)
            {
                using var factoryCtxRef = _umbracoContextFactory.EnsureUmbracoContext();
                try
                {
                    hreflangVariants = _hreflangResolver
                        .ResolveAsync(pagesForBuild, culture, root.Key, factoryCtxRef.UmbracoContext, CancellationToken.None)
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Graceful degradation per spec § Failure & Edge Cases
                    // line 211 — log and proceed with no variants. Note:
                    // the variant-less body IS cached for full TTL; this
                    // residual symptom is captured in deferred-work as a
                    // bounded-by-TTL trade-off (same class as D1).
                    _logger.LogWarning(
                        ex,
                        "/llms.txt — hreflang resolver threw for {Host} {Culture}; emitting manifest without variants",
                        hostForBuild,
                        culture);
                    hreflangVariants = null;
                }
            }

            var ctx = new LlmsTxtBuilderContext(
                Hostname: hostForBuild,
                Culture: culture,
                RootContent: root,
                Pages: pagesForBuild,
                Settings: resolvedForBuild,
                HreflangVariants: hreflangVariants);
            // Inner build is sync-over-async by IAppPolicyCache contract.
            // The builder honours CancellationToken.None inside the cache
            // factory; per-request cancellation is handled by the outer
            // WriteAsync (response stream).
            var body = _builder.BuildAsync(ctx, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            // ETag computed once at cache-write time and reused across
            // hits — Story 2.3 AC6.
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
                "/llms.txt — builder threw for {Host} {Culture}",
                hostForBuild,
                culture);
            return Problem(
                title: "/llms.txt — builder failed",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (entry is null || string.IsNullOrEmpty(entry.Body))
        {
            _logger.LogWarning(
                "/llms.txt — builder returned empty body for {Host} {Culture}",
                hostForBuild,
                culture);
            return Problem(
                title: "/llms.txt — builder returned empty body",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var response = HttpContext.Response;
        VaryHeaderHelper.AppendAccept(HttpContext);
        response.Headers[Constants.HttpHeaders.CacheControl] =
            $"public, max-age={Math.Max(0, settingsSnapshot.LlmsTxtBuilder.CachePolicySeconds)}";
        response.Headers[Constants.HttpHeaders.ETag] = entry.ETag;

        // Story 2.3 AC1 — If-None-Match revalidation. RFC 7232 § 4.1: the 304
        // carries the same Vary/Cache-Control/ETag a 200 would, but NOT
        // Content-Type (representation metadata absent on 304). If-Modified-Since
        // is intentionally ignored — manifests are cache-keyed by (host, culture),
        // not by timestamp; honouring If-Modified-Since would invite stale-content
        // false-positives. Strong validator wins per RFC 7232 § 6.
        if (IfNoneMatchMatcher.Matches(HttpContext.Request, entry.ETag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            response.ContentType = null;
            return new EmptyResult();
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = Constants.HttpHeaders.MarkdownContentType;

        // HEAD: write headers + status, no body. ASP.NET Core omits body on HEAD
        // automatically when no WriteAsync runs; mirror MarkdownController's pattern.
        if (!HttpMethods.IsHead(HttpContext.Request.Method))
        {
            await response.WriteAsync(entry.Body, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        // Story 5.1 — publish LlmsTxtRequestedNotification on the 200 path
        // only. The 304 short-circuit returned earlier; HEAD still emits
        // (the request HIT the route, just without a body — analytics still
        // wants the visibility). The StatusCode == 200 guard mirrors
        // MarkdownController + AcceptHeaderNegotiationMiddleware — defends
        // against a response filter / OnStarting that mutates StatusCode
        // after WriteAsync.
        if (response.StatusCode == StatusCodes.Status200OK)
        {
            await _notificationPublisher.PublishLlmsTxtAsync(
                HttpContext,
                hostname: hostForBuild,
                culture: culture,
                cancellationToken: cancellationToken);
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Walk the published content under <paramref name="root"/> in tree-order
    /// (root first, then descendants) using
    /// <see cref="IDocumentNavigationQueryService.TryGetDescendantsKeys"/> + the
    /// provided <see cref="IPublishedContentCache"/>. Pre-collecting in the
    /// controller keeps <see cref="LlmsTxt.Umbraco.Builders.DefaultLlmsTxtBuilder"/>
    /// pure-function over its inputs (the <c>Builders/</c> folder boundary forbids
    /// HTTP / context dependencies).
    /// </summary>
    private IReadOnlyList<IPublishedContent> CollectPages(
        IPublishedContent root,
        IPublishedContentCache? snapshot)
    {
        var result = new List<IPublishedContent> { root };
        if (snapshot is null)
        {
            return result;
        }

        if (!_navigation.TryGetDescendantsKeys(root.Key, out var descendants))
        {
            return result;
        }

        foreach (var descendantKey in descendants)
        {
            var page = snapshot.GetById(descendantKey);
            if (page is not null)
            {
                result.Add(page);
            }
        }

        return result;
    }

    /// <summary>
    /// Story 3.1 — drop pages whose <c>ContentType.Alias</c> is in the resolved
    /// exclusion list OR whose <c>excludeFromLlmExports</c> composition property
    /// is <c>true</c>. Filtering happens in the controller (before the cache
    /// factory) so the builder stays a pure transform over its input list.
    /// </summary>
    private static IReadOnlyList<IPublishedContent> FilterExcludedPages(
        IReadOnlyList<IPublishedContent> pages,
        ResolvedLlmsSettings resolved,
        string? culture)
    {
        if (pages.Count == 0)
        {
            return pages;
        }

        var excluded = resolved.ExcludedDoctypeAliases;
        if (excluded.Count == 0)
        {
            // No alias-based exclusions — only per-page bool can drop pages.
            // Fast-path: filter only on the bool when no aliases configured.
            return pages
                .Where(p => !TryReadExcludeBool(p, culture))
                .ToList();
        }

        var aliasSet = excluded as HashSet<string>
            ?? new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);

        return pages
            .Where(p => !aliasSet.Contains(p.ContentType.Alias)
                        && !TryReadExcludeBool(p, culture))
            .ToList();
    }

    /// <summary>
    /// Read <c>excludeFromLlmExports</c> via the property layer, mirroring the
    /// shape <see cref="MarkdownController.TryReadExcludeBool"/> uses (and for
    /// the same trap-avoiding reasons documented there).
    /// </summary>
    private static bool TryReadExcludeBool(IPublishedContent page, string? culture)
    {
        // The excludeFromLlmExports property lives on the invariant
        // llmsTxtSettingsComposition. Pass culture: null — see Story 3.1
        // manual gate Step 4 finding (HasValue/GetValue with a non-null culture
        // returns false on invariant properties even when the bool is set).
        _ = culture;
        var prop = page.GetProperty(ExcludeFromLlmExportsAlias);
        if (prop is null || !prop.HasValue(culture: null))
        {
            return false;
        }
        var value = prop.GetValue(culture: null);
        return value is bool b && b;
    }

    /// <summary>
    /// Build a <see cref="ResolvedLlmsSettings"/> from the appsettings snapshot
    /// only — used on the resolver-throw graceful-degradation path. Mirrors
    /// the shape <see cref="DefaultLlmsSettingsResolver.BuildAppsettingsOnly"/>
    /// produces; pure duplication is acceptable here because the
    /// <c>Configuration/</c> resolver method is private and the
    /// <c>Controllers/</c> folder can't call into it without leaking the
    /// private surface.
    /// </summary>
    private static ResolvedLlmsSettings BuildAppsettingsFallback(LlmsTxtSettings settings)
    {
        var excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in settings.ExcludedDoctypeAliases ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                excludedSet.Add(alias.Trim());
            }
        }
        return new ResolvedLlmsSettings(
            SiteName: settings.SiteName,
            SiteSummary: settings.SiteSummary,
            ExcludedDoctypeAliases: excludedSet,
            BaseSettings: settings);
    }
}
