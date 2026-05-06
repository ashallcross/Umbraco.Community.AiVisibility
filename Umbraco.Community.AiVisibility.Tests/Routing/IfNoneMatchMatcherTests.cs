using Umbraco.Community.AiVisibility.Routing;
using Microsoft.AspNetCore.Http;

namespace Umbraco.Community.AiVisibility.Tests.Routing;

[TestFixture]
public class IfNoneMatchMatcherTests
{
    private const string ETag = "\"abc123def456_HX\"";

    private static HttpRequest MakeRequest(string? ifNoneMatch)
    {
        var ctx = new DefaultHttpContext();
        if (ifNoneMatch is not null)
        {
            ctx.Request.Headers["If-None-Match"] = ifNoneMatch;
        }
        return ctx.Request;
    }

    [Test]
    public void Matches_ExactETag_ReturnsTrue()
    {
        Assert.That(IfNoneMatchMatcher.Matches(MakeRequest(ETag), ETag), Is.True);
    }

    [Test]
    public void Matches_WildcardStar_ReturnsTrue()
    {
        Assert.That(IfNoneMatchMatcher.Matches(MakeRequest("*"), ETag), Is.True);
    }

    [Test]
    public void Matches_WeakWildcard_ReturnsFalse()
    {
        // RFC 7232 § 3.2: only the bare `*` token is the wildcard. `W/*` is malformed.
        Assert.That(IfNoneMatchMatcher.Matches(MakeRequest("W/*"), ETag), Is.False);
    }

    [Test]
    public void Matches_WeakValidator_StrippedThenCompared()
    {
        // We always emit strong validators; accept either form on input. CDNs
        // commonly rewrite ETags adding `W/` weak prefixes — we still match.
        var weak = "W/" + ETag;
        Assert.That(IfNoneMatchMatcher.Matches(MakeRequest(weak), ETag), Is.True);
    }

    [Test]
    public void Matches_CommaSeparatedList_AnyMatchSucceeds()
    {
        var list = "\"old1\", \"old2\", " + ETag + ", \"old3\"";
        Assert.That(IfNoneMatchMatcher.Matches(MakeRequest(list), ETag), Is.True);
    }

    [Test]
    public void Matches_NoneInList_ReturnsFalse()
    {
        var list = "\"old1\", \"old2\", \"old3\"";
        Assert.That(IfNoneMatchMatcher.Matches(MakeRequest(list), ETag), Is.False);
    }

    [Test]
    public void Matches_NoHeader_ReturnsFalse()
    {
        Assert.That(IfNoneMatchMatcher.Matches(MakeRequest(null), ETag), Is.False);
    }

    [Test]
    public void Matches_EmptyHeader_ReturnsFalse()
    {
        Assert.That(IfNoneMatchMatcher.Matches(MakeRequest(string.Empty), ETag), Is.False);
    }

    [Test]
    public void Matches_StaleETag_ReturnsFalse()
    {
        Assert.That(IfNoneMatchMatcher.Matches(MakeRequest("\"stale\""), ETag), Is.False);
    }

    [Test]
    public void Matches_NullArgs_Throws()
    {
        Assert.That(() => IfNoneMatchMatcher.Matches(null!, ETag), Throws.ArgumentNullException);
        Assert.That(() => IfNoneMatchMatcher.Matches(MakeRequest(ETag), null!), Throws.ArgumentNullException);
    }
}
