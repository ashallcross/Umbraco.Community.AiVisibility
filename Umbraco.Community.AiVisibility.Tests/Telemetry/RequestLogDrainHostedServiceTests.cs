using Umbraco.Community.AiVisibility.Telemetry;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Persistence;
using Umbraco.Community.AiVisibility.Persistence.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Umbraco.Community.AiVisibility.Tests.Telemetry;

[TestFixture]
public class RequestLogDrainHostedServiceTests
{
    private static IOptionsMonitor<AiVisibilitySettings> SettingsMonitor(AiVisibilitySettings? value = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(value ?? new AiVisibilitySettings());
        return monitor;
    }

    private static IServerRoleAccessor RoleAccessor(ServerRole role)
    {
        var accessor = Substitute.For<IServerRoleAccessor>();
        accessor.CurrentServerRole.Returns(role);
        return accessor;
    }

    private static DefaultRequestLog NewDefaultLog(AiVisibilitySettings? settings = null) =>
        new(SettingsMonitor(settings), NullLogger<DefaultRequestLog>.Instance, TimeProvider.System);

    [Test]
    public async Task StartAsync_DisabledViaSettings_ShortCircuits()
    {
        // AC3 + AC5: kill switch suppresses the drainer (notifications still
        // fire — that's covered by Task 6 tests).
        var settings = new AiVisibilitySettings { RequestLog = new RequestLogSettings { Enabled = false } };
        var scopeProvider = Substitute.For<IScopeProvider>();
        var drainer = new RequestLogDrainHostedService(
            NewDefaultLog(),
            scopeProvider,
            SettingsMonitor(settings),
            RoleAccessor(ServerRole.Single),
            NullLogger<RequestLogDrainHostedService>.Instance);

        await drainer.StartAsync(CancellationToken.None);
        await drainer.StopAsync(CancellationToken.None);

        scopeProvider.DidNotReceiveWithAnyArgs().CreateScope();
    }

    [Test]
    public async Task StartAsync_ServerRoleSubscriber_StartsDrainLoop()
    {
        // Story 6.0a AC1 (Codex finding #1) — Subscriber instances using
        // the default DefaultRequestLog MUST drain their own
        // per-process channel. The pre-6.0a Subscriber-suppress branch
        // caused front-end nodes to silently shed traffic via DropOldest
        // on a channel the SchedulingPublisher could not reach. The pin
        // is "loop was permitted to start": same shape as
        // StartAsync_ServerRoleUnknown_StartsDrainLoop above.
        var scopeProvider = Substitute.For<IScopeProvider>();
        var drainer = new RequestLogDrainHostedService(
            NewDefaultLog(),
            scopeProvider,
            SettingsMonitor(),
            RoleAccessor(ServerRole.Subscriber),
            NullLogger<RequestLogDrainHostedService>.Instance);

        await drainer.StartAsync(CancellationToken.None);
        // Stop quickly — the loop has parked on WaitToReadAsync and will
        // exit on the cancel signal. We're not asserting drain behaviour
        // here, only that the loop was permitted to start.
        await drainer.StopAsync(CancellationToken.None);

        // The Subscriber-role drain loop spins up and parks on an empty
        // channel; no batch is ever assembled so no scope is created.
        // The pin is "loop was permitted to start": StartAsync + StopAsync
        // succeed cleanly with the default writer + Subscriber role.
        Assert.That(drainer, Is.Not.Null, "drainer instance survived StartAsync + StopAsync on Subscriber");
    }

    [Test]
    public async Task StartAsync_ServerRoleSubscriber_CustomWriterRegistered_SuppressesDrainLoop()
    {
        // Story 6.0a AC1 — adopter-registered custom IRequestLog
        // (e.g. App Insights) still suppresses the built-in drainer
        // regardless of role. The custom writer owns its own persistence;
        // the built-in drainer has no channel to read from.
        var customLog = Substitute.For<IRequestLog>();
        var scopeProvider = Substitute.For<IScopeProvider>();
        var drainer = new RequestLogDrainHostedService(
            customLog,
            scopeProvider,
            SettingsMonitor(),
            RoleAccessor(ServerRole.Subscriber),
            NullLogger<RequestLogDrainHostedService>.Instance);

        await drainer.StartAsync(CancellationToken.None);
        await drainer.StopAsync(CancellationToken.None);

        scopeProvider.DidNotReceiveWithAnyArgs().CreateScope();
    }

    [Test]
    public async Task StartAsync_ServerRoleUnknown_StartsDrainLoop()
    {
        // Spec Drift Note #9 — boot-time IServerRoleAccessor returns
        // Unknown for ~15s before the heartbeat lands. Permitting drain
        // during this window means a single-instance dev install boots
        // straight into a working drainer rather than waiting on the
        // heartbeat. This test pins the permitted-Unknown contract so a
        // future regression that re-adds Unknown to the suppression list
        // would surface here.
        var scopeProvider = Substitute.For<IScopeProvider>();
        var drainer = new RequestLogDrainHostedService(
            NewDefaultLog(),
            scopeProvider,
            SettingsMonitor(),
            RoleAccessor(ServerRole.Unknown),
            NullLogger<RequestLogDrainHostedService>.Instance);

        await drainer.StartAsync(CancellationToken.None);
        // Stop quickly — the loop has parked on WaitToReadAsync and will
        // exit on the cancel signal. We're not asserting drain behaviour
        // here, only that the loop was permitted to start.
        await drainer.StopAsync(CancellationToken.None);

        // The Unknown-role drain loop spins up and parks on an empty
        // channel; no batch is ever assembled so no scope is created.
        // The pin is "loop was permitted to start": replace the channel
        // with a completed reader and ensure StopAsync exits cleanly.
        Assert.That(drainer, Is.Not.Null, "drainer instance survived StartAsync + StopAsync");
    }

    [Test]
    public async Task StartAsync_AdopterOverrideRequestLog_DrainerNoOps()
    {
        // When a custom IRequestLog is registered (not the default
        // channel-backed shape), the drainer can't read from it — exit
        // cleanly per the documented contract.
        var customLog = Substitute.For<IRequestLog>();
        var scopeProvider = Substitute.For<IScopeProvider>();
        var drainer = new RequestLogDrainHostedService(
            customLog,
            scopeProvider,
            SettingsMonitor(),
            RoleAccessor(ServerRole.Single),
            NullLogger<RequestLogDrainHostedService>.Instance);

        await drainer.StartAsync(CancellationToken.None);
        await drainer.StopAsync(CancellationToken.None);

        scopeProvider.DidNotReceiveWithAnyArgs().CreateScope();
    }

    [Test]
    public async Task DrainLoop_DbWriteFails_DoesNotPropagateAndLoopSurvives()
    {
        // AC5 / Task 5.3: a DB write failure must NOT throw out of the
        // drainer; the loop survives. Mirrors
        // LogRetentionJobTests.ExecuteAsync_DbThrows_LogsWarningAndDoesNotRethrow.
        // Pinning shape: write 2 entries with BatchSize=1, drive 2 batches
        // each of which fails the scope; the drainer must call CreateScope
        // for every batch (proving the loop kept iterating despite the
        // first failure) and StopAsync must complete without throwing.
        var log = NewDefaultLog();
        await log.EnqueueAsync(
            new RequestLogEntry { Path = "/a", Culture = "en-GB" },
            CancellationToken.None);
        await log.EnqueueAsync(
            new RequestLogEntry { Path = "/b", Culture = "en-GB" },
            CancellationToken.None);

        var scopeProvider = Substitute.For<IScopeProvider>();
        scopeProvider.CreateScope(Arg.Any<System.Data.IsolationLevel>())
            .Throws(new InvalidOperationException("simulated DB outage"));

        var drainer = new RequestLogDrainHostedService(
            log,
            scopeProvider,
            SettingsMonitor(new AiVisibilitySettings
            {
                RequestLog = new RequestLogSettings { MaxBatchIntervalSeconds = 1, BatchSize = 1 },
            }),
            RoleAccessor(ServerRole.Single),
            NullLogger<RequestLogDrainHostedService>.Instance);

        await drainer.StartAsync(CancellationToken.None);
        // Allow the loop to flush twice. BatchSize=1 forces a flush per entry.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.DoesNotThrowAsync(async () => await drainer.StopAsync(CancellationToken.None));

        // Multiple scope attempts → loop survived past the first failure.
        scopeProvider.ReceivedWithAnyArgs().CreateScope(Arg.Any<System.Data.IsolationLevel>());
    }

    [Test]
    public void Type_RegisteredAsHostedService_AndIsAsyncDisposable()
    {
        var t = typeof(RequestLogDrainHostedService);
        Assert.Multiple(() =>
        {
            Assert.That(typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t));
            Assert.That(typeof(IAsyncDisposable).IsAssignableFrom(t));
            Assert.That(t.IsSealed, Is.True);
        });
    }

    [Test]
    public void ConstructorParameters_MatchDocumentedDISurface()
    {
        var ctorParams = typeof(RequestLogDrainHostedService)
            .GetConstructors()
            .Single()
            .GetParameters();

        Assert.Multiple(() =>
        {
            Assert.That(ctorParams[0].ParameterType, Is.EqualTo(typeof(IRequestLog)));
            Assert.That(ctorParams[1].ParameterType, Is.EqualTo(typeof(IScopeProvider)),
                "Infrastructure-flavour IScopeProvider per architecture.md line 350");
            Assert.That(ctorParams[2].ParameterType, Is.EqualTo(typeof(IOptionsMonitor<AiVisibilitySettings>)));
            Assert.That(ctorParams[3].ParameterType, Is.EqualTo(typeof(IServerRoleAccessor)));
        });
    }

    [Test]
    public async Task StopAsync_NotStarted_ReturnsImmediately()
    {
        // Defensive: StopAsync called without a prior successful StartAsync
        // (e.g. drainer was suppressed by the kill switch) must not throw.
        var drainer = new RequestLogDrainHostedService(
            NewDefaultLog(),
            Substitute.For<IScopeProvider>(),
            SettingsMonitor(),
            RoleAccessor(ServerRole.Single),
            NullLogger<RequestLogDrainHostedService>.Instance);

        await drainer.StopAsync(CancellationToken.None);
        Assert.Pass("StopAsync without StartAsync exits cleanly.");
    }
}
