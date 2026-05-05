using LlmsTxt.Umbraco.Caching;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace LlmsTxt.Umbraco.Tests.Caching;

[TestFixture]
public class CachingMarkdownExtractorDecoratorTests
{
    private static readonly Guid NodeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid NodeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime UpdatedUtc = new(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string TestHost = "test.example";

    private LlmsCacheKeyIndex _index = null!;
    private AppCaches _appCaches = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IOptionsMonitor<LlmsTxtSettings> _settings = null!;

    [SetUp]
    public void Setup()
    {
        _index = new LlmsCacheKeyIndex();
        _appCaches = new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache()));
        _httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { Request = { Host = new HostString(TestHost) } },
        };
        _settings = Substitute.For<IOptionsMonitor<LlmsTxtSettings>>();
        _settings.CurrentValue.Returns(new LlmsTxtSettings { CachePolicySeconds = 60 });
    }

    [TearDown]
    public void TearDown() => _appCaches.Dispose();

    [Test]
    public async Task ExtractAsync_CacheMiss_InvokesInner_AndCachesResult()
    {
        var inner = new CountingExtractor(BuildFound("md1"));
        var decorator = MakeDecorator(inner);
        var content = StubContent(NodeA);

        var first = await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);
        var second = await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);

        Assert.That(first.Status, Is.EqualTo(MarkdownExtractionStatus.Found));
        Assert.That(second.Markdown, Is.EqualTo(first.Markdown));
        Assert.That(inner.CallCount, Is.EqualTo(1), "inner extractor must be invoked exactly once across miss + hit");
    }

    [Test]
    public async Task ExtractAsync_CacheMiss_RegistersIndexEntry()
    {
        var inner = new CountingExtractor(BuildFound("md"));
        var decorator = MakeDecorator(inner);
        var content = StubContent(NodeA);

        await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);

        var key = LlmsCacheKeys.Page(NodeA, TestHost, "en-GB");
        Assert.That(_index.GetKeysFor(NodeA), Contains.Item(key));
    }

    [Test]
    public async Task ExtractAsync_CacheHit_RegistersIndexEntry_Idempotent()
    {
        var inner = new CountingExtractor(BuildFound("md"));
        var decorator = MakeDecorator(inner);
        var content = StubContent(NodeA);

        await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);
        await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);
        await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);

        // HashSet semantics — same key registered three times collapses to one entry.
        Assert.That(_index.GetKeysFor(NodeA).Count, Is.EqualTo(1));
    }

    [Test]
    public async Task ExtractAsync_ErrorResult_NotCached()
    {
        var error = MarkdownExtractionResult.Failed(
            new InvalidOperationException("transient"),
            sourceUrl: null,
            contentKey: NodeA);
        var inner = new CountingExtractor(error);
        var decorator = MakeDecorator(inner);
        var content = StubContent(NodeA);

        var first = await decorator.ExtractAsync(content, null, CancellationToken.None);
        var second = await decorator.ExtractAsync(content, null, CancellationToken.None);

        Assert.That(first.Status, Is.EqualTo(MarkdownExtractionStatus.Error));
        Assert.That(second.Status, Is.EqualTo(MarkdownExtractionStatus.Error));
        Assert.That(inner.CallCount, Is.EqualTo(2), "Error results must NOT be cached — re-render on next request");
    }

    [Test]
    public async Task ExtractAsync_ErrorResult_DoesNotRegisterIndexEntry()
    {
        var error = MarkdownExtractionResult.Failed(
            new InvalidOperationException("transient"),
            sourceUrl: null,
            contentKey: NodeA);
        var inner = new CountingExtractor(error);
        var decorator = MakeDecorator(inner);

        await decorator.ExtractAsync(StubContent(NodeA), null, CancellationToken.None);

        Assert.That(_index.GetKeysFor(NodeA), Is.Empty);
    }

    [Test]
    public async Task ExtractAsync_DifferentCultures_DifferentCacheKeys()
    {
        var inner = new CountingExtractor(BuildFound("md"));
        var decorator = MakeDecorator(inner);
        var content = StubContent(NodeA);

        await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);
        await decorator.ExtractAsync(content, "fr-FR", CancellationToken.None);

        Assert.That(inner.CallCount, Is.EqualTo(2), "different cultures key independently → two factory invocations");
        Assert.That(_index.GetKeysFor(NodeA).Count, Is.EqualTo(2));
    }

    [Test]
    public async Task ExtractAsync_DifferentNodes_DifferentCacheKeys()
    {
        var inner = new CountingExtractor(BuildFound("md"));
        var decorator = MakeDecorator(inner);

        await decorator.ExtractAsync(StubContent(NodeA), "en-GB", CancellationToken.None);
        await decorator.ExtractAsync(StubContent(NodeB), "en-GB", CancellationToken.None);

        Assert.That(inner.CallCount, Is.EqualTo(2));
        Assert.That(_index.GetKeysFor(NodeA), Is.Not.Empty);
        Assert.That(_index.GetKeysFor(NodeB), Is.Not.Empty);
    }

    [Test]
    public async Task ExtractAsync_NullCulture_KeysAsInvariant_AndIsCached()
    {
        var inner = new CountingExtractor(BuildFound("md"));
        var decorator = MakeDecorator(inner);
        var content = StubContent(NodeA);

        await decorator.ExtractAsync(content, null, CancellationToken.None);
        await decorator.ExtractAsync(content, null, CancellationToken.None);

        Assert.That(inner.CallCount, Is.EqualTo(1));
        Assert.That(_index.GetKeysFor(NodeA), Contains.Item(LlmsCacheKeys.Page(NodeA, TestHost, null)));
    }

    [Test, CancelAfter(5000)]
    public async Task ExtractAsync_ConcurrentMiss_SingleFlight_InvokesInnerOnce()
    {
        // 50 concurrent requests for the same (nodeKey, culture); only one should
        // run the inner extractor. The factory blocks for ~100ms to widen the race
        // window. IAppPolicyCache.Get serialises factory invocations per key.
        var gate = new TaskCompletionSource();
        var counter = 0;
        var inner = new BlockingExtractor(BuildFound("md"), () =>
        {
            Interlocked.Increment(ref counter);
            gate.Task.GetAwaiter().GetResult(); // hold all parked threads on the lock
        });
        var decorator = MakeDecorator(inner);
        var content = StubContent(NodeA);

        var tasks = Enumerable
            .Range(0, 50)
            .Select(_ => Task.Run(() => decorator.ExtractAsync(content, "en-GB", CancellationToken.None)))
            .ToArray();

        // Give the workers ~50ms to all park on the cache lock.
        await Task.Delay(50);
        gate.SetResult();

        await Task.WhenAll(tasks);

        Assert.That(counter, Is.EqualTo(1), "single-flight: inner extractor must be invoked exactly once");
    }

    [Test]
    public void ExtractAsync_CancellationDuringFactory_PropagatesOperationCanceledException()
    {
        var inner = new CancelingExtractor();
        var decorator = MakeDecorator(inner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await decorator.ExtractAsync(StubContent(NodeA), "en-GB", cts.Token));
    }

    [Test]
    public async Task Settings_CachePolicySeconds_Zero_ExtractRunsOnEveryRequest()
    {
        // Spec § Failure & Edge Cases: `CachePolicySeconds = 0` is a valid disable mechanism.
        // Umbraco's ObjectCacheAppCache treats TimeSpan.Zero as immediate eviction, so the
        // factory runs on every request — adopters who set 0 get cache-disabled behaviour
        // for free without us shipping a separate "caching disabled" flag.
        _settings.CurrentValue.Returns(new LlmsTxtSettings { CachePolicySeconds = 0 });
        var inner = new CountingExtractor(BuildFound("md"));
        var decorator = MakeDecorator(inner);
        var content = StubContent(NodeA);

        await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);
        await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);
        await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);

        Assert.That(inner.CallCount, Is.EqualTo(3),
            "TTL=0 must disable caching — inner extractor invoked once per request");
    }

    [Test]
    public async Task ExtractAsync_FirstCallerCancelled_DoesNotPoisonOtherCallers()
    {
        // Regression guard: the first caller's CancellationToken must NOT be threaded
        // into the inner extractor inside the cache-factory delegate, otherwise a
        // cancellation from the first arrived caller would propagate as
        // OperationCanceledException to every other caller parked on the cache lock —
        // even those whose own tokens are still valid.
        var inner = new CountingExtractor(BuildFound("md"));
        var decorator = MakeDecorator(inner);
        var content = StubContent(NodeA);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // The cancelled caller throws (outer ThrowIfCancellationRequested before Get).
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await decorator.ExtractAsync(content, "en-GB", cancelled.Token));

        // A subsequent caller with a valid token completes successfully — proving the
        // factory delegate did not capture the previous caller's token.
        var subsequent = await decorator.ExtractAsync(content, "en-GB", CancellationToken.None);
        Assert.That(subsequent.Status, Is.EqualTo(MarkdownExtractionStatus.Found));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private CachingMarkdownExtractorDecorator MakeDecorator(IMarkdownContentExtractor inner)
        => new(inner, _appCaches, _index, _httpContextAccessor, _settings, NullLogger<CachingMarkdownExtractorDecorator>.Instance);

    private static MarkdownExtractionResult BuildFound(string body) =>
        MarkdownExtractionResult.Found(
            markdown: body,
            contentKey: NodeA,
            culture: "en-GB",
            updatedUtc: UpdatedUtc,
            sourceUrl: "https://example.test/x");

    private static IPublishedContent StubContent(Guid key)
    {
        var c = Substitute.For<IPublishedContent>();
        c.Key.Returns(key);
        c.Name.Returns("Test");
        c.UpdateDate.Returns(UpdatedUtc);
        return c;
    }

    private sealed class CountingExtractor : IMarkdownContentExtractor
    {
        private readonly MarkdownExtractionResult _result;
        public int CallCount { get; private set; }
        public CountingExtractor(MarkdownExtractionResult result) { _result = result; }

        public Task<MarkdownExtractionResult> ExtractAsync(
            IPublishedContent content, string? culture, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class BlockingExtractor : IMarkdownContentExtractor
    {
        private readonly MarkdownExtractionResult _result;
        private readonly Action _onCall;
        public BlockingExtractor(MarkdownExtractionResult result, Action onCall)
        {
            _result = result;
            _onCall = onCall;
        }

        public Task<MarkdownExtractionResult> ExtractAsync(
            IPublishedContent content, string? culture, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _onCall();
            return Task.FromResult(_result);
        }
    }

    private sealed class CancelingExtractor : IMarkdownContentExtractor
    {
        public Task<MarkdownExtractionResult> ExtractAsync(
            IPublishedContent content, string? culture, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(MarkdownExtractionResult.Failed(
                new InvalidOperationException("unreachable"),
                sourceUrl: null,
                contentKey: content.Key));
        }
    }
}
