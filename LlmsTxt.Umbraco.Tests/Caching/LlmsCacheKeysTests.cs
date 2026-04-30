using LlmsTxt.Umbraco.Caching;

namespace LlmsTxt.Umbraco.Tests.Caching;

[TestFixture]
public class LlmsCacheKeysTests
{
    private static readonly Guid Node = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string Host = "sitea.example";

    [Test]
    public void Page_WithCulture_FormatsAsLowercaseN()
    {
        var key = LlmsCacheKeys.Page(Node, Host, "en-GB");
        Assert.That(key, Is.EqualTo("llms:page:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:sitea.example:en-gb"));
    }

    [Test]
    public void Page_WithUppercaseCulture_NormalisesToLowercase()
    {
        var lower = LlmsCacheKeys.Page(Node, Host, "en-GB");
        var upper = LlmsCacheKeys.Page(Node, Host, "EN-gb");
        Assert.That(upper, Is.EqualTo(lower));
    }

    [Test]
    public void Page_WithNullCulture_UsesInvariantSentinel()
    {
        var key = LlmsCacheKeys.Page(Node, Host, null);
        Assert.That(key, Is.EqualTo("llms:page:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:sitea.example:_"));
    }

    [Test]
    public void Page_WithEmptyCulture_UsesInvariantSentinel()
    {
        Assert.That(
            LlmsCacheKeys.Page(Node, Host, string.Empty),
            Is.EqualTo(LlmsCacheKeys.Page(Node, Host, null)));
    }

    [Test]
    public void Page_StartsWithPagePrefix()
    {
        Assert.That(LlmsCacheKeys.Page(Node, Host, "en-GB"), Does.StartWith(LlmsCacheKeys.PagePrefix));
    }

    [Test]
    public void Page_StartsWithGlobalPrefix()
    {
        // ClearByKey(Prefix) must cover every key shape we ever issue.
        Assert.That(LlmsCacheKeys.Page(Node, Host, "en-GB"), Does.StartWith(LlmsCacheKeys.Prefix));
    }

    [Test]
    public void Page_NodeKeyImmediatelyFollowsPagePrefix()
    {
        // Story 1.5: nodeKey stays as the second segment so the handler's
        // race-mitigating prefix-clear `llms:page:{nodeKey:N}:` still finds
        // and clears every per-host entry for that node.
        var key = LlmsCacheKeys.Page(Node, Host, "en-GB");
        Assert.That(key, Does.StartWith($"{LlmsCacheKeys.PagePrefix}{Node:N}:"));
    }

    [Test]
    public void Page_DifferentNode_DifferentKey()
    {
        var a = LlmsCacheKeys.Page(Node, Host, "en-GB");
        var b = LlmsCacheKeys.Page(Guid.NewGuid(), Host, "en-GB");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void Page_DifferentHost_DifferentKey()
    {
        var a = LlmsCacheKeys.Page(Node, "sitea.example", "en-GB");
        var b = LlmsCacheKeys.Page(Node, "siteb.example", "en-GB");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void Page_UppercaseHost_NormalisesToLowercase()
    {
        var lower = LlmsCacheKeys.Page(Node, "sitea.example", "en-GB");
        var upper = LlmsCacheKeys.Page(Node, "SiteA.Example", "en-GB");
        Assert.That(upper, Is.EqualTo(lower));
    }

    [Test]
    public void Page_WithNullHost_UsesInvariantSentinel()
    {
        var key = LlmsCacheKeys.Page(Node, null, "en-GB");
        Assert.That(key, Is.EqualTo("llms:page:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:_:en-gb"));
    }

    [Test]
    public void Page_WithEmptyHost_UsesInvariantSentinel()
    {
        Assert.That(
            LlmsCacheKeys.Page(Node, string.Empty, "en-GB"),
            Is.EqualTo(LlmsCacheKeys.Page(Node, null, "en-GB")));
    }

    [Test]
    public void Page_WithWhitespaceHost_UsesInvariantSentinel()
    {
        // Story 1.5 review: defensive against malformed Host headers / misbehaving
        // clients. IsNullOrWhiteSpace routes a single-space host to the "_" sentinel
        // rather than allowing " " to become a distinct cache-key segment.
        Assert.That(
            LlmsCacheKeys.Page(Node, "   ", "en-GB"),
            Is.EqualTo(LlmsCacheKeys.Page(Node, null, "en-GB")));
    }

    [Test]
    public void Page_HostWithPort_StripsPort()
    {
        // Story 1.5 review: NormaliseHost is public and may be called from future
        // Epic 2 manifest builders that pull host strings from IDomain entries that
        // include port. The helper must produce the same key regardless of whether
        // the caller passes "sitea.example" or "sitea.example:443".
        var withoutPort = LlmsCacheKeys.Page(Node, "sitea.example", "en-GB");
        var withPort = LlmsCacheKeys.Page(Node, "sitea.example:443", "en-GB");
        Assert.That(withPort, Is.EqualTo(withoutPort));
    }

    [Test]
    public void Page_HostWithPortOnly_UsesInvariantSentinel()
    {
        // Edge case: a malformed input like ":443" (port without host) strips to an
        // empty string, which must route to the "_" sentinel rather than colliding
        // with a missing-host entry under an empty segment.
        Assert.That(
            LlmsCacheKeys.Page(Node, ":443", "en-GB"),
            Is.EqualTo(LlmsCacheKeys.Page(Node, null, "en-GB")));
    }

    [Test]
    public void Page_DifferentCulture_DifferentKey()
    {
        var en = LlmsCacheKeys.Page(Node, Host, "en-GB");
        var fr = LlmsCacheKeys.Page(Node, Host, "fr-FR");
        Assert.That(fr, Is.Not.EqualTo(en));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Story 2.1 — /llms.txt manifest keys
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void LlmsTxt_DefaultHostAndCulture_FormatsCorrectly()
    {
        var key = LlmsCacheKeys.LlmsTxt(Host, "en-GB");
        Assert.That(key, Is.EqualTo("llms:llmstxt:sitea.example:en-gb"));
    }

    [Test]
    public void LlmsTxt_HostUppercased_NormalisedToLower()
    {
        var lower = LlmsCacheKeys.LlmsTxt("sitea.example", "en-GB");
        var upper = LlmsCacheKeys.LlmsTxt("SiteA.Example", "en-GB");
        Assert.That(upper, Is.EqualTo(lower));
    }

    [Test]
    public void LlmsTxt_HostWithPort_PortStripped()
    {
        var withoutPort = LlmsCacheKeys.LlmsTxt("sitea.example", "en-GB");
        var withPort = LlmsCacheKeys.LlmsTxt("sitea.example:443", "en-GB");
        Assert.That(withPort, Is.EqualTo(withoutPort));
    }

    [Test]
    public void LlmsTxt_NullHost_UsesUnderscore()
    {
        var key = LlmsCacheKeys.LlmsTxt(null, "en-GB");
        Assert.That(key, Is.EqualTo("llms:llmstxt:_:en-gb"));
    }

    [Test]
    public void LlmsTxt_NullCulture_UsesUnderscore()
    {
        var key = LlmsCacheKeys.LlmsTxt(Host, null);
        Assert.That(key, Is.EqualTo("llms:llmstxt:sitea.example:_"));
    }

    [Test]
    public void LlmsTxt_StartsWithGlobalPrefix()
    {
        Assert.That(LlmsCacheKeys.LlmsTxt(Host, "en-GB"), Does.StartWith(LlmsCacheKeys.Prefix));
    }

    [Test]
    public void LlmsTxt_StartsWithLlmsTxtPrefix()
    {
        Assert.That(LlmsCacheKeys.LlmsTxt(Host, "en-GB"), Does.StartWith(LlmsCacheKeys.LlmsTxtPrefix));
    }

    [Test]
    public void LlmsTxtHostPrefix_BuildsClearByKeyArgument()
    {
        // Pessimistic invalidation: ClearByKey(LlmsTxtHostPrefix(host)) must drop
        // every culture's manifest entry for that hostname.
        var prefix = LlmsCacheKeys.LlmsTxtHostPrefix("sitea.example");
        Assert.Multiple(() =>
        {
            Assert.That(prefix, Is.EqualTo("llms:llmstxt:sitea.example:"));
            Assert.That(LlmsCacheKeys.LlmsTxt("sitea.example", "en-GB"), Does.StartWith(prefix));
            Assert.That(LlmsCacheKeys.LlmsTxt("sitea.example", "fr-FR"), Does.StartWith(prefix));
            Assert.That(LlmsCacheKeys.LlmsTxt("siteb.example", "en-GB"), Does.Not.StartWith(prefix));
        });
    }

    [Test]
    public void LlmsTxt_DifferentHost_DifferentKey()
    {
        var a = LlmsCacheKeys.LlmsTxt("sitea.example", "en-GB");
        var b = LlmsCacheKeys.LlmsTxt("siteb.example", "en-GB");
        Assert.That(b, Is.Not.EqualTo(a));
    }
}
