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
    public void Page_DifferentCulture_DifferentKey()
    {
        var en = LlmsCacheKeys.Page(Node, Host, "en-GB");
        var fr = LlmsCacheKeys.Page(Node, Host, "fr-FR");
        Assert.That(fr, Is.Not.EqualTo(en));
    }
}
