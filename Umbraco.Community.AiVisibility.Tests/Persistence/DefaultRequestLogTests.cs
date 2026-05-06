using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Persistence;
using Umbraco.Community.AiVisibility.Persistence.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Umbraco.Community.AiVisibility.Tests.Persistence;

[TestFixture]
public class DefaultLlmsRequestLogTests
{
    private static IOptionsMonitor<AiVisibilitySettings> SettingsMonitor(AiVisibilitySettings? value = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(value ?? new AiVisibilitySettings());
        return monitor;
    }

    private static RequestLogEntry SampleEntry() => new()
    {
        CreatedUtc = DateTime.UtcNow,
        Path = "/about",
        ContentKey = Guid.NewGuid(),
        Culture = "en-US",
        UserAgentClass = nameof(Umbraco.Community.AiVisibility.Persistence.UserAgentClass.HumanBrowser),
        ReferrerHost = "google.com",
    };

    [Test]
    public async Task EnqueueAsync_HappyPath_WritesToChannel()
    {
        var log = new DefaultRequestLog(
            SettingsMonitor(),
            NullLogger<DefaultRequestLog>.Instance,
            TimeProvider.System);

        await log.EnqueueAsync(SampleEntry(), CancellationToken.None);

        Assert.That(log.Reader.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task EnqueueAsync_QueueFull_DropsOldest_KeepsCapacityBound()
    {
        // QueueCapacity is clamped to a minimum of 64 — exercise overflow
        // by writing >64 entries and checking the channel never exceeds 64.
        var settings = new AiVisibilitySettings
        {
            RequestLog = new RequestLogSettings { QueueCapacity = 64 },
        };
        var log = new DefaultRequestLog(
            SettingsMonitor(settings),
            NullLogger<DefaultRequestLog>.Instance,
            TimeProvider.System);

        for (var i = 0; i < 200; i++)
        {
            await log.EnqueueAsync(SampleEntry(), CancellationToken.None);
        }

        Assert.That(log.Reader.Count, Is.LessThanOrEqualTo(64),
            "DropOldest semantics must keep the channel within capacity");
    }

    [Test]
    public async Task EnqueueAsync_QueueCapacityClampedBelowMinimum()
    {
        // Operator typo: QueueCapacity = 1 should clamp to MinQueueCapacity (64).
        // We confirm by enqueuing 64 entries and checking they all land
        // (a literal-1 queue would drop 63 of them).
        var settings = new AiVisibilitySettings
        {
            RequestLog = new RequestLogSettings { QueueCapacity = 1 },
        };
        var log = new DefaultRequestLog(
            SettingsMonitor(settings),
            NullLogger<DefaultRequestLog>.Instance,
            TimeProvider.System);

        for (var i = 0; i < 64; i++)
        {
            await log.EnqueueAsync(SampleEntry(), CancellationToken.None);
        }

        Assert.That(log.Reader.Count, Is.EqualTo(64));
    }

    [Test]
    public async Task EnqueueAsync_NullEntry_NoOp()
    {
        var log = new DefaultRequestLog(
            SettingsMonitor(),
            NullLogger<DefaultRequestLog>.Instance,
            TimeProvider.System);

        await log.EnqueueAsync(null!, CancellationToken.None);

        Assert.That(log.Reader.Count, Is.EqualTo(0));
    }

    [Test]
    public void EnqueueAsync_CancellationRequested_ReturnsCancelledTask()
    {
        var log = new DefaultRequestLog(
            SettingsMonitor(),
            NullLogger<DefaultRequestLog>.Instance,
            TimeProvider.System);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var task = log.EnqueueAsync(SampleEntry(), cts.Token);

        Assert.That(task.IsCanceled, Is.True);
        Assert.That(log.Reader.Count, Is.EqualTo(0));
    }
}
