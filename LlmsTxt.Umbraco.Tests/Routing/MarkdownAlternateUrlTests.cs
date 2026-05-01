using LlmsTxt.Umbraco.Routing;

namespace LlmsTxt.Umbraco.Tests.Routing;

/// <summary>
/// Story 4.1 — pins the canonical URL → .md alternate mapping per the
/// llms.txt trailing-slash convention. Reused by:
/// <list type="bullet">
/// <item><description><c>DiscoverabilityHeaderMiddleware</c> (Link header).</description></item>
/// <item><description><c>LlmsLinkTagHelper</c> (HTML &lt;link rel="alternate"&gt;).</description></item>
/// <item><description><c>LlmsHintTagHelper</c> (visually-hidden anchor).</description></item>
/// </list>
/// </summary>
[TestFixture]
public class MarkdownAlternateUrlTests
{
    [TestCase("/home", "/home.md")]
    [TestCase("/blog/post-1", "/blog/post-1.md")]
    [TestCase("/about-us", "/about-us.md")]
    public void Append_NonTrailingSlash_AppendsDotMd(string canonical, string expected)
    {
        Assert.That(MarkdownAlternateUrl.Append(canonical), Is.EqualTo(expected));
    }

    [TestCase("/", "/index.html.md")]
    [TestCase("/blog/", "/blog/index.html.md")]
    [TestCase("/section/sub/", "/section/sub/index.html.md")]
    public void Append_TrailingSlash_AppendsIndexHtmlMd(string canonical, string expected)
    {
        Assert.That(MarkdownAlternateUrl.Append(canonical), Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("/")]
    public void Append_EmptyOrNullOrRoot_ReturnsIndexHtmlMd(string? canonical)
    {
        // Site root collapses to /index.html.md per llms.txt trailing-slash convention,
        // regardless of which root representation the caller supplies.
        Assert.That(MarkdownAlternateUrl.Append(canonical), Is.EqualTo("/index.html.md"));
    }

    [TestCase("/home.md")]
    [TestCase("/blog/index.html.md")]
    [TestCase("/HOME.MD")] // case-insensitive idempotency
    public void Append_AlreadyMdSuffix_PassesThrough(string canonical)
    {
        Assert.That(MarkdownAlternateUrl.Append(canonical), Is.EqualTo(canonical));
    }
}
