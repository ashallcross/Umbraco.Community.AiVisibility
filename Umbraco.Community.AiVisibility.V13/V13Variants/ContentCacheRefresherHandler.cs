using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Sync;

namespace Umbraco.Community.AiVisibility.Caching;

/// <summary>
/// v13 copy-with-edit variant of the v17 <c>ContentCacheRefresherHandler</c>
/// (Story 9.2 hybrid shell strategy — see Spec Drift SDN-1).
///
/// <para>
/// <b>Why a v13 variant exists:</b> the v17 handler walks branch descendants via
/// <c>IDocumentNavigationQueryService.TryGetDescendantsKeys</c>, which does NOT
/// exist on Umbraco v13. Rather than carry an <c>#if</c> in the shell (AC9), this
/// net8-only copy drops the navigation dependency and makes branch invalidation
/// <b>pessimistic</b>: a branch refresh clears the entire <c>llms:</c> cache
/// namespace. This is correctness-safe (never leaves a stale descendant entry) and
/// matches the handler's existing pessimistic philosophy for manifests
/// (<c>ClearAllManifestNamespaces</c> already over-clears every host on any node
/// change). The only cost is rebuilding more per-page cache than strictly necessary
/// on the relatively rare branch-refresh (move / sort / bulk-publish) — a
/// performance trade, not an output difference. Documented as a v13-vs-v17 cache
/// granularity difference (v13 README, Story 9.4).
/// </para>
///
/// <para>
/// Everything else mirrors the v17 handler verbatim: <see cref="ContentCacheRefresherNotification"/>
/// is the load-balancer-safe broadcast signal (we avoid
/// <see cref="ContentPublishedNotification"/> per architecture, Umbraco-CMS#20539);
/// per-payload exceptions are caught and logged; malformed payloads skip rather than
/// <c>ClearAll</c>.
/// </para>
/// </summary>
internal sealed class ContentCacheRefresherHandler
    : INotificationAsyncHandler<ContentCacheRefresherNotification>
{
    private readonly AppCaches _appCaches;
    private readonly ICacheKeyIndex _index;
    private readonly ILogger<ContentCacheRefresherHandler> _logger;

    public ContentCacheRefresherHandler(
        AppCaches appCaches,
        ICacheKeyIndex index,
        ILogger<ContentCacheRefresherHandler> logger)
    {
        _appCaches = appCaches;
        _index = index;
        _logger = logger;
    }

    public Task HandleAsync(
        ContentCacheRefresherNotification notification,
        CancellationToken cancellationToken)
    {
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
                _logger.LogError(
                    ex,
                    "Cache invalidation failed for {NodeKey} {ChangeTypes}",
                    payload.Key,
                    payload.ChangeTypes);
            }
        }

        // Pessimistic manifest invalidation — any per-node payload may change the
        // manifest output for any hostname mapping onto the same content tree.
        ClearAllManifestNamespaces();

        return Task.CompletedTask;
    }

    private void HandlePayload(ContentCacheRefresher.JsonPayload payload)
    {
        if (payload.ChangeTypes.HasFlag(TreeChangeTypes.RefreshAll))
        {
            ClearAll();
            return;
        }

        if (payload.Key is not Guid nodeKey)
        {
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
    }

    private void ClearAll()
    {
        _appCaches.RuntimeCache.ClearByKey(AiVisibilityCacheKeys.Prefix);
        _index.Reset();
    }

    private void ClearNode(Guid nodeKey, bool removeIndexEntry)
    {
        _appCaches.RuntimeCache.ClearByKey($"{AiVisibilityCacheKeys.PagePrefix}{nodeKey:N}:");
        if (removeIndexEntry)
        {
            _index.Remove(nodeKey);
        }
    }

    private void ClearAllManifestNamespaces()
    {
        try
        {
            _appCaches.RuntimeCache.ClearByKey(AiVisibilityCacheKeys.LlmsTxtPrefix);
            _appCaches.RuntimeCache.ClearByKey(AiVisibilityCacheKeys.LlmsFullPrefix);
            _appCaches.RuntimeCache.ClearByKey(AiVisibilityCacheKeys.SettingsPrefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manifest / settings cache prefix-clear failed");
        }
    }

    /// <summary>
    /// v13 branch invalidation. v17 walks descendant keys via the navigation
    /// query service; v13 has no such service, so this clears the entire
    /// <c>llms:</c> namespace pessimistically. Correctness-safe (no stale
    /// descendant can survive); over-clears on the rare branch refresh.
    /// </summary>
    private void ClearBranch(Guid rootKey)
    {
        _logger.LogDebug(
            "v13 branch refresh — pessimistic full cache clear for branch root {RootKey} "
            + "(no IDocumentNavigationQueryService on v13; see ContentCacheRefresherHandler v13 variant)",
            rootKey);
        ClearAll();
    }
}
