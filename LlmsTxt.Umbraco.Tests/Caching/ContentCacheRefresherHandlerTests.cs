using LlmsTxt.Umbraco.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Sync;

namespace LlmsTxt.Umbraco.Tests.Caching;

[TestFixture]
public class ContentCacheRefresherHandlerTests
{
    private static readonly Guid Root = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Child1 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Child2 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid Stranger = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private const string TestHost = "test.example";

    private LlmsCacheKeyIndex _index = null!;
    private AppCaches _appCaches = null!;
    private IDocumentNavigationQueryService _navigation = null!;
    private IDomainService _domainService = null!;

    [SetUp]
    public void Setup()
    {
        _index = new LlmsCacheKeyIndex();
        _appCaches = new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache()));
        _navigation = Substitute.For<IDocumentNavigationQueryService>();
        _domainService = Substitute.For<IDomainService>();
        // Default — no domains bound. Tests opting into manifest invalidation
        // override this in their own arrangement.
        _domainService.GetAll(includeWildcards: true).Returns(Array.Empty<global::Umbraco.Cms.Core.Models.IDomain>());
    }

    [TearDown]
    public void TearDown() => _appCaches.Dispose();

    [Test]
    public async Task RefreshAll_NotificationType_ClearsCacheAndIndex()
    {
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var notification = new ContentCacheRefresherNotification(
            messageObject: Array.Empty<ContentCacheRefresher.JsonPayload>(),
            messageType: MessageType.RefreshAll);

        await handler.HandleAsync(notification, CancellationToken.None);

        AssertCacheCleared(Root);
        Assert.That(_index.GetKeysFor(Root), Is.Empty);
    }

    [Test]
    public async Task RefreshAll_PayloadFlag_ClearsCacheAndIndex()
    {
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshAll,
        };
        var notification = new ContentCacheRefresherNotification(
            messageObject: new[] { payload },
            messageType: MessageType.RefreshByJson);

        await handler.HandleAsync(notification, CancellationToken.None);

        AssertCacheCleared(Root);
        Assert.That(_index.GetKeysFor(Root), Is.Empty);
    }

    [Test]
    public async Task RefreshNode_KnownNodeKey_ClearsCachedKeys_RemovesIndexEntry()
    {
        SeedCacheAndIndex(Root);
        SeedCacheAndIndex(Stranger); // unrelated entry, must survive
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        AssertCacheCleared(Root);
        Assert.That(_index.GetKeysFor(Root), Is.Empty);
        AssertCacheStillPresent(Stranger);
        Assert.That(_index.GetKeysFor(Stranger), Is.Not.Empty);
    }

    [Test]
    public async Task RefreshNode_UnknownNodeKey_NoOp_NoError()
    {
        // Architecture edge case: adopter publishes a node we never cached.
        SeedCacheAndIndex(Stranger);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root, // not in index — prefix-clear is a no-op
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        // Stranger's entry survives — handler made no destructive moves.
        AssertCacheStillPresent(Stranger);
    }

    [Test]
    public async Task RefreshNode_PrefixClear_ClearsEntriesNotInIndex()
    {
        // Spec § Failure & Edge Cases: a cache entry written between the decorator's
        // RuntimeCache.Get and its post-Register call must still be invalidated when
        // a publish for that node arrives. The handler clears by `llms:page:{nodeKey:N}:`
        // prefix, so an in-flight cache write that hasn't yet reached the index is
        // still cleared.
        var orphanKey = LlmsCacheKeys.Page(Root, TestHost, "en-GB");
        _appCaches.RuntimeCache.Insert(orphanKey, () => "cached", TimeSpan.FromMinutes(5));
        // Note: NOT registered in the index — simulates the race window.

        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        Assert.That(_appCaches.RuntimeCache.Get(orphanKey), Is.Null,
            "prefix-clear must invalidate cache entries that aren't yet in the index");
    }

    [Test]
    public async Task RefreshBranch_KnownRoot_ClearsRootAndDescendants()
    {
        SeedCacheAndIndex(Root);
        SeedCacheAndIndex(Child1);
        SeedCacheAndIndex(Child2);
        SeedCacheAndIndex(Stranger); // unrelated, survives

        IEnumerable<Guid> descendants = new[] { Child1, Child2 };
        _navigation
            .TryGetDescendantsKeys(Root, out Arg.Any<IEnumerable<Guid>>()!)
            .Returns(call =>
            {
                call[1] = descendants;
                return true;
            });

        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshBranch,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        AssertCacheCleared(Root);
        AssertCacheCleared(Child1);
        AssertCacheCleared(Child2);
        AssertCacheStillPresent(Stranger);
    }

    [Test]
    public async Task RefreshBranch_RootNotInNavigation_ClearsRootOnly()
    {
        SeedCacheAndIndex(Root);
        SeedCacheAndIndex(Child1);

        _navigation
            .TryGetDescendantsKeys(Root, out Arg.Any<IEnumerable<Guid>>()!)
            .Returns(false);

        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshBranch,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        AssertCacheCleared(Root);
        // Child1 still cached because we couldn't find descendants.
        AssertCacheStillPresent(Child1);
    }

    [Test]
    public async Task Remove_KnownNodeKey_BehavesLikeRefreshNode()
    {
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.Remove,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        AssertCacheCleared(Root);
        Assert.That(_index.GetKeysFor(Root), Is.Empty);
    }

    [Test]
    public async Task MalformedPayload_DoesNotClearAll()
    {
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var notification = new ContentCacheRefresherNotification(
            messageObject: "garbage string", // not a JsonPayload[]
            messageType: MessageType.RefreshByJson);

        await handler.HandleAsync(notification, CancellationToken.None);

        // Skip rather than clear-all on malformed input — handler must NOT punish
        // the whole instance for one upstream tooling bug.
        AssertCacheStillPresent(Root);
        Assert.That(_index.GetKeysFor(Root), Is.Not.Empty);
    }

    [Test]
    public async Task OnePayloadThrows_OtherPayloadsStillProcessed()
    {
        SeedCacheAndIndex(Root);
        SeedCacheAndIndex(Child1);

        // First payload triggers a navigation call that throws; second payload
        // is a normal RefreshNode that should still process.
        _navigation
            .When(n => n.TryGetDescendantsKeys(Root, out Arg.Any<IEnumerable<Guid>>()!))
            .Do(_ => throw new InvalidOperationException("nav blew up"));

        var handler = MakeHandler();
        var notification = new ContentCacheRefresherNotification(
            messageObject: new[]
            {
                new ContentCacheRefresher.JsonPayload
                {
                    Key = Root,
                    ChangeTypes = TreeChangeTypes.RefreshBranch,
                },
                new ContentCacheRefresher.JsonPayload
                {
                    Key = Child1,
                    ChangeTypes = TreeChangeTypes.RefreshNode,
                },
            },
            messageType: MessageType.RefreshByJson);

        await handler.HandleAsync(notification, CancellationToken.None);

        // First payload's exception was caught — Child1 still got processed.
        AssertCacheCleared(Child1);
    }

    [Test]
    public async Task PayloadWithNoKey_NoOp()
    {
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = null, // missing Guid
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        // No-op when no Guid available to look up.
        AssertCacheStillPresent(Root);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 2.1 — manifest cache invalidation
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RefreshNode_ClearsManifestForBoundHostnames()
    {
        // Two hosts bound — both manifests are dropped after a single per-node
        // payload (pessimistic clear, since the changed node may belong to either tree).
        SeedManifestCache("sitea.example", "en-gb");
        SeedManifestCache("siteb.example", "en-gb");
        SeedDomains("sitea.example", "siteb.example");
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        AssertManifestCleared("sitea.example", "en-gb");
        AssertManifestCleared("siteb.example", "en-gb");
    }

    [Test]
    public async Task RefreshNode_ClearsAllCulturesForAffectedHostname()
    {
        // Manifest cache is keyed by host:culture; a single ClearByKey of the host
        // prefix must drop entries across cultures.
        SeedManifestCache("sitea.example", "en-gb");
        SeedManifestCache("sitea.example", "fr-fr");
        SeedDomains("sitea.example");
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        AssertManifestCleared("sitea.example", "en-gb");
        AssertManifestCleared("sitea.example", "fr-fr");
    }

    [Test]
    public async Task RefreshAll_AlsoClearsManifestCache()
    {
        // ClearAll drops the whole `llms:` prefix, which covers manifests too.
        SeedManifestCache("sitea.example", "en-gb");
        SeedDomains("sitea.example");
        var handler = MakeHandler();
        var notification = new ContentCacheRefresherNotification(
            messageObject: Array.Empty<ContentCacheRefresher.JsonPayload>(),
            messageType: MessageType.RefreshAll);

        await handler.HandleAsync(notification, CancellationToken.None);

        AssertManifestCleared("sitea.example", "en-gb");
    }

    [Test]
    public async Task NoDomainsBound_ManifestInvalidationIsNoOp()
    {
        // Defensive: ClearByKey on a missing prefix is a no-op; the host-walk just
        // produces zero clears. Confirms we don't blow up on a single-site default install.
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        // No SeedDomains call — IDomainService returns empty per default Setup.
        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        // No exception; per-node prefix-clear still ran.
        AssertCacheCleared(Root);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 2.2 — /llms-full.txt manifest invalidation (parallel to /llms.txt)
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PerNodePayload_ClearsLlmsFullForBoundHostnames()
    {
        SeedFullManifestCache("sitea.example", "en-gb");
        SeedFullManifestCache("siteb.example", "en-gb");
        SeedDomains("sitea.example", "siteb.example");
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        AssertFullManifestCleared("sitea.example", "en-gb");
        AssertFullManifestCleared("siteb.example", "en-gb");
    }

    [Test]
    public async Task RefreshAll_AlsoClearsLlmsFullManifestCache()
    {
        SeedFullManifestCache("sitea.example", "en-gb");
        SeedDomains("sitea.example");
        var handler = MakeHandler();
        var notification = new ContentCacheRefresherNotification(
            messageObject: Array.Empty<ContentCacheRefresher.JsonPayload>(),
            messageType: MessageType.RefreshAll);

        await handler.HandleAsync(notification, CancellationToken.None);

        AssertFullManifestCleared("sitea.example", "en-gb");
    }

    [Test]
    public async Task PerNodePayload_ClearsBothLlmsTxtAndLlmsFullPrefixes()
    {
        // Single per-node payload must drop both manifest namespaces in one pass.
        SeedManifestCache("sitea.example", "en-gb");
        SeedFullManifestCache("sitea.example", "en-gb");
        SeedDomains("sitea.example");
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        AssertManifestCleared("sitea.example", "en-gb");
        AssertFullManifestCleared("sitea.example", "en-gb");
    }

    [Test]
    public async Task WildcardDomainBinding_LlmsFull_TtlOnlyInvalidation_DocumentedTradeoff()
    {
        // Same trade-off as the /llms.txt wildcard test (deferred-work.md § Story 2.1
        // first bullet) — wildcards have no concrete request host to invalidate by,
        // so subdomain manifests cached under llms:llmsfull:foo.example.com:* survive
        // until TTL.
        SeedFullManifestCache("foo.example.com", "en-gb");
        SeedDomainsRaw(Domain("*.example.com"));
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        AssertFullManifestStillPresent("foo.example.com", "en-gb");
    }

    [Test]
    public async Task WildcardDomainBinding_NoConcreteHostInvalidation()
    {
        // Wildcard ("*.example.com") doesn't have a concrete request host to invalidate
        // by — matching subdomains will cache against their own host segment, so they
        // get cleared when their own subdomain's request comes through later. The
        // handler must skip wildcards without erroring.
        SeedManifestCache("foo.example.com", "en-gb");
        SeedDomainsRaw(Domain("*.example.com"));
        SeedCacheAndIndex(Root);
        var handler = MakeHandler();
        var payload = new ContentCacheRefresher.JsonPayload
        {
            Key = Root,
            ChangeTypes = TreeChangeTypes.RefreshNode,
        };

        await handler.HandleAsync(BuildNotification(payload), CancellationToken.None);

        // The seeded foo.example.com manifest entry is NOT cleared because no
        // concrete IDomain matches it. Acceptable trade-off documented in the
        // handler's xmldoc.
        AssertManifestStillPresent("foo.example.com", "en-gb");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private ContentCacheRefresherHandler MakeHandler()
        => new(
            _appCaches,
            _index,
            _navigation,
            _domainService,
            NullLogger<ContentCacheRefresherHandler>.Instance);

    private static ContentCacheRefresherNotification BuildNotification(ContentCacheRefresher.JsonPayload payload)
        => new(
            messageObject: new[] { payload },
            messageType: MessageType.RefreshByJson);

    private void SeedCacheAndIndex(Guid nodeKey)
    {
        // Use the realistic page-cache key shape (Story 1.5: includes host) so the
        // handler's prefix-clear (`llms:page:{nodeKey:N}:`) actually exercises the
        // prefix matcher. nodeKey stays as the second segment so per-host entries
        // for the same node all share the prefix and get cleared together.
        var key = LlmsCacheKeys.Page(nodeKey, TestHost, "en-GB");
        _appCaches.RuntimeCache.Insert(key, () => "cached", TimeSpan.FromMinutes(5));
        _index.Register(nodeKey, key);
    }

    private void AssertCacheCleared(Guid nodeKey)
    {
        var key = LlmsCacheKeys.Page(nodeKey, TestHost, "en-GB");
        Assert.That(_appCaches.RuntimeCache.Get(key), Is.Null,
            $"expected cache key '{key}' to have been cleared");
    }

    private void AssertCacheStillPresent(Guid nodeKey)
    {
        var key = LlmsCacheKeys.Page(nodeKey, TestHost, "en-GB");
        Assert.That(_appCaches.RuntimeCache.Get(key), Is.Not.Null,
            $"expected cache key '{key}' to still be present");
    }

    private void SeedManifestCache(string host, string culture)
    {
        var key = LlmsCacheKeys.LlmsTxt(host, culture);
        _appCaches.RuntimeCache.Insert(key, () => $"manifest-{host}-{culture}", TimeSpan.FromMinutes(5));
    }

    private void AssertManifestCleared(string host, string culture)
    {
        var key = LlmsCacheKeys.LlmsTxt(host, culture);
        Assert.That(_appCaches.RuntimeCache.Get(key), Is.Null,
            $"expected manifest cache key '{key}' to have been cleared");
    }

    private void AssertManifestStillPresent(string host, string culture)
    {
        var key = LlmsCacheKeys.LlmsTxt(host, culture);
        Assert.That(_appCaches.RuntimeCache.Get(key), Is.Not.Null,
            $"expected manifest cache key '{key}' to still be present");
    }

    private void SeedFullManifestCache(string host, string culture)
    {
        var key = LlmsCacheKeys.LlmsFull(host, culture);
        _appCaches.RuntimeCache.Insert(key, () => $"full-manifest-{host}-{culture}", TimeSpan.FromMinutes(5));
    }

    private void AssertFullManifestCleared(string host, string culture)
    {
        var key = LlmsCacheKeys.LlmsFull(host, culture);
        Assert.That(_appCaches.RuntimeCache.Get(key), Is.Null,
            $"expected /llms-full.txt cache key '{key}' to have been cleared");
    }

    private void AssertFullManifestStillPresent(string host, string culture)
    {
        var key = LlmsCacheKeys.LlmsFull(host, culture);
        Assert.That(_appCaches.RuntimeCache.Get(key), Is.Not.Null,
            $"expected /llms-full.txt cache key '{key}' to still be present");
    }

    private void SeedDomains(params string[] hostnames)
    {
        var domains = hostnames.Select(h => Domain(h)).ToArray();
        SeedDomainsRaw(domains);
    }

    private void SeedDomainsRaw(params global::Umbraco.Cms.Core.Models.IDomain[] domains)
    {
        _domainService.GetAll(includeWildcards: true).Returns(domains);
    }

    private static global::Umbraco.Cms.Core.Models.IDomain Domain(string domainName)
    {
        var d = Substitute.For<global::Umbraco.Cms.Core.Models.IDomain>();
        d.DomainName.Returns(domainName);
        d.RootContentId.Returns((int?)42);
        d.LanguageIsoCode.Returns("en-GB");
        return d;
    }
}
