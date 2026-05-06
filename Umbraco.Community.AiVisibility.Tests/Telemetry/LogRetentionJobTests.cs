using System.Data;
using Umbraco.Community.AiVisibility.Telemetry;
using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Umbraco.Community.AiVisibility.Tests.Telemetry;

[TestFixture]
public class LogRetentionJobTests
{
    private static IOptionsMonitor<AiVisibilitySettings> SettingsMonitor(AiVisibilitySettings? value = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(value ?? new AiVisibilitySettings());
        return monitor;
    }

    private static LogRetentionJob NewJob(
        AiVisibilitySettings? settings = null,
        IScopeProvider? scope = null,
        ILogger<LogRetentionJob>? logger = null) =>
        new(
            SettingsMonitor(settings),
            scope ?? Substitute.For<IScopeProvider>(),
            logger ?? NullLogger<LogRetentionJob>.Instance,
            TimeProvider.System);

    [Test]
    public void Period_DefaultSettings_Returns24Hours()
    {
        var job = NewJob();
        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromHours(24)));
    }

    [Test]
    public void Period_DurationDaysZero_ReturnsInfiniteTimeSpan()
    {
        // Story 4.2 chunk-3 P2 ratification — InfiniteTimeSpan, NOT
        // TimeSpan.Zero (which causes Umbraco runner hot-loop).
        var settings = new AiVisibilitySettings
        {
            LogRetention = new LogRetentionSettings { DurationDays = 0 },
        };

        var job = NewJob(settings);

        Assert.That(job.Period, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void Period_RunIntervalHoursZero_ReturnsInfiniteTimeSpan()
    {
        var settings = new AiVisibilitySettings
        {
            LogRetention = new LogRetentionSettings { DurationDays = 30, RunIntervalHours = 0 },
        };

        var job = NewJob(settings);

        Assert.That(job.Period, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void Period_RunIntervalSecondsOverride_TakesPrecedenceOverHours()
    {
        // Architect-A5 escape hatch — seconds override lets the manual
        // gate verify exactly-once across instances in minutes rather
        // than days.
        var settings = new AiVisibilitySettings
        {
            LogRetention = new LogRetentionSettings
            {
                DurationDays = 30,
                RunIntervalHours = 24,
                RunIntervalSecondsOverride = 30,
            },
        };

        var job = NewJob(settings);

        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void Period_RunIntervalHoursAboveCap_ClampsToYear()
    {
        // Operator typo: 1_000_000 hours would overflow TimeSpan.FromHours.
        var settings = new AiVisibilitySettings
        {
            LogRetention = new LogRetentionSettings
            {
                DurationDays = 30,
                RunIntervalHours = 1_000_000,
            },
        };

        var job = NewJob(settings);

        Assert.That(job.Period, Is.EqualTo(TimeSpan.FromHours(LogRetentionJob.MaxRunIntervalHours)));
    }

    [Test]
    public async Task ExecuteAsync_DurationDaysDisabled_ShortCircuits()
    {
        var settings = new AiVisibilitySettings
        {
            LogRetention = new LogRetentionSettings { DurationDays = 0 },
        };
        var scope = Substitute.For<IScopeProvider>();
        var job = NewJob(settings, scope);

        await job.ExecuteAsync();

        scope.DidNotReceiveWithAnyArgs().CreateScope();
        scope.DidNotReceiveWithAnyArgs().CreateScope(Arg.Any<IsolationLevel>());
    }

    [Test]
    public async Task ExecuteAsync_HappyPath_OpensScopeExecutesDeleteAndCompletes()
    {
        var scopeProvider = Substitute.For<IScopeProvider>();
        var scope = Substitute.For<IScope>();
        var database = Substitute.For<global::Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase>();
        scope.Database.Returns(database);
        scopeProvider.CreateScope(Arg.Any<IsolationLevel>()).Returns(scope);
        database.Execute(Arg.Any<string>(), Arg.Any<object[]>()).Returns(7);

        var logger = Substitute.For<ILogger<LogRetentionJob>>();
        var job = NewJob(scope: scopeProvider, logger: logger);

        await job.ExecuteAsync();

        // AC9 — Infrastructure-flavour scope opens with ReadCommitted.
        scopeProvider.Received(1).CreateScope(IsolationLevel.ReadCommitted);
        database.Received(1).Execute(
            Arg.Is<string>(sql => sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
                                  && sql.Contains("aiVisibilityRequestLog")),
            Arg.Any<object[]>());
        scope.Received(1).Complete();
        // RUN log line emission — architect-A5 manual gate Step 13's
        // exactly-once verification depends on `grep "AiVisibility log retention
        // job RUN"` succeeding. Pin it via the underlying ILogger.Log
        // method (LogInformation is an extension method NSubstitute
        // cannot intercept directly).
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o!.ToString()!.Contains("AiVisibility log retention job RUN")),
            null,
            Arg.Any<Func<object, Exception?, string>>()!);
    }

    [Test]
    public async Task ExecuteAsync_DbThrows_LogsWarningAndDoesNotRethrow()
    {
        var scopeProvider = Substitute.For<IScopeProvider>();
        var scope = Substitute.For<IScope>();
        var database = Substitute.For<global::Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase>();
        scope.Database.Returns(database);
        scopeProvider.CreateScope(Arg.Any<IsolationLevel>()).Returns(scope);
        database
            .When(d => d.Execute(Arg.Any<string>(), Arg.Any<object[]>()))
            .Do(_ => throw new InvalidOperationException("simulated DB failure"));

        var job = NewJob(scope: scopeProvider);

        Assert.DoesNotThrowAsync(async () => await job.ExecuteAsync());
        // scope.Complete() must NOT be called on failure (auto-rollback).
        scope.DidNotReceive().Complete();
    }

    [Test]
    public void Type_ImplementsIDistributedBackgroundJob_AndIsSealed()
    {
        var t = typeof(LogRetentionJob);
        Assert.Multiple(() =>
        {
            Assert.That(typeof(IDistributedBackgroundJob).IsAssignableFrom(t));
            Assert.That(t.IsSealed, Is.True);
        });
    }
}
