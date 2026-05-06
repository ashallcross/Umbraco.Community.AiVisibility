using Umbraco.Community.AiVisibility.Telemetry;
using Umbraco.Community.AiVisibility.Composing;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Robots;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace Umbraco.Community.AiVisibility.Tests.Composing;

[TestFixture]
public class HealthChecksComposerTests
{
    [Test]
    public void Compose_RegistersIRobotsAuditor_AsSingleton()
    {
        // Story 4.2 — IRobotsAuditor MUST be Singleton (project-context.md
        // § DI Lifetimes lists it as the canonical Singleton example).
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        var descriptor = services.Single(d => d.ServiceType == typeof(IRobotsAuditor));
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Lifetime, Is.EqualTo(ServiceLifetime.Singleton));
            Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(DefaultRobotsAuditor)));
        });
    }

    [Test]
    public void Compose_RegistersAiBotList_AsSingleton()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        var descriptor = services.Single(d => d.ServiceType == typeof(AiBotList));
        Assert.That(descriptor.Lifetime, Is.EqualTo(ServiceLifetime.Singleton));
    }

    [Test]
    public void Compose_RegistersStartupRobotsAuditRunner_AsHostedService()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        var hostedDescriptors = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .ToArray();

        Assert.That(
            hostedDescriptors.Any(d => d.ImplementationType == typeof(StartupRobotsAuditRunner)),
            Is.True,
            "StartupRobotsAuditRunner must be registered via AddHostedService");
    }

    [Test]
    public void Compose_RegistersRobotsAuditRefreshJob_AsIDistributedBackgroundJob()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        var distributedJobs = services
            .Where(d => d.ServiceType == typeof(IDistributedBackgroundJob))
            .ToArray();

        Assert.That(
            distributedJobs.Any(d => d.ImplementationType == typeof(RobotsAuditRefreshJob)
                                     && d.Lifetime == ServiceLifetime.Singleton),
            Is.True,
            "RobotsAuditRefreshJob must be registered as Singleton<IDistributedBackgroundJob>");
    }

    [Test]
    public void AdopterOverride_PreRegisteredBeforeComposer_DefaultDoesNotReplace()
    {
        var (composer, builder, services) = BuildComposer();
        services.AddSingleton<IRobotsAuditor, NoOpAdopterAuditor>();

        composer.Compose(builder);

        var registrations = services
            .Where(d => d.ServiceType == typeof(IRobotsAuditor))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(registrations, Has.Length.EqualTo(1),
                "TryAdd* must not stack a duplicate registration");
            Assert.That(registrations[0].ImplementationType,
                Is.EqualTo(typeof(NoOpAdopterAuditor)),
                "adopter's pre-registration wins; DefaultRobotsAuditor is NOT registered");
        });
    }

    [Test]
    public void Compose_AdopterScopedOverride_ThrowsAtComposerTime()
    {
        // D2 lifetime guard: an adopter pre-registering IRobotsAuditor as
        // Scoped (or Transient) would form a captive dep in the Singleton
        // RobotsAuditRefreshJob. The composer rejects the misregistration up
        // front so the failure surfaces at composition time, not at the first
        // refresh-cycle resolution.
        var (composer, builder, services) = BuildComposer();
        services.AddScoped<IRobotsAuditor, NoOpAdopterAuditor>();

        var ex = Assert.Throws<InvalidOperationException>(() => composer.Compose(builder));
        Assert.That(ex!.Message, Does.Contain("must be registered as Singleton"));
    }

    [Test]
    public void Compose_RegistersNamedHttpClient_ForRobotsAuditor()
    {
        // P1 SSRF defence: the named HttpClient registered for
        // DefaultRobotsAuditor.HttpClientName resolves cleanly through DI.
        // The handler-side AllowAutoRedirect=false setting is verified
        // end-to-end by chunk-2's Audit_RedirectResponse_RefusedNotFollowed
        // test using a capture handler; this test pins the registration shape.
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);
        services.AddLogging();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(DefaultRobotsAuditor.HttpClientName);

        Assert.That(client, Is.Not.Null);
    }

    [Test]
    public void Compose_StartupValidation_HealthChecksComposer_NoCaptiveDependency()
    {
        // Story 4.2 — canonical DI gate. ValidateOnBuild + ValidateScopes
        // catches captive dependencies at service-provider construction time.
        // Stub-driven (Story 3.2 + 4.1 precedent — DefaultRobotsAuditor +
        // RobotsAuditRefreshJob + StartupRobotsAuditRunner each pull
        // dependencies the test harness can stub without booting Umbraco).
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        StubAuditorDependencies(services);

        Assert.DoesNotThrow(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            // IRobotsAuditor is Singleton — resolve from root.
            var auditor = provider.GetRequiredService<IRobotsAuditor>();
            Assert.That(auditor, Is.Not.Null);

            // The IDistributedBackgroundJob registration must also resolve cleanly.
            var jobs = provider.GetServices<IDistributedBackgroundJob>().ToArray();
            Assert.That(jobs.Any(j => j is RobotsAuditRefreshJob), Is.True);
        }, "ValidateScopes + ValidateOnBuild must succeed — Singleton auditor + Singleton job + Scoped IDomainService dep flow are captive-free");
    }

    private static (RobotsComposer Composer, IUmbracoBuilder Builder, IServiceCollection Services)
        BuildComposer()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        builder.Config.Returns(configuration);
        return (new RobotsComposer(), builder, services);
    }

    private static void StubAuditorDependencies(IServiceCollection services)
    {
        services.AddSingleton(_ => Substitute.For<IHttpClientFactory>());
        services.AddSingleton(_ => new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache())));
        services.AddSingleton(_ =>
        {
            var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
            monitor.CurrentValue.Returns(new AiVisibilitySettings());
            return monitor;
        });
        services.AddSingleton(_ => Substitute.For<IServerRoleAccessor>());
        services.AddScoped(_ => Substitute.For<IDomainService>());
        services.AddSingleton(_ => Substitute.For<IHttpContextAccessor>());
        services.AddLogging();
    }

    private sealed class NoOpAdopterAuditor : IRobotsAuditor
    {
        public Task<RobotsAuditResult> AuditAsync(string hostname, string scheme, CancellationToken cancellationToken)
            => Task.FromResult(new RobotsAuditResult(
                Hostname: hostname,
                Outcome: RobotsAuditOutcome.NoAiBlocks,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: DateTime.UtcNow));
    }
}
