using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Notifications;
using Umbraco.Community.AiVisibility.Persistence;
using Umbraco.Community.AiVisibility.Persistence.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Umbraco.Community.AiVisibility.Tests.Notifications;

[TestFixture]
public class DefaultLlmsRequestLogHandlerTests
{
    private IRequestLog _requestLog = null!;
    private IOptionsMonitor<AiVisibilitySettings> _settings = null!;
    private TimeProvider _timeProvider = null!;
    private DefaultRequestLogHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _requestLog = Substitute.For<IRequestLog>();
        _settings = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        _settings.CurrentValue.Returns(new AiVisibilitySettings());
        _timeProvider = TimeProvider.System;
        _handler = new DefaultRequestLogHandler(
            _requestLog,
            _settings,
            _timeProvider,
            NullLogger<DefaultRequestLogHandler>.Instance);
    }

    [Test]
    public async Task HandleAsync_MarkdownNotification_EnqueuesEntryWithCorrectShape()
    {
        var contentKey = Guid.NewGuid();
        var n = new MarkdownPageRequestedNotification(
            path: "/about",
            contentKey: contentKey,
            culture: "en-US",
            userAgentClassification: UserAgentClass.HumanBrowser,
            referrerHost: "google.com");

        await _handler.HandleAsync(n, CancellationToken.None);

        await _requestLog.Received(1).EnqueueAsync(
            Arg.Is<RequestLogEntry>(e =>
                e.Path == "/about"
                && e.ContentKey == contentKey
                && e.Culture == "en-US"
                && e.UserAgentClass == nameof(UserAgentClass.HumanBrowser)
                && e.ReferrerHost == "google.com"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_LlmsTxtNotification_PathIsLlmsTxt_ContentKeyIsNull()
    {
        var n = new LlmsTxtRequestedNotification(
            hostname: "example.com",
            culture: "en-US",
            userAgentClassification: UserAgentClass.AiTraining,
            referrerHost: null);

        await _handler.HandleAsync(n, CancellationToken.None);

        await _requestLog.Received(1).EnqueueAsync(
            Arg.Is<RequestLogEntry>(e =>
                e.Path == "/llms.txt"
                && e.ContentKey == null
                && e.UserAgentClass == nameof(UserAgentClass.AiTraining)
                && e.ReferrerHost == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_LlmsFullTxtNotification_PathIsLlmsFullTxt()
    {
        var n = new LlmsFullTxtRequestedNotification(
            hostname: "example.com",
            culture: "en-US",
            userAgentClassification: UserAgentClass.AiSearchRetrieval,
            referrerHost: null,
            bytesServed: 12345);

        await _handler.HandleAsync(n, CancellationToken.None);

        await _requestLog.Received(1).EnqueueAsync(
            Arg.Is<RequestLogEntry>(e =>
                e.Path == "/llms-full.txt"
                && e.ContentKey == null
                && e.UserAgentClass == nameof(UserAgentClass.AiSearchRetrieval)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_RequestLogDisabled_ShortCircuitsWithoutEnqueue()
    {
        // AC3 — kill switch decouples the writer; notifications still
        // reach OUR handler (Umbraco dispatched), but our handler
        // short-circuits before calling IRequestLog.
        var settings = new AiVisibilitySettings
        {
            RequestLog = new RequestLogSettings { Enabled = false },
        };
        _settings.CurrentValue.Returns(settings);

        await _handler.HandleAsync(
            new MarkdownPageRequestedNotification(
                "/p", Guid.NewGuid(), "en", UserAgentClass.AiTraining, null),
            CancellationToken.None);

        await _requestLog.DidNotReceive().EnqueueAsync(
            Arg.Any<RequestLogEntry>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_RequestLogThrows_DoesNotPropagateToCaller()
    {
        // AC2 defence-in-depth: the handler swallows the writer's
        // exceptions so adopters publishing notifications can't be
        // surprised by writer failures.
        _requestLog
            .EnqueueAsync(Arg.Any<RequestLogEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("simulated DB write failure"));

        Assert.DoesNotThrowAsync(async () => await _handler.HandleAsync(
            new MarkdownPageRequestedNotification(
                "/p", Guid.NewGuid(), "en", UserAgentClass.HumanBrowser, null),
            CancellationToken.None));
    }

    [Test]
    public void Type_ImplementsThreeNotificationAsyncHandlers()
    {
        var t = typeof(DefaultRequestLogHandler);
        Assert.Multiple(() =>
        {
            Assert.That(typeof(global::Umbraco.Cms.Core.Events.INotificationAsyncHandler<MarkdownPageRequestedNotification>)
                .IsAssignableFrom(t));
            Assert.That(typeof(global::Umbraco.Cms.Core.Events.INotificationAsyncHandler<LlmsTxtRequestedNotification>)
                .IsAssignableFrom(t));
            Assert.That(typeof(global::Umbraco.Cms.Core.Events.INotificationAsyncHandler<LlmsFullTxtRequestedNotification>)
                .IsAssignableFrom(t));
            Assert.That(t.IsSealed);
        });
    }
}
