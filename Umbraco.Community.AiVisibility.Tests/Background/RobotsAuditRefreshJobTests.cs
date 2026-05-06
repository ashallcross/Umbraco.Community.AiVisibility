using LlmsTxt.Umbraco.Background;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Robots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace LlmsTxt.Umbraco.Tests.Background;

[TestFixture]
public class RobotsAuditRefreshJobTests
{
    [Test]
    public void Period_RefreshIntervalHoursPositive_ReturnsThatMany()
    {
        var (job, _, _) = BuildJob(refreshIntervalHours: 6);
        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromHours(6)));
    }

    [Test]
    public void Period_RefreshIntervalHoursZero_ReturnsInfinite()
    {
        // Story 4.2 — RefreshIntervalHours <= 0 disables the recurring refresh.
        // Returns Timeout.InfiniteTimeSpan (canonical "never fire" sentinel for
        // periodic-timer APIs) — NOT TimeSpan.Zero, which Umbraco's
        // distributed-job runner would treat as "fire immediately, every tick".
        var (job, _, _) = BuildJob(refreshIntervalHours: 0);
        Assert.That(job.Period, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void Period_RefreshIntervalHoursNegative_ReturnsInfinite()
    {
        var (job, _, _) = BuildJob(refreshIntervalHours: -5);
        Assert.That(job.Period, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void Period_RefreshIntervalHoursIntMaxValue_ClampedToOneYear()
    {
        // int.MaxValue would overflow TimeSpan.FromHours; the Period getter
        // clamps to MaxRefreshIntervalHours (one year) so an operator typo
        // doesn't crash the runner's tick poll.
        var (job, _, _) = BuildJob(refreshIntervalHours: int.MaxValue);
        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromHours(RobotsAuditRefreshJob.MaxRefreshIntervalHours)));
    }

    [Test]
    public void Period_RefreshIntervalSecondsOverrideExceedsCap_ClampedToOneDay()
    {
        var (job, _, _) = BuildJob(refreshIntervalHours: 24,
            secondsOverride: RobotsAuditRefreshJob.MaxRefreshIntervalSecondsOverride * 2);
        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromSeconds(RobotsAuditRefreshJob.MaxRefreshIntervalSecondsOverride)));
    }

    [Test]
    public async Task ExecuteAsync_RefreshIntervalHoursZero_NoAuditCalls()
    {
        var (job, auditor, _) = BuildJob(refreshIntervalHours: 0);

        await job.ExecuteAsync();

        await auditor.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_PerHost_OneAuditCallPerHost()
    {
        var (job, auditor, _) = BuildJob(refreshIntervalHours: 6,
            domainNames: new[] { "https://sitea.example", "https://siteb.example" });
        auditor
            .RefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RobotsAuditResult(
                Hostname: "x",
                Outcome: RobotsAuditOutcome.NoAiBlocks,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: DateTime.UtcNow)));

        await job.ExecuteAsync();

        await auditor.Received(1).RefreshAsync("sitea.example", "https", Arg.Any<CancellationToken>());
        await auditor.Received(1).RefreshAsync("siteb.example", "https", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_OneHostThrows_OthersStillRun()
    {
        // ResolveHostnames enumerates a Dictionary, so iteration order isn't
        // guaranteed across runtime versions. Set both hosts to throw + good
        // independently and assert each received exactly one call — no matter
        // which order the runner picked.
        var (job, auditor, _) = BuildJob(refreshIntervalHours: 6,
            domainNames: new[] { "https://sitea.example", "https://siteb.example" });
        auditor
            .RefreshAsync("sitea.example", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<RobotsAuditResult>>(_ => throw new HttpRequestException("simulated"));
        auditor
            .RefreshAsync("siteb.example", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RobotsAuditResult(
                Hostname: "siteb.example",
                Outcome: RobotsAuditOutcome.NoAiBlocks,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: DateTime.UtcNow)));

        Assert.DoesNotThrowAsync(async () => await job.ExecuteAsync(),
            "exceptions in one host's audit must not stall the others");
        // Both hosts MUST be called regardless of which throws — siteb.example
        // proves the loop continued past sitea.example's throw, AND the symmetric
        // call to sitea.example proves the throwing host was actually attempted
        // (not silently skipped).
        await auditor.Received(1).RefreshAsync("sitea.example", "https", Arg.Any<CancellationToken>());
        await auditor.Received(1).RefreshAsync("siteb.example", "https", Arg.Any<CancellationToken>());
    }

    [Test]
    public void Period_RefreshIntervalSecondsOverride_TakesPrecedence()
    {
        // Story 4.2 dev-only escape hatch — used by the architect-A5
        // two-instance gate so cycles fire every ~30s instead of ≥1h.
        var (job, _, _) = BuildJob(refreshIntervalHours: 24, secondsOverride: 30);
        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void Period_SecondsOverrideZeroOrNegative_FallsBackToHours()
    {
        // Defensive: an adopter / dev who sets the override to 0 or -5 in
        // appsettings (operator typo) should fall back to the hours knob.
        var (job, _, _) = BuildJob(refreshIntervalHours: 6, secondsOverride: 0);
        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromHours(6)));
        var (job2, _, _) = BuildJob(refreshIntervalHours: 6, secondsOverride: -5);
        Assert.That(job2.Period, Is.EqualTo(TimeSpan.FromHours(6)));
    }

    [Test]
    public async Task ExecuteAsync_SecondsOverridePositive_StillAuditsEvenIfHoursZero()
    {
        // The seconds override should fire the audit cycle even when the
        // hours knob is set to "disabled" (0). Useful for a dev who wants
        // ONLY the seconds-precision cadence without dual-knob overlap.
        var (job, auditor, _) = BuildJob(refreshIntervalHours: 0, secondsOverride: 30,
            domainNames: new[] { "https://sitea.example" });
        auditor
            .RefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RobotsAuditResult(
                "x", RobotsAuditOutcome.NoAiBlocks, Array.Empty<RobotsAuditFinding>(), DateTime.UtcNow)));

        await job.ExecuteAsync();

        await auditor.Received(1).RefreshAsync("sitea.example", "https", Arg.Any<CancellationToken>());
    }

    private static (RobotsAuditRefreshJob Job, IRobotsAuditor Auditor, IServiceProvider Services)
        BuildJob(int refreshIntervalHours, int? secondsOverride = null, IReadOnlyList<string>? domainNames = null)
    {
        var settings = new AiVisibilitySettings
        {
            RobotsAuditor = new RobotsAuditorSettings
            {
                RefreshIntervalHours = refreshIntervalHours,
                RefreshIntervalSecondsOverride = secondsOverride,
            },
        };
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(settings);

        var auditor = Substitute.For<IRobotsAuditor>();

        var domainService = Substitute.For<IDomainService>();
        var domains = (domainNames ?? Array.Empty<string>())
            .Select(name =>
            {
                var d = Substitute.For<IDomain>();
                d.DomainName.Returns(name);
                return d;
            })
            .ToArray();
#pragma warning disable CS0618
        domainService.GetAll(Arg.Any<bool>()).Returns(domains);
#pragma warning restore CS0618

        var services = new ServiceCollection();
        services.AddSingleton(domainService);
        services.AddSingleton(auditor);
        var provider = services.BuildServiceProvider();

        var job = new RobotsAuditRefreshJob(
            provider,
            monitor,
            NullLogger<RobotsAuditRefreshJob>.Instance);

        return (job, auditor, provider);
    }
}
