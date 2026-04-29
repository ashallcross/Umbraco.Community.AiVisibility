namespace LlmsTxt.Umbraco.Caching;

/// <summary>
/// Singleton, thread-safe map from <see cref="System.Guid"/> nodeKey to the
/// set of <see cref="Umbraco.Cms.Core.Cache.IAppPolicyCache"/> entries cached
/// for that node. Populated by <see cref="CachingMarkdownExtractorDecorator"/>
/// on cache-miss writes; consumed by <see cref="ContentCacheRefresherHandler"/>
/// on invalidation.
///
/// <para>
/// Per-instance in-memory — Umbraco's distributed cache refresher fans the
/// invalidation notification to every instance independently, so each
/// instance maintains its own index and clears its own cache (architecture.md
/// § Caching &amp; HTTP).
/// </para>
/// </summary>
public interface ILlmsCacheKeyIndex
{
    /// <summary>
    /// Register a <paramref name="cacheKey"/> against a <paramref name="nodeKey"/>.
    /// Idempotent — re-registering the same pair is a no-op (HashSet semantics).
    /// </summary>
    void Register(Guid nodeKey, string cacheKey);

    /// <summary>
    /// Snapshot the cache keys currently registered for <paramref name="nodeKey"/>.
    /// Returns an empty collection when the node is unknown — callers iterate
    /// without holding any lock.
    /// </summary>
    IReadOnlyCollection<string> GetKeysFor(Guid nodeKey);

    /// <summary>
    /// Remove the entry for <paramref name="nodeKey"/>. No-op when unknown.
    /// </summary>
    void Remove(Guid nodeKey);

    /// <summary>
    /// Clear the entire index. Used on <c>RefreshAll</c> notifications.
    /// </summary>
    void Reset();
}
