using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace Umbraco.Community.AiVisibility.Tests.Configuration;

/// <summary>
/// Story 4.1 Task 4 — pins the per-page-bool-then-resolver-throw-fail-open
/// shape lifted from <c>MarkdownController.IsExcludedAsync</c> +
/// <c>AcceptHeaderNegotiationMiddleware.IsExcludedAsync</c> into a single
/// shared evaluator. Five tests covering the gate's contract surface; existing
/// MarkdownController + AcceptHeaderNegotiation fixtures continue pinning the
/// integration with their respective callers.
/// </summary>
[TestFixture]
public class DefaultLlmsExclusionEvaluatorTests
{
    private const string ExcludeBoolAlias = "excludeFromLlmExports";

    private static IPublishedContent StubPage(string doctypeAlias, bool? excludeFromLlmExports = null)
    {
        var content = Substitute.For<IPublishedContent>();
        var ct = Substitute.For<IPublishedContentType>();
        ct.Alias.Returns(doctypeAlias);
        content.ContentType.Returns(ct);
        content.Key.Returns(Guid.NewGuid());

        if (excludeFromLlmExports.HasValue)
        {
            var prop = Substitute.For<IPublishedProperty>();
            prop.HasValue(culture: null, segment: null).Returns(true);
            prop.GetValue(culture: null, segment: null).Returns(excludeFromLlmExports.Value);
            content.GetProperty(ExcludeBoolAlias).Returns(prop);
        }
        else
        {
            content.GetProperty(ExcludeBoolAlias).Returns((IPublishedProperty?)null);
        }

        return content;
    }

    private static ResolvedLlmsSettings ResolvedWith(params string[] excludedAliases)
    {
        var set = new HashSet<string>(excludedAliases, StringComparer.OrdinalIgnoreCase);
        return new ResolvedLlmsSettings(
            SiteName: null,
            SiteSummary: null,
            ExcludedDoctypeAliases: set,
            BaseSettings: new AiVisibilitySettings());
    }

    [Test]
    public async Task IsExcludedAsync_PerPageBoolTrue_ReturnsTrue_WithoutCallingResolver()
    {
        var content = StubPage("articlePage", excludeFromLlmExports: true);
        var resolver = Substitute.For<ISettingsResolver>();
        var evaluator = new DefaultExclusionEvaluator(resolver, NullLogger<DefaultExclusionEvaluator>.Instance);

        var excluded = await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None);

        Assert.That(excluded, Is.True);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsExcludedAsync_DoctypeAliasInResolverList_CaseInsensitive_ReturnsTrue()
    {
        var content = StubPage("articlePage");
        var resolver = Substitute.For<ISettingsResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ResolvedWith("ARTICLEPAGE"));
        var evaluator = new DefaultExclusionEvaluator(resolver, NullLogger<DefaultExclusionEvaluator>.Instance);

        var excluded = await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None);

        Assert.That(excluded, Is.True);
    }

    [Test]
    public async Task IsExcludedAsync_NotInResolverList_AndNoBool_ReturnsFalse()
    {
        var content = StubPage("articlePage");
        var resolver = Substitute.For<ISettingsResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ResolvedWith("homePage"));
        var evaluator = new DefaultExclusionEvaluator(resolver, NullLogger<DefaultExclusionEvaluator>.Instance);

        var excluded = await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None);

        Assert.That(excluded, Is.False);
    }

    [Test]
    public async Task IsExcludedAsync_ResolverThrows_FailsOpen_ReturnsFalse()
    {
        var content = StubPage("articlePage");
        var resolver = Substitute.For<ISettingsResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("resolver glitch"));
        var evaluator = new DefaultExclusionEvaluator(resolver, NullLogger<DefaultExclusionEvaluator>.Instance);

        var excluded = await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None);

        Assert.That(excluded, Is.False);
    }

    [Test]
    public void IsExcludedAsync_ResolverThrowsOperationCancelled_Propagates()
    {
        var content = StubPage("articlePage");
        var resolver = Substitute.For<ISettingsResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());
        var evaluator = new DefaultExclusionEvaluator(resolver, NullLogger<DefaultExclusionEvaluator>.Instance);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None));
    }
}
