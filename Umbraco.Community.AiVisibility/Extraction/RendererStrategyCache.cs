using System.Collections.Concurrent;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.3 — default <see cref="IRendererStrategyCache"/> implementation.
/// Backed by a thread-safe
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by the
/// <c>(ContentTypeAlias, TemplateAlias)</c> tuple.
/// </summary>
/// <remarks>
/// <para>
/// <b>ConcurrentDictionary, not <c>IAppPolicyCache</c>.</b> The cache is a
/// single-purpose decision store with no TTL / no eviction policy; the
/// dictionary's built-in lock-free reads + striped-lock writes are exactly
/// the right tool. <c>IAppPolicyCache</c> would carry per-key TTL machinery
/// that's actively unwanted here (the entries are valid for the life of the
/// process; there's no freshness signal).
/// </para>
/// <para>
/// <b>Process-lifetime, no notification hook.</b> See
/// <see cref="IRendererStrategyCache"/> remarks. Adopters who remove a hijack
/// and want to verify Razor-path performance restart the host. The cache size
/// is bounded by the count of <c>(doctype × hijacked template)</c>
/// combinations on the site — typically tens, never thousands; not a
/// memory-leak risk.
/// </para>
/// </remarks>
internal sealed class RendererStrategyCache : IRendererStrategyCache
{
    private readonly ConcurrentDictionary<(string ContentTypeAlias, string TemplateAlias), bool> _cache = new();

    public bool IsHijacked(string contentTypeAlias, string templateAlias)
        => _cache.TryGetValue((contentTypeAlias, templateAlias), out _);

    public bool MarkHijacked(string contentTypeAlias, string templateAlias)
        => _cache.TryAdd((contentTypeAlias, templateAlias), true);
}
