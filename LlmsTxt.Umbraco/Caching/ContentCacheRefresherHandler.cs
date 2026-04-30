using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
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
    private readonly IDomainService _domainService;
    private readonly ILogger<ContentCacheRefresherHandler> _logger;

    public ContentCacheRefresherHandler(
        AppCaches appCaches,
        ILlmsCacheKeyIndex index,
        IDocumentNavigationQueryService navigation,
        IDomainService domainService,
        ILogger<ContentCacheRefresherHandler> logger)
    {
        _appCaches = appCaches;
        _index = index;
        _navigation = navigation;
        _domainService = domainService;
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
        ClearManifestsForBoundHostnames();

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

    private void ClearManifestsForBoundHostnames()
    {
        try
        {
            var hostnames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var domain in _domainService.GetAll(includeWildcards: true))
            {
                var raw = domain.DomainName;
                if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('/'))
                {
                    // Culture-only "/en/" style binding — never carries a hostname.
                    continue;
                }

                // Strip optional scheme then normalise (lowercase, port-stripped).
                var schemeIdx = raw.IndexOf("://", StringComparison.Ordinal);
                var hostPart = schemeIdx < 0 ? raw : raw[(schemeIdx + 3)..];
                if (hostPart.StartsWith("*.", StringComparison.Ordinal))
                {
                    // Wildcard binding has no concrete request host to invalidate by;
                    // matching subdomains will cache against their own host segment.
                    continue;
                }

                var normalised = LlmsCacheKeys.NormaliseHost(hostPart);
                if (normalised != "_")
                {
                    hostnames.Add(normalised);
                }
            }

            foreach (var host in hostnames)
            {
                _appCaches.RuntimeCache.ClearByKey(LlmsCacheKeys.LlmsTxtHostPrefix(host));
            }
        }
        catch (Exception ex)
        {
            // Defensive — IDomainService access shouldn't throw, but if it does the
            // per-page loop has already done its job; don't poison the broadcast.
            _logger.LogError(ex, "Manifest cache invalidation failed during IDomainService walk");
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
