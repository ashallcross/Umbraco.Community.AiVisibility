using Umbraco.Community.AiVisibility.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Sync;

namespace Umbraco.Community.AiVisibility.Tests.Caching;

[TestFixture]
public class PublicAccessCacheRefresherHandlerTests
{
    private const string Host = "sitea.example";
    private static readonly Guid PageKey = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private AppCaches _appCaches = null!;

    [SetUp]
    public void Setup()
    {
        _appCaches = new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache()));
    }

    [TearDown]
    public void TearDown() => _appCaches.Dispose();

    private PublicAccessCacheRefresherHandler MakeHandler(ILogger<PublicAccessCacheRefresherHandler>? logger = null)
        => new(_appCaches, logger ?? NullLogger<PublicAccessCacheRefresherHandler>.Instance);

    private void Seed(string key)
        => _appCaches.RuntimeCache.Insert(key, () => "cached", TimeSpan.FromMinutes(5));

    [TestCase(MessageType.RefreshAll)]
    [TestCase(MessageType.RefreshById)]
    [TestCase(MessageType.RemoveById)]
    public async Task HandleAsync_ClearsManifestAndSettingsPrefixes(MessageType messageType)
    {
        var llmsTxt = AiVisibilityCacheKeys.LlmsTxt(Host, "en-gb");
        var llmsFull = AiVisibilityCacheKeys.LlmsFull(Host, "en-gb");
        var settings = AiVisibilityCacheKeys.Settings("en-gb");
        Seed(llmsTxt);
        Seed(llmsFull);
        Seed(settings);

        await MakeHandler().HandleAsync(
            new PublicAccessCacheRefresherNotification(1234, messageType),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_appCaches.RuntimeCache.Get(llmsTxt), Is.Null, "/llms.txt manifest cleared");
            Assert.That(_appCaches.RuntimeCache.Get(llmsFull), Is.Null, "/llms-full.txt manifest cleared");
            Assert.That(_appCaches.RuntimeCache.Get(settings), Is.Null, "resolved settings cleared");
        });
    }

    [Test]
    public async Task HandleAsync_LeavesPerPageAndRobotsEntriesAlone()
    {
        // Per-page `.md` evaluates exclusion live per request, so its cache must
        // survive a Public Access change; robots audit is unrelated.
        var page = AiVisibilityCacheKeys.Page(PageKey, Host, "en-gb");
        var robots = AiVisibilityCacheKeys.Robots(Host);
        Seed(page);
        Seed(robots);
        Seed(AiVisibilityCacheKeys.LlmsTxt(Host, "en-gb"));

        await MakeHandler().HandleAsync(
            new PublicAccessCacheRefresherNotification(1234, MessageType.RefreshById),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_appCaches.RuntimeCache.Get(page), Is.Not.Null, "aiv:page: entry untouched");
            Assert.That(_appCaches.RuntimeCache.Get(robots), Is.Not.Null, "robots entry untouched");
        });
    }

    [Test]
    public void HandleAsync_ClearThrows_LogsErrorAndDoesNotRethrow()
    {
        var runtimeCache = Substitute.For<IAppPolicyCache>();
        runtimeCache.When(c => c.ClearByKey(Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("boom"));
        var appCaches = new AppCaches(
            runtimeCache,
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache()));
        var logger = Substitute.For<ILogger<PublicAccessCacheRefresherHandler>>();
        var handler = new PublicAccessCacheRefresherHandler(appCaches, logger);

        Assert.DoesNotThrowAsync(() => handler.HandleAsync(
            new PublicAccessCacheRefresherNotification(1234, MessageType.RefreshById),
            CancellationToken.None));

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception?>(e => e is InvalidOperationException),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void Compose_StartupValidation_PublicAccessCacheRefresherHandler_NoCaptiveDependency()
    {
        // Umbraco's AddNotificationAsyncHandler (used by CachingComposer)
        // registers the handler as a transient INotificationAsyncHandler<T>;
        // AppCaches is a singleton.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_appCaches);
        services.AddTransient<
            INotificationAsyncHandler<PublicAccessCacheRefresherNotification>,
            PublicAccessCacheRefresherHandler>();

        Assert.DoesNotThrow(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            var handler = provider
                .GetRequiredService<INotificationAsyncHandler<PublicAccessCacheRefresherNotification>>();
            Assert.That(handler, Is.InstanceOf<PublicAccessCacheRefresherHandler>());
        });
    }
}
