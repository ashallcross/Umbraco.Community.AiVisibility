using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace Umbraco.Community.AiVisibility.Caching;

/// <summary>
/// Clears the <c>/llms.txt</c>, <c>/llms-full.txt</c> and resolved-settings caches
/// when Umbraco Public Access rules change, so a page that gains or loses Public
/// Access drops out of (or reappears in) the manifests on the next request.
///
/// <para>
/// <b>Why this notification.</b> Saving or removing a Public Access rule publishes
/// nothing, so <see cref="ContentCacheRefresherNotification"/> never fires for it.
/// <see cref="PublicAccessCacheRefresherNotification"/> is raised by Umbraco's
/// <c>PublicAccessCacheRefresher</c>, which is a distributed cache refresher: it
/// runs on every node of a load-balanced setup (including frontend nodes when the
/// change is made on a separate backoffice node), so each node clears its own
/// in-memory manifests.
/// </para>
///
/// <para>
/// <b>Why the per-page <c>.md</c> cache is left alone.</b> The <c>.md</c> route
/// evaluates exclusion (including Public Access) live on every request before it
/// reads the per-page cache, so a cached body for a now-protected page is never
/// served. Only the manifests bake exclusion into a cached body.
/// </para>
///
/// <para>
/// <b>Failure-mode-safe.</b> A failing prefix clear is logged at <c>Error</c> and
/// swallowed so it never breaks Umbraco's cache-refresher broadcast.
/// </para>
/// </summary>
internal sealed class PublicAccessCacheRefresherHandler
    : INotificationAsyncHandler<PublicAccessCacheRefresherNotification>
{
    private readonly AppCaches _appCaches;
    private readonly ILogger<PublicAccessCacheRefresherHandler> _logger;

    public PublicAccessCacheRefresherHandler(
        AppCaches appCaches,
        ILogger<PublicAccessCacheRefresherHandler> logger)
    {
        _appCaches = appCaches;
        _logger = logger;
    }

    public Task HandleAsync(
        PublicAccessCacheRefresherNotification notification,
        CancellationToken cancellationToken)
    {
        // Every Public Access message type (Refresh by id/key, RefreshAll, Remove)
        // can change which pages the manifests list, so all of them clear the
        // same three namespaces — mirrors ContentCacheRefresherHandler's
        // ClearAllManifestNamespaces.
        try
        {
            _appCaches.RuntimeCache.ClearByKey(AiVisibilityCacheKeys.LlmsTxtPrefix);
            _appCaches.RuntimeCache.ClearByKey(AiVisibilityCacheKeys.LlmsFullPrefix);
            _appCaches.RuntimeCache.ClearByKey(AiVisibilityCacheKeys.SettingsPrefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Manifest / settings cache prefix-clear failed after a Public Access change ({MessageType})",
                notification.MessageType);
        }

        return Task.CompletedTask;
    }
}
