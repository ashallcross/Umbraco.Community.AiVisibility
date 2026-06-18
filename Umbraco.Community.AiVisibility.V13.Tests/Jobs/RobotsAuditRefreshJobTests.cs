using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Robots;
using Umbraco.Community.AiVisibility.V13.Jobs;

namespace Umbraco.Community.AiVisibility.V13.Tests.Jobs;

[TestFixture]
public sealed class RobotsAuditRefreshJobTests
{
    [TestCase(ServerRole.Subscriber)]
    [TestCase(ServerRole.Unknown)]
    public async Task RunJobAsync_NonRunningRole_DoesNotResolveScopedServices(ServerRole role)
    {
        var services = new ServiceCollection();
        var auditor = new RecordingAuditor();
        services.AddSingleton<IRobotsAuditor>(auditor);
        services.AddScoped(_ => Substitute.For<IDomainService>());

        var job = CreateJob(services.BuildServiceProvider(), role);

        await job.RunJobAsync();

        Assert.That(auditor.Hostnames, Is.Empty);
    }

    [TestCase(ServerRole.Single)]
    [TestCase(ServerRole.SchedulingPublisher)]
    public async Task RunJobAsync_RunningRole_AuditsConcreteDomains(ServerRole role)
    {
        var services = new ServiceCollection();
        var auditor = new RecordingAuditor();
        services.AddSingleton<IRobotsAuditor>(auditor);
        services.AddScoped(_ => DomainService(
            Domain("https://example.com", isWildcard: false),
            Domain("*.example.net", isWildcard: true)));

        var job = CreateJob(services.BuildServiceProvider(), role);

        await job.RunJobAsync();

        Assert.That(auditor.Hostnames, Is.EqualTo(new[] { "example.com" }));
    }

    [Test]
    public void Period_Disabled_ReturnsInfinite()
    {
        var job = CreateJob(
            new ServiceCollection().BuildServiceProvider(),
            ServerRole.Single,
            new AiVisibilitySettings
            {
                RobotsAuditor = new RobotsAuditorSettings { RefreshIntervalHours = 0 },
            });

        Assert.That(job.Period, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    private static RobotsAuditRefreshJob CreateJob(
        IServiceProvider services,
        ServerRole role,
        AiVisibilitySettings? settings = null)
    {
        var roleAccessor = Substitute.For<IServerRoleAccessor>();
        roleAccessor.CurrentServerRole.Returns(role);

        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(settings ?? new AiVisibilitySettings
        {
            RobotsAuditor = new RobotsAuditorSettings { RefreshIntervalHours = 24 },
        });

        return new RobotsAuditRefreshJob(
            services,
            monitor,
            roleAccessor,
            NullLogger<RobotsAuditRefreshJob>.Instance);
    }

    private static IDomainService DomainService(params IDomain[] domains)
    {
        var service = Substitute.For<IDomainService>();
#pragma warning disable CS0618
        service.GetAll(includeWildcards: true).Returns(domains);
#pragma warning restore CS0618
        return service;
    }

    private static IDomain Domain(string domainName, bool isWildcard)
    {
        var domain = Substitute.For<IDomain>();
        domain.DomainName.Returns(domainName);
        domain.IsWildcard.Returns(isWildcard);
        return domain;
    }

    private sealed class RecordingAuditor : IRobotsAuditor
    {
        public List<string> Hostnames { get; } = new();

        public Task<RobotsAuditResult> AuditAsync(
            string hostname,
            string scheme,
            CancellationToken cancellationToken)
            => RefreshAsync(hostname, scheme, cancellationToken);

        public Task<RobotsAuditResult> RefreshAsync(
            string hostname,
            string scheme,
            CancellationToken cancellationToken)
        {
            Hostnames.Add(hostname);
            return Task.FromResult(new RobotsAuditResult(
                Hostname: hostname,
                Outcome: RobotsAuditOutcome.NoAiBlocks,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: DateTime.UtcNow));
        }
    }
}
