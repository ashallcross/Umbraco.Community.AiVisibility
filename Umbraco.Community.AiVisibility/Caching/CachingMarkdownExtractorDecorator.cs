using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace Umbraco.Community.AiVisibility.Caching;

/// <summary>
/// Decorator over <see cref="IMarkdownContentExtractor"/> that caches successful
/// extractions in <see cref="AppCaches.RuntimeCache"/> and registers the cache
/// key against the node's <see cref="System.Guid"/> in <see cref="ICacheKeyIndex"/>
/// so <see cref="ContentCacheRefresherHandler"/> can invalidate by nodeKey on publish.
///
/// <para>
/// <b>Single-flight thundering-herd guarantee.</b>
/// <see cref="IAppPolicyCache.Get(string, Func{object?}, TimeSpan?, bool)"/> serialises
/// the factory invocation per key, so concurrent requests for the same
/// <c>(nodeKey, culture)</c> all park on the cache lock and only one runs the inner
/// extractor (architecture.md § Caching &amp; HTTP).
/// </para>
///
/// <para>
/// <b>Error results are NOT cached.</b> Extraction failures are typically transient
/// (template bug being fixed, transient DB blip). Caching them would mask hot fixes for
/// the configured TTL. The decorator post-checks status and explicitly evicts
/// <see cref="MarkdownExtractionStatus.Error"/> entries.
/// </para>
/// </summary>
internal sealed class CachingMarkdownExtractorDecorator : IMarkdownContentExtractor
{
    private readonly IMarkdownContentExtractor _inner;
    private readonly AppCaches _appCaches;
    private readonly ICacheKeyIndex _index;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly ILogger<CachingMarkdownExtractorDecorator> _logger;

    public CachingMarkdownExtractorDecorator(
        IMarkdownContentExtractor inner,
        AppCaches appCaches,
        ICacheKeyIndex index,
        IHttpContextAccessor httpContextAccessor,
        IOptionsMonitor<AiVisibilitySettings> settings,
        ILogger<CachingMarkdownExtractorDecorator> logger)
    {
        _inner = inner;
        _appCaches = appCaches;
        _index = index;
        _httpContextAccessor = httpContextAccessor;
        _settings = settings;
        _logger = logger;
    }

    public Task<MarkdownExtractionResult> ExtractAsync(
        IPublishedContent content,
        string? culture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var seconds = _settings.CurrentValue.CachePolicySeconds;
        if (seconds <= 0)
        {
            // Cache disabled — bypass the cache entirely. ObjectCacheAppCache rejects
            // TimeSpan.Zero ("AbsoluteExpirationRelativeToNow must be positive"), and a
            // negative seconds value would produce a negative TimeSpan that the cache
            // cannot represent. This gives adopters a single-knob disable without us
            // shipping a separate "caching disabled" flag.
            return _inner.ExtractAsync(content, culture, cancellationToken);
        }

        // Story 1.5: include request host in cache key so multi-domain bindings
        // on the same node never collide (a CDN fronting both hosts could otherwise
        // serve siteA's body to siteB clients). Background scenarios with no ambient
        // HttpContext fall back to the "_" sentinel host via NormaliseHost.
        var host = _httpContextAccessor.HttpContext?.Request.Host.HasValue == true
            ? _httpContextAccessor.HttpContext.Request.Host.Host
            : null;
        var key = AiVisibilityCacheKeys.Page(content.Key, host, culture);
        var ttl = TimeSpan.FromSeconds(seconds);

        // sync-over-async inside the factory: IAppPolicyCache.Get is sync, and Umbraco's
        // ObjectCacheAppCache serialises factory invocations per key — exactly one
        // thread runs the inner extractor per (key, miss). We pass CancellationToken.None
        // INTO the factory so a cancellation from the first arrived caller can't poison
        // every other caller parked on the cache lock; outer cancellation is observed
        // before and after Get instead.
        var result = _appCaches.RuntimeCache.Get(
            key,
            () => _inner
                .ExtractAsync(content, culture, CancellationToken.None)
                .GetAwaiter().GetResult(),
            ttl,
            isSliding: false) as MarkdownExtractionResult;

        cancellationToken.ThrowIfCancellationRequested();

        if (result is null)
        {
            return Task.FromResult(MarkdownExtractionResult.Failed(
                new InvalidOperationException(
                    $"Cache factory returned null for {key}"),
                sourceUrl: null,
                contentKey: content.Key));
        }

        if (result.Status == MarkdownExtractionStatus.Error)
        {
            _appCaches.RuntimeCache.ClearByKey(key);
            return Task.FromResult(result);
        }

        // Success — register in the index for fast invalidation lookup. The handler also
        // prefix-clears by `llms:page:{nodeKey:N}:` so a publish racing this register
        // (handler runs between cache-write and Register) still invalidates the entry —
        // the index is now a hint, not load-bearing for correctness. HashSet semantics
        // make this idempotent; safe to re-register on every cache hit.
        _index.Register(content.Key, key);
        return Task.FromResult(result);
    }
}
