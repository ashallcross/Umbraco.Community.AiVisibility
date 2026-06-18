using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Community.AiVisibility.Composing;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Persistence;
using Umbraco.Community.AiVisibility.Robots;
using Umbraco.Community.AiVisibility.V13.Jobs;

namespace Umbraco.Community.AiVisibility.V13.Tests.Composing;

[TestFixture]
public sealed class V13StartupValidationTests
{
    [Test]
    public void Compose_StartupValidation_LogRetentionJob_NoCaptiveDependency()
    {
        var services = Compose(new NotificationsComposer());
        StubSharedDependencies(services);
        services.AddSingleton(_ => Substitute.For<IScopeProvider>());
        services.AddSingleton(_ => Substitute.For<IEventAggregator>());

        Assert.DoesNotThrow(() =>
        {
            using var provider = BuildValidatingProvider(services);
            var jobs = provider.GetServices<IRecurringBackgroundJob>().ToArray();
            Assert.That(jobs.Any(j => j is LogRetentionJob), Is.True);
            Assert.That(provider.GetRequiredService<IRequestLog>(), Is.Not.Null);
        });
    }

    [Test]
    public void Compose_StartupValidation_RobotsAuditRefreshJob_NoCaptiveDependency()
    {
        var services = Compose(new RobotsComposer());
        StubSharedDependencies(services);
        services.AddSingleton(_ => Substitute.For<IHttpClientFactory>());
        services.AddScoped(_ => Substitute.For<IDomainService>());
        services.AddSingleton(_ => new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache())));

        Assert.DoesNotThrow(() =>
        {
            using var provider = BuildValidatingProvider(services);
            var jobs = provider.GetServices<IRecurringBackgroundJob>().ToArray();
            Assert.That(jobs.Any(j => j is RobotsAuditRefreshJob), Is.True);
            Assert.That(provider.GetRequiredService<IRobotsAuditor>(), Is.Not.Null);
        });
    }

    [Test]
    public void Compose_StartupValidation_RobotsAuditHealthCheck_NoCaptiveDependency()
    {
        var services = Compose(new RobotsComposer());
        StubSharedDependencies(services);
        services.AddSingleton(_ => Substitute.For<IHttpClientFactory>());
        services.AddScoped(_ => Substitute.For<IDomainService>());
        services.AddSingleton(_ => new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache())));

        Assert.DoesNotThrow(() =>
        {
            using var provider = BuildValidatingProvider(services);
            using var scope = provider.CreateScope();
            Assert.That(scope.ServiceProvider.GetRequiredService<RobotsAuditHealthCheck>(), Is.Not.Null);
        });
    }

    private static IServiceCollection Compose(IComposer composer)
    {
        var services = new ServiceCollection();
        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        builder.Config.Returns(new ConfigurationBuilder().Build());
        composer.Compose(builder);
        return services;
    }

    private static ServiceProvider BuildValidatingProvider(IServiceCollection services)
        => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

    private static void StubSharedDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => Substitute.For<IServerRoleAccessor>());
        services.AddSingleton(_ => Substitute.For<IHttpContextAccessor>());
        services.AddSingleton(_ =>
        {
            var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
            monitor.CurrentValue.Returns(new AiVisibilitySettings());
            return monitor;
        });
    }
}
