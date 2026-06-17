using System.Collections.Concurrent;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Default <see cref="IRendererStrategyCache"/> implementation. Backed by a
/// thread-safe <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by the
/// <c>(ContentTypeAlias, TemplateAlias)</c> tuple, with the value indicating
/// which cached decision applies — hijacked (try Loopback) or permanently
/// failed (skip both Razor and Loopback).
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
/// is bounded by the count of <c>(doctype × template)</c> combinations on the
/// site — typically tens, never thousands; not a memory-leak risk.
/// </para>
/// <para>
/// <b>Single dictionary, decision enum value.</b> Storing both decision types
/// in a single <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by tuple
/// preserves the invariant that a tuple has exactly one cached decision —
/// the first decision wins. The implementation never transitions between
/// states; a tuple cached as Hijacked stays hijacked, a tuple cached as
/// RazorPermanentlyFailed stays permanently failed. Process restart is the
/// only path back to "not cached" (matches the documented invalidation model).
/// </para>
/// </remarks>
internal sealed class RendererStrategyCache : IRendererStrategyCache
{
    private enum Decision
    {
        Hijacked,
        RazorPermanentlyFailed,
    }

    private readonly ConcurrentDictionary<(string ContentTypeAlias, string TemplateAlias), Decision> _cache = new();

    public bool IsHijacked(string contentTypeAlias, string templateAlias)
        => _cache.TryGetValue((contentTypeAlias, templateAlias), out var decision)
           && decision == Decision.Hijacked;

    public bool MarkHijacked(string contentTypeAlias, string templateAlias)
        => _cache.TryAdd((contentTypeAlias, templateAlias), Decision.Hijacked);

    public bool IsRazorPermanentlyFailed(string contentTypeAlias, string templateAlias)
        => _cache.TryGetValue((contentTypeAlias, templateAlias), out var decision)
           && decision == Decision.RazorPermanentlyFailed;

    public bool MarkRazorPermanentlyFailed(string contentTypeAlias, string templateAlias)
        => _cache.TryAdd((contentTypeAlias, templateAlias), Decision.RazorPermanentlyFailed);
}
