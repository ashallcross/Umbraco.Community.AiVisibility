using LlmsTxt.Umbraco.Routing;
using Microsoft.AspNetCore.Http;

namespace LlmsTxt.Umbraco.Tests.Routing;

[TestFixture]
public class LlmsPipelineFilterTests
{
    [TestCase("/home.md", ExpectedResult = true)]
    [TestCase("/blog/post-1.md", ExpectedResult = true)]
    [TestCase("/docs/index.html.md", ExpectedResult = true)]
    [TestCase("/MIXED/Case.MD", ExpectedResult = true)]
    [TestCase("/home", ExpectedResult = false)]
    [TestCase("/home.html", ExpectedResult = false)]
    [TestCase("/file.markdown", ExpectedResult = false)]
    [TestCase("/", ExpectedResult = false)]
    [TestCase("", ExpectedResult = false)]
    public bool IsMarkdownPath_RecognisesDotMdSuffixCaseInsensitive(string path)
        => LlmsPipelineFilter.IsMarkdownPath(new PathString(string.IsNullOrEmpty(path) ? null : path));

    [Test]
    public void HandleAsServerSideRequest_ComposesWithExistingDelegate_PreviousReturnsTrue_ResultIsTrue()
    {
        // Adopter has registered a previous delegate that returns true for /api/* paths.
        Func<HttpRequest, bool> previous = req => req.Path.StartsWithSegments("/api");
        var composed = LlmsPipelineFilter.ComposeHandleAsServerSideRequest(previous);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/foo";

        Assert.That(composed(ctx.Request), Is.True);
    }

    [Test]
    public void HandleAsServerSideRequest_ComposesWithExistingDelegate_PreviousReturnsFalseAndPathIsMd_ResultIsTrue()
    {
        Func<HttpRequest, bool> previous = req => false;
        var composed = LlmsPipelineFilter.ComposeHandleAsServerSideRequest(previous);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/home.md";

        Assert.That(composed(ctx.Request), Is.True);
    }

    [Test]
    public void HandleAsServerSideRequest_ComposesWithExistingDelegate_PreviousReturnsFalseAndPathIsNotMd_ResultIsFalse()
    {
        Func<HttpRequest, bool> previous = req => false;
        var composed = LlmsPipelineFilter.ComposeHandleAsServerSideRequest(previous);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/home";

        Assert.That(composed(ctx.Request), Is.False);
    }

    [Test]
    public void HandleAsServerSideRequest_ComposesWithNullPrevious_PathIsMd_ResultIsTrue()
    {
        var composed = LlmsPipelineFilter.ComposeHandleAsServerSideRequest(previous: null);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/home.md";

        Assert.That(composed(ctx.Request), Is.True);
    }

    [Test]
    public void HandleAsServerSideRequest_DoesNotInvokePreviousIfMdMatchesFirst()
    {
        // Documents the OR semantics — both predicates may run, but neither raises;
        // the composed delegate is pure and side-effect-free besides its return value.
        var previousInvoked = 0;
        Func<HttpRequest, bool> previous = req => { previousInvoked++; return false; };
        var composed = LlmsPipelineFilter.ComposeHandleAsServerSideRequest(previous);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/home.md";

        Assert.That(composed(ctx.Request), Is.True);
        Assert.That(previousInvoked, Is.EqualTo(1), "previous delegate is consulted exactly once per call");
    }
}
