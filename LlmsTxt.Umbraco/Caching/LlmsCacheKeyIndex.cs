using System.Collections.Concurrent;

namespace LlmsTxt.Umbraco.Caching;

/// <summary>
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>-backed singleton implementation of
/// <see cref="ILlmsCacheKeyIndex"/>. The data shape is locked by architecture.md §
/// Caching &amp; HTTP: <c>ConcurrentDictionary&lt;Guid, HashSet&lt;string&gt;&gt;</c>.
/// <para>
/// <see cref="HashSet{T}"/> itself is not thread-safe; mutations and reads on the inner
/// set hold a <c>lock(set)</c>. <see cref="GetKeysFor"/> snapshots under the same lock
/// so callers iterate without lock contention.
/// </para>
/// </summary>
internal sealed class LlmsCacheKeyIndex : ILlmsCacheKeyIndex
{
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _store = new();

    public void Register(Guid nodeKey, string cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey))
        {
            return;
        }

        var set = _store.GetOrAdd(nodeKey, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set)
        {
            set.Add(cacheKey);
        }
    }

    public IReadOnlyCollection<string> GetKeysFor(Guid nodeKey)
    {
        if (!_store.TryGetValue(nodeKey, out var set))
        {
            return Array.Empty<string>();
        }
        lock (set)
        {
            // Snapshot under the lock so callers can iterate safely afterwards.
            return set.ToArray();
        }
    }

    public void Remove(Guid nodeKey) => _store.TryRemove(nodeKey, out _);

    public void Reset() => _store.Clear();
}
