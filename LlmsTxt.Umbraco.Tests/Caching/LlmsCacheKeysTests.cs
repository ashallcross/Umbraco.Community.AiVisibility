using LlmsTxt.Umbraco.Caching;

namespace LlmsTxt.Umbraco.Tests.Caching;

[TestFixture]
public class LlmsCacheKeysTests
{
    private static readonly Guid Node = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Test]
    public void Page_WithCulture_FormatsAsLowercaseN()
    {
        var key = LlmsCacheKeys.Page(Node, "en-GB");
        Assert.That(key, Is.EqualTo("llms:page:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:en-gb"));
    }

    [Test]
    public void Page_WithUppercaseCulture_NormalisesToLowercase()
    {
        var lower = LlmsCacheKeys.Page(Node, "en-GB");
        var upper = LlmsCacheKeys.Page(Node, "EN-gb");
        Assert.That(upper, Is.EqualTo(lower));
    }

    [Test]
    public void Page_WithNullCulture_UsesInvariantSentinel()
    {
        var key = LlmsCacheKeys.Page(Node, null);
        Assert.That(key, Is.EqualTo("llms:page:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:_"));
    }

    [Test]
    public void Page_WithEmptyCulture_UsesInvariantSentinel()
    {
        Assert.That(
            LlmsCacheKeys.Page(Node, string.Empty),
            Is.EqualTo(LlmsCacheKeys.Page(Node, null)));
    }

    [Test]
    public void Page_StartsWithPagePrefix()
    {
        Assert.That(LlmsCacheKeys.Page(Node, "en-GB"), Does.StartWith(LlmsCacheKeys.PagePrefix));
    }

    [Test]
    public void Page_StartsWithGlobalPrefix()
    {
        // ClearByKey(Prefix) must cover every key shape we ever issue.
        Assert.That(LlmsCacheKeys.Page(Node, "en-GB"), Does.StartWith(LlmsCacheKeys.Prefix));
    }

    [Test]
    public void Page_DifferentNode_DifferentKey()
    {
        var a = LlmsCacheKeys.Page(Node, "en-GB");
        var b = LlmsCacheKeys.Page(Guid.NewGuid(), "en-GB");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void Page_DifferentCulture_DifferentKey()
    {
        var en = LlmsCacheKeys.Page(Node, "en-GB");
        var fr = LlmsCacheKeys.Page(Node, "fr-FR");
        Assert.That(fr, Is.Not.EqualTo(en));
    }
}
