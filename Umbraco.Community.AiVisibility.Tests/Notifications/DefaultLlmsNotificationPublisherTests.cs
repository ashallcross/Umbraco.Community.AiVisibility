using LlmsTxt.Umbraco.Notifications;
using Umbraco.Community.AiVisibility.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Umbraco.Cms.Core.Events;

namespace LlmsTxt.Umbraco.Tests.Notifications;

[TestFixture]
public class DefaultLlmsNotificationPublisherTests
{
    private IEventAggregator _aggregator = null!;
    private IUserAgentClassifier _classifier = null!;
    private DefaultLlmsNotificationPublisher _publisher = null!;

    [SetUp]
    public void Setup()
    {
        _aggregator = Substitute.For<IEventAggregator>();
        _classifier = Substitute.For<IUserAgentClassifier>();
        _publisher = new DefaultLlmsNotificationPublisher(
            _aggregator,
            _classifier,
            NullLogger<DefaultLlmsNotificationPublisher>.Instance);
    }

    private static HttpContext NewContext(string? userAgent = null, string? referer = null)
    {
        var ctx = new DefaultHttpContext();
        if (userAgent is not null) ctx.Request.Headers.UserAgent = new StringValues(userAgent);
        if (referer is not null) ctx.Request.Headers.Referer = new StringValues(referer);
        return ctx;
    }

    [Test]
    public async Task PublishMarkdownPageAsync_PublishesWithCorrectShape()
    {
        _classifier.Classify("ChatGPT-User/1.0").Returns(UserAgentClass.AiUserTriggered);
        var ctx = NewContext("ChatGPT-User/1.0", "https://google.com/search?q=foo");
        var contentKey = Guid.NewGuid();

        await _publisher.PublishMarkdownPageAsync(ctx, "/about", contentKey, "en-US", CancellationToken.None);

        await _aggregator.Received(1).PublishAsync(
            Arg.Is<MarkdownPageRequestedNotification>(n =>
                n.Path == "/about"
                && n.ContentKey == contentKey
                && n.Culture == "en-US"
                && n.UserAgentClassification == UserAgentClass.AiUserTriggered
                && n.ReferrerHost == "google.com"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PublishLlmsTxtAsync_PublishesNotification()
    {
        _classifier.Classify(Arg.Any<string?>()).Returns(UserAgentClass.HumanBrowser);
        var ctx = NewContext("Mozilla/5.0");

        await _publisher.PublishLlmsTxtAsync(ctx, "example.com", "en-US", CancellationToken.None);

        await _aggregator.Received(1).PublishAsync(
            Arg.Is<LlmsTxtRequestedNotification>(n =>
                n.Hostname == "example.com"
                && n.Culture == "en-US"
                && n.UserAgentClassification == UserAgentClass.HumanBrowser),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PublishLlmsFullTxtAsync_CarriesBytesServed()
    {
        _classifier.Classify(Arg.Any<string?>()).Returns(UserAgentClass.AiSearchRetrieval);
        var ctx = NewContext("PerplexityBot");

        await _publisher.PublishLlmsFullTxtAsync(ctx, "example.com", "en", 12345, CancellationToken.None);

        await _aggregator.Received(1).PublishAsync(
            Arg.Is<LlmsFullTxtRequestedNotification>(n =>
                n.Hostname == "example.com"
                && n.BytesServed == 12345
                && n.UserAgentClassification == UserAgentClass.AiSearchRetrieval),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PublishMarkdownPageAsync_AggregatorThrows_DoesNotPropagate()
    {
        // AC2 defence-in-depth: a publisher fault must never escape to
        // the response path (the response is already written by this
        // point).
        _aggregator
            .PublishAsync(Arg.Any<MarkdownPageRequestedNotification>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        var ctx = NewContext();
        Assert.DoesNotThrowAsync(async () => await _publisher.PublishMarkdownPageAsync(
            ctx, "/p", Guid.NewGuid(), "en", CancellationToken.None));
    }

    [Test]
    public void ExtractReferrerHost_NullOrEmpty_ReturnsNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DefaultLlmsNotificationPublisher.ExtractReferrerHost(null), Is.Null);
            Assert.That(DefaultLlmsNotificationPublisher.ExtractReferrerHost(""), Is.Null);
            Assert.That(DefaultLlmsNotificationPublisher.ExtractReferrerHost("   "), Is.Null);
        });
    }

    [Test]
    public void ExtractReferrerHost_AbsoluteUrl_ReturnsHostOnly_NoPathOrQuery()
    {
        // Pin PII discipline: the helper must NEVER carry path / query /
        // fragment back into the notification payload.
        Assert.That(
            DefaultLlmsNotificationPublisher.ExtractReferrerHost(
                "https://google.com/search?q=ai+blog&secret=foo#frag"),
            Is.EqualTo("google.com"));
    }

    [Test]
    public void ExtractReferrerHost_MalformedUrl_ReturnsNull()
    {
        Assert.That(
            DefaultLlmsNotificationPublisher.ExtractReferrerHost("not a url at all"),
            Is.Null);
    }
}
