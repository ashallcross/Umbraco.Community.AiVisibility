using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.V13.Jobs;

namespace Umbraco.Community.AiVisibility.V13.Tests.Jobs;

/// <summary>
/// Story 9.2 AC5 — the novel v13 job behaviour: best-effort role gating
/// (Single OR SchedulingPublisher, re-read per cycle) and the Period disable
/// contract. Pure unit tests — no Umbraco harness.
/// </summary>
[TestFixture]
public sealed class LogRetentionJobTests
{
    private static AiVisibilitySettings SettingsWith(LogRetentionSettings retention)
        => new() { LogRetention = retention };

    private static IOptionsMonitor<AiVisibilitySettings> Monitor(AiVisibilitySettings settings)
    {
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(settings);
        return monitor;
    }

    private static LogRetentionJob CreateJob(
        AiVisibilitySettings settings,
        IScopeProvider scopeProvider,
        ServerRole role)
    {
        var roleAccessor = Substitute.For<IServerRoleAccessor>();
        roleAccessor.CurrentServerRole.Returns(role);
        return new LogRetentionJob(
            Monitor(settings),
            scopeProvider,
            roleAccessor,
            NullLogger<LogRetentionJob>.Instance);
    }

    [Test]
    public void Period_DurationDaysZero_ReturnsInfinite_DisableContract()
    {
        var job = CreateJob(
            SettingsWith(new LogRetentionSettings { DurationDays = 0 }),
            Substitute.For<IScopeProvider>(),
            ServerRole.Single);

        Assert.That(job.Period, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void Period_NormalConfig_ReturnsConfiguredHours()
    {
        var job = CreateJob(
            SettingsWith(new LogRetentionSettings { DurationDays = 90, RunIntervalHours = 24 }),
            Substitute.For<IScopeProvider>(),
            ServerRole.Single);

        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromHours(24)));
    }

    [Test]
    public void Period_SecondsOverride_TakesPrecedence()
    {
        var job = CreateJob(
            SettingsWith(new LogRetentionSettings { DurationDays = 90, RunIntervalHours = 24, RunIntervalSecondsOverride = 5 }),
            Substitute.For<IScopeProvider>(),
            ServerRole.Single);

        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    [TestCase(ServerRole.Subscriber)]
    [TestCase(ServerRole.Unknown)]
    public async Task RunJobAsync_NonRunningRole_DoesNotOpenScope(ServerRole role)
    {
        var scopeProvider = Substitute.For<IScopeProvider>();
        var job = CreateJob(
            SettingsWith(new LogRetentionSettings { DurationDays = 90, RunIntervalHours = 24 }),
            scopeProvider,
            role);

        await job.RunJobAsync();

        scopeProvider.DidNotReceiveWithAnyArgs().CreateScope();
    }

    [TestCase(ServerRole.Single)]
    [TestCase(ServerRole.SchedulingPublisher)]
    public async Task RunJobAsync_RunningRole_OpensScopeAndDeletes(ServerRole role)
    {
        var database = Substitute.For<IUmbracoDatabase>();
        database.Execute(Arg.Any<string>(), Arg.Any<object[]>()).Returns(0);
        var scope = Substitute.For<IScope>();
        scope.Database.Returns(database);
        var scopeProvider = Substitute.For<IScopeProvider>();
        scopeProvider.CreateScope(Arg.Any<IsolationLevel>()).Returns(scope);

        var job = CreateJob(
            SettingsWith(new LogRetentionSettings { DurationDays = 90, RunIntervalHours = 24 }),
            scopeProvider,
            role);

        await job.RunJobAsync();

        scopeProvider.Received(1).CreateScope(IsolationLevel.ReadCommitted);
        database.ReceivedWithAnyArgs(1).Execute(default!, default(object[])!);
        scope.Received(1).Complete();
    }
}
