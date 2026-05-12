using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Community.AiVisibility.Tests.Configuration;

/// <summary>
/// Pins the three-source exclusion chain inside <see cref="DefaultExclusionEvaluator"/>:
/// per-page <c>excludeFromLlmExports</c> bool → <c>IPublicAccessService.IsProtected</c>
/// → resolver-overlay doctype-alias list. Existing <c>MarkdownController</c> and
/// <c>AcceptHeaderNegotiationMiddleware</c> fixtures continue pinning the integration
/// with their respective callers.
/// </summary>
[TestFixture]
public class DefaultLlmsExclusionEvaluatorTests
{
    private const string ExcludeBoolAlias = "excludeFromLlmExports";
    private const string DefaultPath = "-1,1000,2000";

    private static IPublishedContent StubPage(
        string doctypeAlias,
        bool? excludeFromLlmExports = null,
        string path = DefaultPath)
    {
        var content = Substitute.For<IPublishedContent>();
        var ct = Substitute.For<IPublishedContentType>();
        ct.Alias.Returns(doctypeAlias);
        content.ContentType.Returns(ct);
        content.Key.Returns(Guid.NewGuid());
        content.Path.Returns(path);

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

    /// <summary>
    /// Helper that constructs a <see cref="DefaultExclusionEvaluator"/> with default
    /// substitutes for all three dependencies. Existing tests defaulted to the
    /// "non-protected" path; the substitute returns <c>false</c> from
    /// <see cref="IPublicAccessService.IsProtected(string)"/> unless the caller
    /// configures otherwise.
    /// </summary>
    private static DefaultExclusionEvaluator BuildEvaluator(
        ISettingsResolver? resolver = null,
        IPublicAccessService? publicAccess = null)
    {
        resolver ??= Substitute.For<ISettingsResolver>();
        if (publicAccess is null)
        {
            publicAccess = Substitute.For<IPublicAccessService>();
            publicAccess.IsProtected(Arg.Any<string>()).Returns(Attempt<PublicAccessEntry?>.Fail());
        }

        return new DefaultExclusionEvaluator(
            resolver,
            publicAccess,
            NullLogger<DefaultExclusionEvaluator>.Instance);
    }

    [Test]
    public async Task IsExcludedAsync_PerPageBoolTrue_ReturnsTrue_WithoutCallingResolverOrPublicAccess()
    {
        var content = StubPage("articlePage", excludeFromLlmExports: true);
        var resolver = Substitute.For<ISettingsResolver>();
        var publicAccess = Substitute.For<IPublicAccessService>();
        publicAccess.IsProtected(Arg.Any<string>()).Returns(Attempt<PublicAccessEntry?>.Fail());

        var evaluator = BuildEvaluator(resolver, publicAccess);

        var excluded = await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None);

        Assert.That(excluded, Is.True);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        publicAccess.DidNotReceive().IsProtected(Arg.Any<string>());
    }

    [Test]
    public async Task IsExcludedAsync_DoctypeAliasInResolverList_CaseInsensitive_ReturnsTrue()
    {
        var content = StubPage("articlePage");
        var resolver = Substitute.For<ISettingsResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ResolvedWith("ARTICLEPAGE"));

        var evaluator = BuildEvaluator(resolver);

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

        var evaluator = BuildEvaluator(resolver);

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

        var evaluator = BuildEvaluator(resolver);

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

        var evaluator = BuildEvaluator(resolver);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None));
    }

    [Test]
    public async Task IsExcludedAsync_IsProtectedTrue_ReturnsTrue_WithoutCallingResolver()
    {
        var content = StubPage("articlePage", path: "-1,1000,2000");
        var resolver = Substitute.For<ISettingsResolver>();
        var publicAccess = Substitute.For<IPublicAccessService>();
        publicAccess.IsProtected("-1,1000,2000").Returns(Attempt<PublicAccessEntry?>.Succeed());

        var evaluator = BuildEvaluator(resolver, publicAccess);

        var excluded = await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None);

        Assert.That(excluded, Is.True);
        publicAccess.Received(1).IsProtected("-1,1000,2000");
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsExcludedAsync_IsProtectedThrows_FailsOpen_ReturnsFalse()
    {
        var content = StubPage("articlePage");
        var resolver = Substitute.For<ISettingsResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ResolvedWith());
        var publicAccess = Substitute.For<IPublicAccessService>();
        publicAccess.IsProtected(Arg.Any<string>()).Throws(new InvalidOperationException("public access glitch"));

        var evaluator = BuildEvaluator(resolver, publicAccess);

        var excluded = await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None);

        Assert.That(excluded, Is.False);
    }

    [Test]
    public void IsExcludedAsync_IsProtectedThrowsOperationCancelled_Propagates()
    {
        var content = StubPage("articlePage");
        var publicAccess = Substitute.For<IPublicAccessService>();
        publicAccess.IsProtected(Arg.Any<string>()).Throws(new OperationCanceledException());

        var evaluator = BuildEvaluator(publicAccess: publicAccess);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await evaluator.IsExcludedAsync(content, "en-gb", "example.com", CancellationToken.None));
    }

    /// <summary>
    /// Compile-time tripwire: if a future Umbraco point release renames or
    /// removes the path-string overload of
    /// <see cref="IPublicAccessService.IsProtected(string)"/>, the static
    /// <see cref="Func{T1,T2,TResult}"/> declaration below stops compiling
    /// and the evaluator's contract is invalidated at build time rather
    /// than at runtime.
    /// </summary>
    [Test]
    public void IPublicAccessService_IsProtectedStringOverload_Exists()
    {
        Func<IPublicAccessService, string, Attempt<PublicAccessEntry?>> _ =
            static (svc, path) => svc.IsProtected(path);

        Assert.Pass(
            "IPublicAccessService.IsProtected(string) overload resolved at "
            + "compile-time; runtime body unreachable.");
    }
}
