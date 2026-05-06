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
}
