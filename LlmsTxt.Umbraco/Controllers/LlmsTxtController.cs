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
/// Story 2.1 — emits the <c>/llms.txt</c> manifest. Resolves the request hostname
/// to an Umbraco root via <see cref="HostnameRootResolver"/> (
/// <see cref="Umbraco.Cms.Core.Services.IDomainService"/> per AR10), checks the
/// in-memory cache (<c>llms:llmstxt:{host}:{culture}</c>), and on a miss delegates
/// to <see cref="ILlmsTxtBuilder"/> for the body. Headers:
/// <c>Content-Type: text/markdown; charset=utf-8</c>, <c>Cache-Control: public,
/// max-age={LlmsTxtBuilder.CachePolicySeconds}</c>, <c>Vary: Accept</c>. ETag/304
/// hardening is deferred to Story 2.3 — this story does NOT emit an <c>ETag</c>
/// header (issuing one without 304 handling would invite client revalidations the
/// server can't satisfy).
/// </summary>
public sealed class LlmsTxtController : Controller
{
    private readonly ILlmsTxtBuilder _builder;
    private readonly IHostnameRootResolver _hostnameResolver;
    private readonly IUmbracoContextFactory _umbracoContextFactory;
    private readonly IDocumentNavigationQueryService _navigation;
    private readonly AppCaches _appCaches;
    private readonly IOptionsMonitor<LlmsTxtSettings> _settings;
    private readonly ILogger<LlmsTxtController> _logger;

    public LlmsTxtController(
        ILlmsTxtBuilder builder,
        IHostnameRootResolver hostnameResolver,
        IUmbracoContextFactory umbracoContextFactory,
        IDocumentNavigationQueryService navigation,
        AppCaches appCaches,
        IOptionsMonitor<LlmsTxtSettings> settings,
        ILogger<LlmsTxtController> logger)
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

        HostnameRootResolution resolution;
        IReadOnlyList<IPublishedContent> pages;
        using (var ctxRef = _umbracoContextFactory.EnsureUmbracoContext())
        {
            resolution = _hostnameResolver.Resolve(host ?? string.Empty, ctxRef.UmbracoContext);
            pages = resolution.Root is null
                ? Array.Empty<IPublishedContent>()
                : CollectPages(resolution.Root, ctxRef.UmbracoContext.Content);
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

        var settingsSnapshot = _settings.CurrentValue;
        var cacheKey = LlmsCacheKeys.LlmsTxt(host, resolution.Culture);
        var ttl = TimeSpan.FromSeconds(Math.Max(0, settingsSnapshot.LlmsTxtBuilder.CachePolicySeconds));

        // Capture root + culture + pages for the factory closure (cancellation token
        // is per-call; the cached body itself is shared across requests so we must
        // not let one caller's cancellation poison parked callers — pass
        // CancellationToken.None into the inner builder. Same pattern Story 1.2
        // locked for the per-page extractor decorator.)
        var root = resolution.Root;
        var culture = resolution.Culture;
        var hostForBuild = LlmsCacheKeys.NormaliseHost(host);
        var pagesForBuild = pages;

        string? manifest;
        try
        {
            manifest = _appCaches.RuntimeCache.GetCacheItem<string>(
                cacheKey,
                () =>
                {
                    var ctx = new LlmsTxtBuilderContext(
                        Hostname: hostForBuild,
                        Culture: culture,
                        RootContent: root,
                        Pages: pagesForBuild,
                        Settings: settingsSnapshot);
                    // Inner build is sync-over-async by IAppPolicyCache contract.
                    // The builder honours CancellationToken.None inside the cache
                    // factory; per-request cancellation is handled by the outer
                    // WriteAsync (response stream).
                    return _builder.BuildAsync(ctx, CancellationToken.None)
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                },
                timeout: ttl);
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

        if (string.IsNullOrEmpty(manifest))
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
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = Constants.HttpHeaders.MarkdownContentType;

        // HEAD: write headers + status, no body. ASP.NET Core omits body on HEAD
        // automatically when no WriteAsync runs; mirror MarkdownController's pattern.
        if (!HttpMethods.IsHead(HttpContext.Request.Method))
        {
            await response.WriteAsync(manifest, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
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
}
