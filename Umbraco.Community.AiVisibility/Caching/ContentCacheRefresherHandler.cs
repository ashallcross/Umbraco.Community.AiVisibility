using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Sync;

namespace LlmsTxt.Umbraco.Caching;

/// <summary>
/// Drives Story 1.2's per-page cache invalidation off Umbraco's distributed cache
/// refresher.
///
/// <para>
/// <see cref="ContentCacheRefresherNotification"/> is the load-balancer-safe broadcast
/// signal — it fires on every instance independently when content publishes, moves, or
/// unpublishes. We avoid <see cref="ContentPublishedNotification"/> per architecture
/// (Umbraco-CMS#20539 stale-content bug).
/// </para>
///
/// <para>
/// <b>Failure-mode-safe.</b> Per-payload exceptions are caught and logged so the rest of
/// a broadcast's payloads still get processed. Malformed <c>MessageObject</c>
/// (deserialisation failure) logs <c>Error</c> and skips — never <c>ClearAll</c>, which
/// would punish the whole instance for one upstream tooling bug.
/// </para>
/// </summary>
internal sealed class ContentCacheRefresherHandler
    : INotificationAsyncHandler<ContentCacheRefresherNotification>
{
    private readonly AppCaches _appCaches;
    private readonly ILlmsCacheKeyIndex _index;
    private readonly IDocumentNavigationQueryService _navigation;
    private readonly ILogger<ContentCacheRefresherHandler> _logger;

    public ContentCacheRefresherHandler(
        AppCaches appCaches,
        ILlmsCacheKeyIndex index,
        IDocumentNavigationQueryService navigation,
        ILogger<ContentCacheRefresherHandler> logger)
    {
        // Story 3.1 — IDomainService dependency dropped. Manifest invalidation
        // now uses a single per-namespace prefix-clear instead of walking
        // bound hostnames (see ClearManifestsForBoundHostnames for the
        // rationale).
        _appCaches = appCaches;
        _index = index;
        _navigation = navigation;
        _logger = logger;
    }

    public Task HandleAsync(
        ContentCacheRefresherNotification notification,
        CancellationToken cancellationToken)
    {
        // RefreshAll arrives with MessageType=RefreshAll and no payload object.
        // ClearAll already drops `llms:` prefix which includes both per-page AND
        // manifest entries, so RefreshAll needs no extra manifest handling.
        if (notification.MessageType == MessageType.RefreshAll)
        {
            ClearAll();
            return Task.CompletedTask;
        }

        if (notification.MessageObject is not ContentCacheRefresher.JsonPayload[] payloads)
        {
            _logger.LogError(
                "ContentCacheRefresherNotification payload not deserialisable as JsonPayload[] — type was {MessageObjectType}",
                notification.MessageObject?.GetType().FullName ?? "<null>");
            return Task.CompletedTask;
        }

        foreach (var payload in payloads)
        {
            try
            {
                HandlePayload(payload);
            }
            catch (Exception ex)
            {
                // Per-payload exceptions never halt the loop — the rest of the broadcast
                // still has invalidations to perform.
                _logger.LogError(
                    ex,
                    "Cache invalidation failed for {NodeKey} {ChangeTypes}",
                    payload.Key,
                    payload.ChangeTypes);
            }
        }

        // Story 2.1 — pessimistic manifest invalidation. Any per-node payload may
        // change the manifest output for any hostname that maps onto the same
        // content tree, so we drop EVERY hostname's manifest cache after the
        // per-node loop. Trade-off: extra clears on multi-site setups where the
        // changed node only belongs to one site; cheaper than mapping nodes to
        // hosts at invalidation time, and manifests are cheap to rebuild
        // (architecture line 320).
        ClearAllManifestNamespaces();

        return Task.CompletedTask;
    }

    private void HandlePayload(ContentCacheRefresher.JsonPayload payload)
    {
        // RefreshAll inside a payload (rare but possible) — pessimistic clear.
        if (payload.ChangeTypes.HasFlag(TreeChangeTypes.RefreshAll))
        {
            ClearAll();
            return;
        }

        if (payload.Key is not Guid nodeKey)
        {
            // No Guid → nothing to look up in the index. Skip silently.
            return;
        }

        if (payload.ChangeTypes.HasFlag(TreeChangeTypes.RefreshBranch))
        {
            ClearBranch(nodeKey);
            return;
        }

        if (payload.ChangeTypes.HasFlag(TreeChangeTypes.Remove)
            || payload.ChangeTypes.HasFlag(TreeChangeTypes.RefreshNode))
        {
            ClearNode(nodeKey, removeIndexEntry: true);
        }

        // RefreshOther / no flags → no-op for our cache.
    }

    private void ClearAll()
    {
        _appCaches.RuntimeCache.ClearByKey(LlmsCacheKeys.Prefix);
        _index.Reset();
    }

    private void ClearNode(Guid nodeKey, bool removeIndexEntry)
    {
        // Prefix-clear by `llms:page:{nodeKey:N}:` so cache entries that race-condition
        // through the decorator's "cache-write → Register" gap are still invalidated —
        // the index is a fast-path hint, not a source of truth for which entries exist.
        // ObjectCacheAppCache.ClearByKey on a missing prefix is a no-op.
        _appCaches.RuntimeCache.ClearByKey($"{LlmsCacheKeys.PagePrefix}{nodeKey:N}:");
        if (removeIndexEntry)
        {
            _index.Remove(nodeKey);
        }
    }

    /// <summary>
    /// Story 3.1 — pessimistic prefix-clear of every manifest / settings
    /// namespace in one call. Renamed from <c>ClearManifestsForBoundHostnames</c>
    /// (code review 2026-04-30) — the method no longer iterates over bound
    /// hostnames, it clears the full <c>llms:llmstxt:</c> /
    /// <c>llms:llmsfull:</c> / <c>llms:settings:</c> namespaces.
    /// </summary>
    private void ClearAllManifestNamespaces()
    {
        // Replaces the original Story 2.1 per-IDomain-walk approach which only
        // cleared hosts that were bound as Umbraco IDomains, leaving the
        // request-cached entries for unbound hosts (e.g. localhost dev,
        // reverse-proxy internal hostnames, alias hosts pointing at the same
        // site) stale until TTL.
        // <para>
        // Surfaced at Story 3.1 manual gate Step 4 — editor publish on a
        // TestSite reachable via localhost did not invalidate the localhost
        // manifest cache because the IDomainService had no localhost binding.
        // The per-host walk left the editor needing a TestSite restart for
        // changes to take effect, which fails the AC5 "publish-then-fresh"
        // editor-experience contract.
        // </para>
        // <para>
        // Trade-off: a single per-node refresh now clears every hostname's
        // manifest cache rather than just the affected one. On multi-tenant
        // setups that's slightly more rebuild work, but manifests are cheap
        // (architecture line 320) and the rebuild is a single-flight against
        // the published cache. The previous "per-host precision" claim was
        // mostly cosmetic anyway because the per-page cache (`llms:page:`)
        // already prefix-clears by node key (NOT by host) — the host
        // segmentation only ever bought us "don't rebuild the manifest for
        // unaffected sites", which is sub-millisecond saved work.
        // </para>
        try
        {
            _appCaches.RuntimeCache.ClearByKey(LlmsCacheKeys.LlmsTxtPrefix);
            _appCaches.RuntimeCache.ClearByKey(LlmsCacheKeys.LlmsFullPrefix);
            _appCaches.RuntimeCache.ClearByKey(LlmsCacheKeys.SettingsPrefix);
        }
        catch (Exception ex)
        {
            // Defensive — should never throw, but the per-page loop has
            // already done its job; don't poison the broadcast.
            _logger.LogError(ex, "Manifest / settings cache prefix-clear failed");
        }
    }

    private void ClearBranch(Guid rootKey)
    {
        // Always clear the root first — even when navigation can't find descendants,
        // the root itself was cached and must be invalidated.
        ClearNode(rootKey, removeIndexEntry: true);

        if (!_navigation.TryGetDescendantsKeys(rootKey, out var descendants))
        {
            _logger.LogWarning(
                "Branch refresh — root not in navigation, clearing root only {RootKey}",
                rootKey);
            return;
        }

        foreach (var descendantKey in descendants)
        {
            ClearNode(descendantKey, removeIndexEntry: true);
        }
    }
}
