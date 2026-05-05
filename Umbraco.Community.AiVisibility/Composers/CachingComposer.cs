using System.Linq;
using Umbraco.Community.AiVisibility.Caching;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Extensions;

namespace LlmsTxt.Umbraco.Composers;

/// <summary>
/// Wires Story 1.2's caching layer:
/// <list type="bullet">
/// <item>Singleton <see cref="ICacheKeyIndex"/> backing the publish-driven
/// invalidation lookup.</item>
/// <item><see cref="ContentCacheRefresherHandler"/> as
/// <see cref="INotificationAsyncHandler{T}"/> for
/// <see cref="ContentCacheRefresherNotification"/>.</item>
/// <item>Decorates <see cref="IMarkdownContentExtractor"/> with
/// <see cref="CachingMarkdownExtractorDecorator"/> only when the current registration is
/// our <see cref="DefaultMarkdownContentExtractor"/> — adopter overrides win.</item>
/// </list>
///
/// <para>
/// Composes after <see cref="RoutingComposer"/> because we need the existing
/// <see cref="IMarkdownContentExtractor"/> registration to be present before we can
/// inspect-and-decorate it.
/// </para>
/// </summary>
[ComposeAfter(typeof(RoutingComposer))]
public sealed class CachingComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.TryAddSingleton<ICacheKeyIndex, CacheKeyIndex>();

        builder.AddNotificationAsyncHandler<
            ContentCacheRefresherNotification,
            ContentCacheRefresherHandler>();

        if (IsAdopterOverride(builder.Services))
        {
            // Don't wrap an adopter's implementation transparently; log once at startup
            // so the bypass is observable to adopters debugging Backoffice cache state.
            builder.Components().Append<AdopterExtractorOverrideComponent>();
            return;
        }

        DecorateDefaultExtractor(builder.Services);
    }

    /// <summary>
    /// Returns <c>true</c> when an adopter has registered a non-default
    /// <see cref="IMarkdownContentExtractor"/> implementation before this composer
    /// runs. Returns <c>false</c> when no registration exists yet (RoutingComposer
    /// hasn't run) or the registration is our <see cref="DefaultMarkdownContentExtractor"/>.
    /// </summary>
    internal static bool IsAdopterOverride(IServiceCollection services)
    {
        var registration = services.LastOrDefault(
            d => d.ServiceType == typeof(IMarkdownContentExtractor));

        if (registration is null)
        {
            return false;
        }

        return registration.ImplementationType != typeof(DefaultMarkdownContentExtractor);
    }

    /// <summary>
    /// Replace the default <see cref="IMarkdownContentExtractor"/> registration with
    /// a factory that constructs the default extractor and wraps it in
    /// <see cref="CachingMarkdownExtractorDecorator"/>.
    /// </summary>
    internal static void DecorateDefaultExtractor(IServiceCollection services)
    {
        var registration = services.LastOrDefault(
            d => d.ServiceType == typeof(IMarkdownContentExtractor));

        if (registration is null)
        {
            return; // RoutingComposer hasn't run — nothing to decorate.
        }

        services.Remove(registration);
        services.TryAddTransient<DefaultMarkdownContentExtractor>();
        services.AddTransient<IMarkdownContentExtractor>(sp =>
            new CachingMarkdownExtractorDecorator(
                sp.GetRequiredService<DefaultMarkdownContentExtractor>(),
                sp.GetRequiredService<AppCaches>(),
                sp.GetRequiredService<ICacheKeyIndex>(),
                sp.GetRequiredService<IHttpContextAccessor>(),
                sp.GetRequiredService<IOptionsMonitor<LlmsTxtSettings>>(),
                sp.GetRequiredService<ILogger<CachingMarkdownExtractorDecorator>>()));
    }
}

/// <summary>
/// Logs once at startup when an adopter has registered their own
/// <see cref="IMarkdownContentExtractor"/> before <see cref="CachingComposer"/> ran,
/// so the caching decorator was deliberately not wrapped around it. Surfaces the
/// bypass for adopters debugging cache state.
/// </summary>
internal sealed class AdopterExtractorOverrideComponent : IAsyncComponent
{
    private readonly ILogger<CachingComposer> _logger;

    public AdopterExtractorOverrideComponent(ILogger<CachingComposer> logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(bool isRestart, CancellationToken cancellationToken)
    {
        if (!isRestart)
        {
            _logger.LogInformation(
                "Adopter has overridden IMarkdownContentExtractor; skipping caching decorator wrap");
        }
        return Task.CompletedTask;
    }

    public Task TerminateAsync(bool isRestart, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
