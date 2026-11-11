using Umbraco.Community.AiVisibility.Extraction;

namespace Umbraco.Community.AiVisibility.Tests.Extraction;

[TestFixture]
public class MarkdownConverterTests
{
    private MarkdownConverter _converter = null!;

    [SetUp]
    public void Setup() => _converter = new MarkdownConverter();

    [Test]
    public void Convert_PreservesHeadingLevels()
    {
        var html = "<h1>One</h1><h2>Two</h2><h3>Three</h3>";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Contain("# One"));
        Assert.That(md, Does.Contain("## Two"));
        Assert.That(md, Does.Contain("### Three"));
    }

    [Test]
    public void Convert_EmitsGfmTables()
    {
        var html = "<table><thead><tr><th>A</th><th>B</th></tr></thead><tbody><tr><td>1</td><td>2</td></tr></tbody></table>";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Contain("| A").IgnoreCase);
        Assert.That(md, Does.Contain("| 1").IgnoreCase);
    }

    [Test]
    public void Convert_PreservesImageAlt()
    {
        var html = "<img src=\"https://example.test/image.png\" alt=\"hero shot\" />";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Contain("![hero shot](https://example.test/image.png)"));
    }

    [Test]
    public void Convert_PreservesLinks()
    {
        var html = "<a href=\"https://example.test/about\">About</a>";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Contain("[About](https://example.test/about)"));
    }

    [Test]
    public void Convert_PreservesFencedCodeBlock()
    {
        var html = "<pre><code class=\"language-csharp\">var x = 1;</code></pre>";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Contain("```csharp"));
        Assert.That(md, Does.Contain("var x = 1;"));
    }

    [Test]
    public void Convert_StripsHtmlComments()
    {
        var html = "<p>Hello <!-- secret --> world</p>";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Not.Contain("secret"));
        Assert.That(md, Does.Contain("Hello"));
        Assert.That(md, Does.Contain("world"));
    }

    [Test]
    public void Convert_RejectsJavascriptUriScheme()
    {
        // Security: javascript: hrefs must not survive the conversion as a clickable link.
        var html = "<a href=\"javascript:alert(1)\">click</a>";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Not.Contain("javascript:"));
    }

    [Test]
    public void Convert_PreservesBlockquote()
    {
        var html = "<blockquote><p>Quoted text</p></blockquote>";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Contain("> Quoted text"));
    }

    [Test]
    public void Convert_HeadingTextWrappedInNewlinesAndIndent_EmitsAtxHeading()
    {
        // GitHub #6: templates that indent heading text used to produce an empty
        // "##" followed by an indented code block.
        var html = "<h2 class=\"textBlock__title\">\n    About us\n</h2>";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Contain("## About us"));
        Assert.That(md, Does.Not.Match(@"(?m)^#{1,6}\s*$"), "no empty heading");
        Assert.That(md, Does.Not.Match(@"(?m)^( {4}|\t)\S"), "no indented code block");
    }

    [Test]
    public void Convert_FigureWithCaption_KeepsAltTextAndCaption()
    {
        var md = _converter.Convert(
            "<figure><img src=\"https://x.test/a.png\" alt=\"Alt A\"><figcaption>Figure 1 — caption</figcaption></figure>");
        Assert.That(md, Does.Contain("![Alt A](https://x.test/a.png)"));
        Assert.That(md, Does.Contain("Figure 1 — caption"));
        Assert.That(md, Does.Not.Contain("![Figure 1"));
        Assert.That(md, Does.Match(@"\(https://x\.test/a\.png\)\s*\n\s*Figure 1"), "caption on its own line");
    }

    [Test]
    public void Convert_FigureWithTwoImages_KeepsBothImages()
    {
        var md = _converter.Convert(
            "<figure><img src=\"https://x.test/a.png\" alt=\"A\"><img src=\"https://x.test/b.png\" alt=\"B\"><figcaption>Two</figcaption></figure>");
        Assert.That(md, Does.Contain("![A](https://x.test/a.png)"));
        Assert.That(md, Does.Contain("![B](https://x.test/b.png)"));
        // 6.3.0 keeps the two images on one line; only the caption is separated.
        Assert.That(md, Does.Match(@"\(https://x\.test/b\.png\)\s*\n\s*Two"), "caption on its own line");
    }

    [Test]
    public void Convert_FigureCaptionWithLink_EmitsLinkOutsideImage()
    {
        var md = _converter.Convert(
            "<figure><img src=\"https://x.test/a.png\" alt=\"A\"><figcaption>See <a href=\"https://y.test/\">link</a></figcaption></figure>");
        Assert.That(md, Does.Contain("![A](https://x.test/a.png)"));
        Assert.That(md, Does.Contain("See [link](https://y.test/)"));
        Assert.That(md, Does.Not.Contain("![See"));
        Assert.That(md, Does.Match(@"\(https://x\.test/a\.png\)\s*\n\s*See "), "caption on its own line");
    }

    [Test]
    public void Convert_UnknownTag_KeepsInnerTextOnly()
    {
        // Bypass: the unknown tag is dropped, its inner content is kept.
        var html = "<p><custom-x>keep</custom-x></p>";
        var md = _converter.Convert(html);
        Assert.That(md, Does.Contain("keep"));
        Assert.That(md, Does.Not.Contain("custom-x"));
        Assert.That(md, Does.Not.Contain("<"));
    }

    [Test]
    public void Convert_WhitelistedSchemes_AllowsMailtoAndTel_DropsDataAndFileLinks()
    {
        var md = _converter.Convert(
            "<p><a href=\"mailto:a@b.test\">Mail</a> <a href=\"tel:+441\">Call</a> "
            + "<a href=\"data:text/html,x\">DataLink</a> <a href=\"file:///etc/passwd\">FileLink</a></p>");
        Assert.Multiple(() =>
        {
            Assert.That(md, Does.Contain("[Mail](mailto:a@b.test)"));
            Assert.That(md, Does.Contain("[Call](tel:+441)"));
            Assert.That(md, Does.Not.Contain("data:"));
            Assert.That(md, Does.Not.Contain("file:"));
            Assert.That(md, Does.Contain("DataLink"));
            Assert.That(md, Does.Contain("FileLink"));
            Assert.That(md, Does.Not.Contain("[DataLink]"));
            Assert.That(md, Does.Not.Contain("[FileLink]"));
        });
    }

    [Test]
    public void Convert_SmartHref_LinkWhoseTextIsTheUrl_EmitsPlainUrl()
    {
        var md = _converter.Convert("<a href=\"https://x.test/\">https://x.test/</a>");
        Assert.That(md, Does.Contain("https://x.test/"));
        Assert.That(md, Does.Not.Contain("[https://x.test/](https://x.test/)"));
    }

    [Test]
    public void Convert_NestedList_IndentsChildByTwoSpaces()
    {
        var md = _converter.Convert("<ul><li>one<ul><li>two</li></ul></li></ul>");
        Assert.That(md, Does.Match(@"(?m)^- one\r?\n  - two$"));
    }
}
