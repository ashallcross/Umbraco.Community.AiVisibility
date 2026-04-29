using LlmsTxt.Umbraco.Routing;

namespace LlmsTxt.Umbraco.Tests.Routing;

[TestFixture]
public class MarkdownPathNormaliserTests
{
    // AC1 / Task 4: strip the .md or /index.html.md suffix to recover the canonical path.
    [TestCase("home.md", ExpectedResult = "/home")]
    [TestCase("/home.md", ExpectedResult = "/home")]
    [TestCase("blog/post-1.md", ExpectedResult = "/blog/post-1")]
    [TestCase("docs/index.html.md", ExpectedResult = "/docs/")]
    [TestCase("Docs/Index.HTML.MD", ExpectedResult = "/Docs/")]
    [TestCase("HOME.MD", ExpectedResult = "/HOME")]
    public string Normalise_StripsMdSuffix(string captured)
        => MarkdownPathNormaliser.NormaliseToCanonical(captured);

    // AC8: /docs/.md is accepted and serves identical Markdown for /docs/ — Task 8 decision.
    [TestCase("docs/.md", ExpectedResult = "/docs/")]
    [TestCase("/docs/.md", ExpectedResult = "/docs/")]
    public string Normalise_HandlesDotSlashMdEdgeCase(string captured)
        => MarkdownPathNormaliser.NormaliseToCanonical(captured);

    // Failure & Edge Cases: URL-encoded characters must decode before resolution.
    [TestCase("caf%C3%A9.md", ExpectedResult = "/café")]
    [TestCase("/blog/%E2%9C%85.md", ExpectedResult = "/blog/✅")]
    public string Normalise_DecodesUrlEncoding(string captured)
        => MarkdownPathNormaliser.NormaliseToCanonical(captured);

    [Test]
    public void Normalise_RejectsNonMdSuffix()
    {
        Assert.Throws<ArgumentException>(() => MarkdownPathNormaliser.NormaliseToCanonical("home.html"));
    }

    [Test]
    public void Normalise_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => MarkdownPathNormaliser.NormaliseToCanonical(""));
    }

    [TestCase("/../etc/passwd.md")]
    [TestCase("/foo/../bar.md")]
    [TestCase("..%2Fbar.md")]      // encoded slash before ..
    [TestCase("/foo/%2E%2E/bar.md")] // encoded ..
    [TestCase("\\windows\\path.md")]
    public void Normalise_RejectsTraversal(string captured)
    {
        Assert.Throws<ArgumentException>(() => MarkdownPathNormaliser.NormaliseToCanonical(captured));
    }

    [TestCase("/.md")]
    [TestCase(".md")]
    public void Normalise_RejectsBareSuffix(string captured)
    {
        // `/.md` and `.md` would otherwise collapse to `/` — silently serving the
        // homepage for plainly malformed input. Reject instead.
        Assert.Throws<ArgumentException>(() => MarkdownPathNormaliser.NormaliseToCanonical(captured));
    }

    [Test]
    public void Normalise_RejectsControlCharacters()
    {
        Assert.Throws<ArgumentException>(() => MarkdownPathNormaliser.NormaliseToCanonical("/home\0.md"));
    }
}
