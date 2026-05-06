using LlmsTxt.Umbraco.Background;
using LlmsTxt.Umbraco.Composers;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Robots;
using LlmsTxt.Umbraco.Notifications;
using Umbraco.Community.AiVisibility.Persistence;
using Umbraco.Community.AiVisibility.Persistence.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.Scoping;

namespace LlmsTxt.Umbraco.Tests.Composers;

[TestFixture]
public class NotificationsComposerTests
{
    [Test]
    public void Compose_RegistersIRequestLog_AsSingleton()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        var descriptor = services.Single(d => d.ServiceType == typeof(IRequestLog));
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Lifetime, Is.EqualTo(ServiceLifetime.Singleton));
            Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(DefaultRequestLog)));
        });
    }

    [Test]
    public void Compose_RegistersIUserAgentClassifier_AsSingleton()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        var descriptor = services.Single(d => d.ServiceType == typeof(IUserAgentClassifier));
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Lifetime, Is.EqualTo(ServiceLifetime.Singleton));
            Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(DefaultUserAgentClassifier)));
        });
    }

    [Test]
    public void Compose_RegistersLogRetentionJob_AsIDistributedBackgroundJob()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        var jobs = services
            .Where(d => d.ServiceType == typeof(IDistributedBackgroundJob))
            .ToArray();

        Assert.That(
            jobs.Any(d => d.ImplementationType == typeof(LogRetentionJob)
                          && d.Lifetime == ServiceLifetime.Singleton),
            Is.True,
            "LogRetentionJob must be registered as Singleton<IDistributedBackgroundJob>");
    }

    [Test]
    public void Compose_RegistersDrainerAsHostedService()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        var hostedDescriptors = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .ToArray();

        Assert.That(
            hostedDescriptors.Any(d => d.ImplementationType == typeof(LlmsRequestLogDrainHostedService)),
            Is.True,
            "LlmsRequestLogDrainHostedService must be registered via AddHostedService");
    }

    [Test]
    public void AdopterOverride_PreRegisteredAsSingleton_DefaultDoesNotReplace()
    {
        // AC6 happy path — adopter's Singleton override wins; TryAddSingleton no-ops.
        var (composer, builder, services) = BuildComposer();
        services.AddSingleton<IRequestLog, NoOpAdopterLog>();

        composer.Compose(builder);

        var registrations = services
            .Where(d => d.ServiceType == typeof(IRequestLog))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(registrations, Has.Length.EqualTo(1));
            Assert.That(registrations[0].ImplementationType, Is.EqualTo(typeof(NoOpAdopterLog)));
        });
    }

    [Test]
    public void Compose_AdopterScopedOverride_ThrowsAtComposerTime()
    {
        // AC6 — composer-time hard-validation. Adopter Scoped registration
        // forms captive dep in the Singleton drainer chain.
        var (composer, builder, services) = BuildComposer();
        services.AddScoped<IRequestLog, NoOpAdopterLog>();

        var ex = Assert.Throws<InvalidOperationException>(() => composer.Compose(builder));
        Assert.That(ex!.Message, Does.Contain("must be registered as Singleton"));
    }

    [Test]
    public void Compose_AdopterTransientOverride_ThrowsAtComposerTime()
    {
        var (composer, builder, services) = BuildComposer();
        services.AddTransient<IRequestLog, NoOpAdopterLog>();

        var ex = Assert.Throws<InvalidOperationException>(() => composer.Compose(builder));
        Assert.That(ex!.Message, Does.Contain("must be registered as Singleton"));
    }

    [Test]
    public void Compose_StartupValidation_NotificationsComposer_NoCaptiveDependency()
    {
        // Canonical DI gate. Stub the dependencies the package types pull
        // (IScopeProvider, IServerRoleAccessor, AiBotList's logger, etc.)
        // and resolve through ValidateScopes + ValidateOnBuild.
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        StubDependencies(services);

        Assert.DoesNotThrow(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });

            // IRequestLog is Singleton — resolve from root.
            var log = provider.GetRequiredService<IRequestLog>();
            Assert.That(log, Is.Not.Null);

            // IUserAgentClassifier is Singleton — same.
            var classifier = provider.GetRequiredService<IUserAgentClassifier>();
            Assert.That(classifier, Is.Not.Null);

            // The IDistributedBackgroundJob registration must resolve cleanly.
            var jobs = provider.GetServices<IDistributedBackgroundJob>().ToArray();
            Assert.That(jobs.Any(j => j is LogRetentionJob), Is.True);
        }, "ValidateScopes + ValidateOnBuild must succeed for the Story 5.1 graph");
    }

    private static (NotificationsComposer Composer, IUmbracoBuilder Builder, IServiceCollection Services)
        BuildComposer()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        builder.Config.Returns(configuration);
        return (new NotificationsComposer(), builder, services);
    }

    private static void StubDependencies(IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
            monitor.CurrentValue.Returns(new AiVisibilitySettings());
            return monitor;
        });
        services.AddSingleton(_ => Substitute.For<IServerRoleAccessor>());
        services.AddSingleton(_ => Substitute.For<IScopeProvider>());
        services.AddSingleton(_ => Substitute.For<IEventAggregator>());
        services.AddLogging();
    }

    private sealed class NoOpAdopterLog : IRequestLog
    {
        public Task EnqueueAsync(RequestLogEntry entry, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
