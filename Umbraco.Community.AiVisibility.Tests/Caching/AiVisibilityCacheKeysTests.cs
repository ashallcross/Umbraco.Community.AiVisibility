using Umbraco.Community.AiVisibility.Caching;

namespace Umbraco.Community.AiVisibility.Tests.Caching;

[TestFixture]
public class CacheKeysTests
{
    private static readonly Guid Node = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string Host = "sitea.example";

    [Test]
    public void Page_WithCulture_FormatsAsLowercaseN()
    {
        var key = AiVisibilityCacheKeys.Page(Node, Host, "en-GB");
        Assert.That(key, Is.EqualTo("aiv:page:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:sitea.example:en-gb"));
    }

    [Test]
    public void Page_WithUppercaseCulture_NormalisesToLowercase()
    {
        var lower = AiVisibilityCacheKeys.Page(Node, Host, "en-GB");
        var upper = AiVisibilityCacheKeys.Page(Node, Host, "EN-gb");
        Assert.That(upper, Is.EqualTo(lower));
    }

    [Test]
    public void Page_WithNullCulture_UsesInvariantSentinel()
    {
        var key = AiVisibilityCacheKeys.Page(Node, Host, null);
        Assert.That(key, Is.EqualTo("aiv:page:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:sitea.example:_"));
    }

    [Test]
    public void Page_WithEmptyCulture_UsesInvariantSentinel()
    {
        Assert.That(
            AiVisibilityCacheKeys.Page(Node, Host, string.Empty),
            Is.EqualTo(AiVisibilityCacheKeys.Page(Node, Host, null)));
    }

    [Test]
    public void Page_StartsWithPagePrefix()
    {
        Assert.That(AiVisibilityCacheKeys.Page(Node, Host, "en-GB"), Does.StartWith(AiVisibilityCacheKeys.PagePrefix));
    }

    [Test]
    public void Page_StartsWithGlobalPrefix()
    {
        // ClearByKey(Prefix) must cover every key shape we ever issue.
        Assert.That(AiVisibilityCacheKeys.Page(Node, Host, "en-GB"), Does.StartWith(AiVisibilityCacheKeys.Prefix));
    }

    [Test]
    public void Page_NodeKeyImmediatelyFollowsPagePrefix()
    {
        // Story 1.5: nodeKey stays as the second segment so the handler's
        // race-mitigating prefix-clear `llms:page:{nodeKey:N}:` still finds
        // and clears every per-host entry for that node.
        var key = AiVisibilityCacheKeys.Page(Node, Host, "en-GB");
        Assert.That(key, Does.StartWith($"{AiVisibilityCacheKeys.PagePrefix}{Node:N}:"));
    }

    [Test]
    public void Page_DifferentNode_DifferentKey()
    {
        var a = AiVisibilityCacheKeys.Page(Node, Host, "en-GB");
        var b = AiVisibilityCacheKeys.Page(Guid.NewGuid(), Host, "en-GB");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void Page_DifferentHost_DifferentKey()
    {
        var a = AiVisibilityCacheKeys.Page(Node, "sitea.example", "en-GB");
        var b = AiVisibilityCacheKeys.Page(Node, "siteb.example", "en-GB");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void Page_UppercaseHost_NormalisesToLowercase()
    {
        var lower = AiVisibilityCacheKeys.Page(Node, "sitea.example", "en-GB");
        var upper = AiVisibilityCacheKeys.Page(Node, "SiteA.Example", "en-GB");
        Assert.That(upper, Is.EqualTo(lower));
    }

    [Test]
    public void Page_WithNullHost_UsesInvariantSentinel()
    {
        var key = AiVisibilityCacheKeys.Page(Node, null, "en-GB");
        Assert.That(key, Is.EqualTo("aiv:page:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:_:en-gb"));
    }

    [Test]
    public void Page_WithEmptyHost_UsesInvariantSentinel()
    {
        Assert.That(
            AiVisibilityCacheKeys.Page(Node, string.Empty, "en-GB"),
            Is.EqualTo(AiVisibilityCacheKeys.Page(Node, null, "en-GB")));
    }

    [Test]
    public void Page_WithWhitespaceHost_UsesInvariantSentinel()
    {
        // Story 1.5 review: defensive against malformed Host headers / misbehaving
        // clients. IsNullOrWhiteSpace routes a single-space host to the "_" sentinel
        // rather than allowing " " to become a distinct cache-key segment.
        Assert.That(
            AiVisibilityCacheKeys.Page(Node, "   ", "en-GB"),
            Is.EqualTo(AiVisibilityCacheKeys.Page(Node, null, "en-GB")));
    }

    [Test]
    public void Page_HostWithPort_StripsPort()
    {
        // Story 1.5 review: NormaliseHost is public and may be called from future
        // Epic 2 manifest builders that pull host strings from IDomain entries that
        // include port. The helper must produce the same key regardless of whether
        // the caller passes "sitea.example" or "sitea.example:443".
        var withoutPort = AiVisibilityCacheKeys.Page(Node, "sitea.example", "en-GB");
        var withPort = AiVisibilityCacheKeys.Page(Node, "sitea.example:443", "en-GB");
        Assert.That(withPort, Is.EqualTo(withoutPort));
    }

    [Test]
    public void Page_HostWithPortOnly_UsesInvariantSentinel()
    {
        // Edge case: a malformed input like ":443" (port without host) strips to an
        // empty string, which must route to the "_" sentinel rather than colliding
        // with a missing-host entry under an empty segment.
        Assert.That(
            AiVisibilityCacheKeys.Page(Node, ":443", "en-GB"),
            Is.EqualTo(AiVisibilityCacheKeys.Page(Node, null, "en-GB")));
    }

    [Test]
    public void Page_DifferentCulture_DifferentKey()
    {
        var en = AiVisibilityCacheKeys.Page(Node, Host, "en-GB");
        var fr = AiVisibilityCacheKeys.Page(Node, Host, "fr-FR");
        Assert.That(fr, Is.Not.EqualTo(en));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 2.1 — /llms.txt manifest keys
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void LlmsTxt_DefaultHostAndCulture_FormatsCorrectly()
    {
        var key = AiVisibilityCacheKeys.LlmsTxt(Host, "en-GB");
        Assert.That(key, Is.EqualTo("aiv:llmstxt:sitea.example:en-gb"));
    }

    [Test]
    public void LlmsTxt_HostUppercased_NormalisedToLower()
    {
        var lower = AiVisibilityCacheKeys.LlmsTxt("sitea.example", "en-GB");
        var upper = AiVisibilityCacheKeys.LlmsTxt("SiteA.Example", "en-GB");
        Assert.That(upper, Is.EqualTo(lower));
    }

    [Test]
    public void LlmsTxt_HostWithPort_PortStripped()
    {
        var withoutPort = AiVisibilityCacheKeys.LlmsTxt("sitea.example", "en-GB");
        var withPort = AiVisibilityCacheKeys.LlmsTxt("sitea.example:443", "en-GB");
        Assert.That(withPort, Is.EqualTo(withoutPort));
    }

    [Test]
    public void LlmsTxt_NullHost_UsesUnderscore()
    {
        var key = AiVisibilityCacheKeys.LlmsTxt(null, "en-GB");
        Assert.That(key, Is.EqualTo("aiv:llmstxt:_:en-gb"));
    }

    [Test]
    public void LlmsTxt_NullCulture_UsesUnderscore()
    {
        var key = AiVisibilityCacheKeys.LlmsTxt(Host, null);
        Assert.That(key, Is.EqualTo("aiv:llmstxt:sitea.example:_"));
    }

    [Test]
    public void LlmsTxt_StartsWithGlobalPrefix()
    {
        Assert.That(AiVisibilityCacheKeys.LlmsTxt(Host, "en-GB"), Does.StartWith(AiVisibilityCacheKeys.Prefix));
    }

    [Test]
    public void LlmsTxt_StartsWithLlmsTxtPrefix()
    {
        Assert.That(AiVisibilityCacheKeys.LlmsTxt(Host, "en-GB"), Does.StartWith(AiVisibilityCacheKeys.LlmsTxtPrefix));
    }

    [Test]
    public void LlmsTxtHostPrefix_BuildsClearByKeyArgument()
    {
        // Pessimistic invalidation: ClearByKey(LlmsTxtHostPrefix(host)) must drop
        // every culture's manifest entry for that hostname.
        var prefix = AiVisibilityCacheKeys.LlmsTxtHostPrefix("sitea.example");
        Assert.Multiple(() =>
        {
            Assert.That(prefix, Is.EqualTo("aiv:llmstxt:sitea.example:"));
            Assert.That(AiVisibilityCacheKeys.LlmsTxt("sitea.example", "en-GB"), Does.StartWith(prefix));
            Assert.That(AiVisibilityCacheKeys.LlmsTxt("sitea.example", "fr-FR"), Does.StartWith(prefix));
            Assert.That(AiVisibilityCacheKeys.LlmsTxt("siteb.example", "en-GB"), Does.Not.StartWith(prefix));
        });
    }

    [Test]
    public void LlmsTxt_DifferentHost_DifferentKey()
    {
        var a = AiVisibilityCacheKeys.LlmsTxt("sitea.example", "en-GB");
        var b = AiVisibilityCacheKeys.LlmsTxt("siteb.example", "en-GB");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 2.2 — /llms-full.txt manifest keys (parallel to LlmsTxt cases)
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void LlmsFull_DefaultHostAndCulture_FormatsCorrectly()
    {
        var key = AiVisibilityCacheKeys.LlmsFull(Host, "en-GB");
        Assert.That(key, Is.EqualTo("aiv:llmsfull:sitea.example:en-gb"));
    }

    [Test]
    public void LlmsFull_HostUppercased_NormalisedToLower()
    {
        var lower = AiVisibilityCacheKeys.LlmsFull("sitea.example", "en-GB");
        var upper = AiVisibilityCacheKeys.LlmsFull("SiteA.Example", "en-GB");
        Assert.That(upper, Is.EqualTo(lower));
    }

    [Test]
    public void LlmsFull_HostWithPort_PortStripped()
    {
        var withoutPort = AiVisibilityCacheKeys.LlmsFull("sitea.example", "en-GB");
        var withPort = AiVisibilityCacheKeys.LlmsFull("sitea.example:443", "en-GB");
        Assert.That(withPort, Is.EqualTo(withoutPort));
    }

    [Test]
    public void LlmsFull_NullHost_UsesUnderscore()
    {
        var key = AiVisibilityCacheKeys.LlmsFull(null, "en-GB");
        Assert.That(key, Is.EqualTo("aiv:llmsfull:_:en-gb"));
    }

    [Test]
    public void LlmsFull_NullCulture_UsesUnderscore()
    {
        var key = AiVisibilityCacheKeys.LlmsFull(Host, null);
        Assert.That(key, Is.EqualTo("aiv:llmsfull:sitea.example:_"));
    }

    [Test]
    public void LlmsFull_StartsWithGlobalPrefix()
    {
        Assert.That(AiVisibilityCacheKeys.LlmsFull(Host, "en-GB"), Does.StartWith(AiVisibilityCacheKeys.Prefix));
    }

    [Test]
    public void LlmsFull_StartsWithLlmsFullPrefix()
    {
        Assert.That(AiVisibilityCacheKeys.LlmsFull(Host, "en-GB"), Does.StartWith(AiVisibilityCacheKeys.LlmsFullPrefix));
    }

    [Test]
    public void LlmsFull_NamespaceDoesNotCollideWithLlmsTxt()
    {
        // Architectural drift item #1 reconciled: architecture's no-hyphen shape
        // (`llms:llmsfull:`) must produce a strictly different prefix from
        // `llms:llmstxt:` so a single ClearByKey cannot accidentally drop both.
        Assert.Multiple(() =>
        {
            Assert.That(AiVisibilityCacheKeys.LlmsFullPrefix, Is.Not.EqualTo(AiVisibilityCacheKeys.LlmsTxtPrefix));
            Assert.That(AiVisibilityCacheKeys.LlmsFull(Host, "en-GB"), Does.Not.StartWith(AiVisibilityCacheKeys.LlmsTxtPrefix));
            Assert.That(AiVisibilityCacheKeys.LlmsTxt(Host, "en-GB"), Does.Not.StartWith(AiVisibilityCacheKeys.LlmsFullPrefix));
        });
    }

    [Test]
    public void LlmsFullHostPrefix_BuildsClearByKeyArgument()
    {
        var prefix = AiVisibilityCacheKeys.LlmsFullHostPrefix("sitea.example");
        Assert.Multiple(() =>
        {
            Assert.That(prefix, Is.EqualTo("aiv:llmsfull:sitea.example:"));
            Assert.That(AiVisibilityCacheKeys.LlmsFull("sitea.example", "en-GB"), Does.StartWith(prefix));
            Assert.That(AiVisibilityCacheKeys.LlmsFull("sitea.example", "fr-FR"), Does.StartWith(prefix));
            Assert.That(AiVisibilityCacheKeys.LlmsFull("siteb.example", "en-GB"), Does.Not.StartWith(prefix));
        });
    }

    [Test]
    public void LlmsFull_DifferentHost_DifferentKey()
    {
        var a = AiVisibilityCacheKeys.LlmsFull("sitea.example", "en-GB");
        var b = AiVisibilityCacheKeys.LlmsFull("siteb.example", "en-GB");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 3.1 — resolver settings cache keys
    // Host-independent (D1-A decision, code review 2026-04-30): one global
    // Settings node per install, so the key omits host. Culture normalisation
    // is the same NormaliseCulture helper pinned above.
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void Settings_Culture_FormatsCorrectly()
    {
        var key = AiVisibilityCacheKeys.Settings("en-GB");
        Assert.That(key, Is.EqualTo("aiv:settings:en-gb"));
    }

    [Test]
    public void Settings_NullCulture_UsesUnderscoreSentinel()
    {
        var key = AiVisibilityCacheKeys.Settings(null);
        Assert.That(key, Is.EqualTo("aiv:settings:_"));
    }

    [Test]
    public void SettingsPrefix_ClearsAllSettingsEntries()
    {
        // ContentCacheRefresherHandler clears the whole llms:settings: namespace
        // in one prefix-clear. Pin that the prefix is a strict prefix of every
        // generated Settings key.
        Assert.Multiple(() =>
        {
            Assert.That(AiVisibilityCacheKeys.Settings("en-GB"), Does.StartWith(AiVisibilityCacheKeys.SettingsPrefix));
            Assert.That(AiVisibilityCacheKeys.Settings("fr-FR"), Does.StartWith(AiVisibilityCacheKeys.SettingsPrefix));
            Assert.That(AiVisibilityCacheKeys.Settings(null), Does.StartWith(AiVisibilityCacheKeys.SettingsPrefix));
        });
    }

    [Test]
    public void Robots_HostOnly_FormatsAsLowercase()
    {
        // Story 4.2 — robots audit cache key. Host-only (no culture); host
        // normalisation reuses NormaliseHost → lowercased + port-stripped.
        // Same key shape across casings + invariant + missing-host paths.
        Assert.Multiple(() =>
        {
            Assert.That(AiVisibilityCacheKeys.Robots("Sitea.Example"),
                Is.EqualTo("aiv:robots:sitea.example"));
            Assert.That(AiVisibilityCacheKeys.Robots("SITEA.EXAMPLE:443"),
                Is.EqualTo("aiv:robots:sitea.example"),
                "port stripped");
            Assert.That(AiVisibilityCacheKeys.Robots(null),
                Is.EqualTo("aiv:robots:_"),
                "null host falls back to '_' sentinel");
            Assert.That(AiVisibilityCacheKeys.Robots("sitea.example"),
                Does.StartWith(AiVisibilityCacheKeys.RobotsPrefix));
            Assert.That(AiVisibilityCacheKeys.Robots("sitea.example"),
                Does.StartWith(AiVisibilityCacheKeys.Prefix));
        });
    }
}
