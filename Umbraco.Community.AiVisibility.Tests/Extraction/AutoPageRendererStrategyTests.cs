using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.ModelBinders;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;

namespace Umbraco.Community.AiVisibility.Tests.Extraction;

/// <summary>
/// Story 7.3 AC2 – AC5 — exercises the
/// <see cref="AutoPageRendererStrategy"/> fallback algorithm:
/// <list type="bullet">
/// <item>Razor success → return verbatim, no cache write.</item>
/// <item>Razor failure with <see cref="ModelBindingException"/> → cache the
/// <c>(ContentTypeAlias, TemplateAlias)</c> tuple + warn + delegate to
/// Loopback.</item>
/// <item>Razor failure with any other exception → propagate the failure
/// verbatim, no cache write, no Loopback attempt (the narrow trigger
/// invariant per architecture.md § Auto Mode — Fallback Internals).</item>
/// <item>Cache hit → skip Razor entirely; Loopback called directly without
/// re-logging the fallback warning.</item>
/// <item>Mixed-template invariant — same content, different template
/// aliases produce distinct cache keys (architecture.md:1280-1284).</item>
/// <item>Empty-template-alias fallback for the no-template / template-deleted
/// cases (architecture.md:1284).</item>
/// <item>Cancellation propagation (Story 7.1 + 7.2 strategy convention).</item>
/// </list>
/// </summary>
[TestFixture]
public class AutoPageRendererStrategyTests
{
    private static readonly Uri SampleUri = new("https://example.test/foo");
    private const string SampleCulture = "en-GB";
    private const string LandingDoctype = "landingPage";
    private const string LandingTemplate = "landingPage";
    private const string LandingPrintTemplate = "landingPagePrint";

    [Test]
    public async Task RenderAsync_RazorSucceeds_ReturnsRazorResult_NoCacheWrite()
    {
        var content = StubPublishedContent(LandingDoctype, templateId: 42);
        var razor = Substitute.For<IPageRendererStrategy>();
        var loopback = Substitute.For<IPageRendererStrategy>();
        var cache = Substitute.For<IRendererStrategyCache>();
        var templateService = StubTemplateService(42, LandingTemplate);

        var razorOk = PageRenderResult.Ok("<html>razor-ok</html>", content, LandingTemplate, SampleCulture);
        razor.RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(razorOk);

        var (auto, sp) = BuildStrategy(cache, templateService, razor, loopback);
        using var spScope = sp;

        var result = await auto.RenderAsync(content, SampleUri, SampleCulture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(razorOk), "Razor success must be returned verbatim");
            cache.DidNotReceive().MarkHijacked(Arg.Any<string>(), Arg.Any<string>());
            loopback.DidNotReceive().RenderAsync(
                Arg.Any<IPublishedContent>(), Arg.Any<Uri>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task RenderAsync_RazorFailsModelBindingException_FallsBackToLoopback_AndCachesDecision()
    {
        var content = StubPublishedContent(LandingDoctype, templateId: 42);
        var razor = Substitute.For<IPageRendererStrategy>();
        var loopback = Substitute.For<IPageRendererStrategy>();
        var cache = Substitute.For<IRendererStrategyCache>();
        var templateService = StubTemplateService(42, LandingTemplate);

        var bindingException = new ModelBindingException("custom view model cannot bind IPublishedContent");
        var razorFailed = PageRenderResult.Failed(bindingException, content, LandingTemplate, SampleCulture);
        razor.RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(razorFailed);

        var loopbackOk = PageRenderResult.Ok("<html>loopback-ok</html>", content, LandingTemplate, SampleCulture);
        loopback.RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(loopbackOk);

        var (auto, sp) = BuildStrategy(cache, templateService, razor, loopback);
        using var spScope = sp;

        var result = await auto.RenderAsync(content, SampleUri, SampleCulture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(loopbackOk),
                "ModelBindingException must trigger fallback; Loopback's result must be returned");
            cache.Received(1).MarkHijacked(LandingDoctype, LandingTemplate);
            razor.Received(1).RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>());
            loopback.Received(1).RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task RenderAsync_RazorFailsOtherException_PropagatesFailure_NoFallback()
    {
        var content = StubPublishedContent(LandingDoctype, templateId: 42);
        var razor = Substitute.For<IPageRendererStrategy>();
        var loopback = Substitute.For<IPageRendererStrategy>();
        var cache = Substitute.For<IRendererStrategyCache>();
        var templateService = StubTemplateService(42, LandingTemplate);

        // Critical narrow-trigger pin: ANY exception type other than
        // ModelBindingException must propagate as Razor's Failed result
        // verbatim — NO cache write, NO Loopback attempt. Widening the
        // trigger here would silently mask unrelated bugs.
        var unrelatedException = new InvalidOperationException("transient Umbraco context error");
        var razorFailed = PageRenderResult.Failed(unrelatedException, content, LandingTemplate, SampleCulture);
        razor.RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(razorFailed);

        var (auto, sp) = BuildStrategy(cache, templateService, razor, loopback);
        using var spScope = sp;

        var result = await auto.RenderAsync(content, SampleUri, SampleCulture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(razorFailed),
                "Non-ModelBindingException failures must propagate verbatim");
            cache.DidNotReceive().MarkHijacked(Arg.Any<string>(), Arg.Any<string>());
            loopback.DidNotReceive().RenderAsync(
                Arg.Any<IPublishedContent>(), Arg.Any<Uri>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task RenderAsync_CacheHit_SkipsRazor_GoesStraightToLoopback()
    {
        var content = StubPublishedContent(LandingDoctype, templateId: 42);
        var razor = Substitute.For<IPageRendererStrategy>();
        var loopback = Substitute.For<IPageRendererStrategy>();
        var cache = Substitute.For<IRendererStrategyCache>();
        var templateService = StubTemplateService(42, LandingTemplate);

        cache.IsHijacked(LandingDoctype, LandingTemplate).Returns(true);

        var loopbackOk = PageRenderResult.Ok("<html>loopback-fast</html>", content, LandingTemplate, SampleCulture);
        loopback.RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(loopbackOk);

        var (auto, sp) = BuildStrategy(cache, templateService, razor, loopback);
        using var spScope = sp;

        var result = await auto.RenderAsync(content, SampleUri, SampleCulture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(loopbackOk),
                "cache hit must skip Razor and return Loopback's result");
            razor.DidNotReceive().RenderAsync(
                Arg.Any<IPublishedContent>(), Arg.Any<Uri>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            loopback.Received(1).RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>());
            cache.DidNotReceive().MarkHijacked(Arg.Any<string>(), Arg.Any<string>());
        });
    }

    /// <summary>
    /// AC4 mixed-template invariant — TWO calls with the same content but
    /// resolving to different template aliases must produce distinct cache
    /// entries. Call 1: Razor fails with ModelBindingException → cache marks
    /// <c>(landingPage, landingPage)</c>. Call 2: cache lookup for
    /// <c>(landingPage, landingPagePrint)</c> returns false (different
    /// tuple slot) → Razor is attempted again and succeeds → no cache write
    /// for the alternate template. Verifies the cache key independence.
    /// </summary>
    [Test]
    public async Task RenderAsync_MixedTemplate_OnlyHijackedTemplateCachesAsHijacked()
    {
        // Two distinct IPublishedContent stubs with different TemplateId
        // values — mirrors the production scenario of a doctype with two
        // templates (default vs print) where only the default is hijacked.
        var defaultContent = StubPublishedContent(LandingDoctype, templateId: 42);
        var printContent = StubPublishedContent(LandingDoctype, templateId: 43);

        // Real RendererStrategyCache used here (not a substitute) — the test
        // needs the real tuple-key semantics to verify the cache STATE after
        // both calls.
        var cache = new RendererStrategyCache();

        var razor = Substitute.For<IPageRendererStrategy>();
        var loopback = Substitute.For<IPageRendererStrategy>();

        // Template service resolves each TemplateId to its respective alias.
        var templateService = Substitute.For<ITemplateService>();
        var landingTemplate = Substitute.For<ITemplate>();
        landingTemplate.Alias.Returns(LandingTemplate);
        var printTemplate = Substitute.For<ITemplate>();
        printTemplate.Alias.Returns(LandingPrintTemplate);
        templateService.GetAsync(42).Returns(landingTemplate);
        templateService.GetAsync(43).Returns(printTemplate);

        // Call 1: default template hijacked — Razor fails with ModelBindingException.
        var razorFail = PageRenderResult.Failed(
            new ModelBindingException("default template hijacked"), defaultContent, LandingTemplate, SampleCulture);
        razor.RenderAsync(defaultContent, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(razorFail);
        // Call 2: alternate template clean — Razor succeeds.
        var razorOk = PageRenderResult.Ok(
            "<html>alt-ok</html>", printContent, LandingPrintTemplate, SampleCulture);
        razor.RenderAsync(printContent, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(razorOk);

        // Loopback only fires for call 1 (the cache-write path).
        var loopbackOk = PageRenderResult.Ok(
            "<html>loopback-ok</html>", defaultContent, LandingTemplate, SampleCulture);
        loopback.RenderAsync(defaultContent, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(loopbackOk);

        var (auto, sp) = BuildStrategy(cache, templateService, razor, loopback);
        using var spScope = sp;

        // Call 1 — default template, hijacked.
        var result1 = await auto.RenderAsync(defaultContent, SampleUri, SampleCulture, CancellationToken.None);
        // Call 2 — alternate template, clean.
        var result2 = await auto.RenderAsync(printContent, SampleUri, SampleCulture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result1, Is.SameAs(loopbackOk), "call 1 must go through Loopback (hijacked)");
            Assert.That(result2, Is.SameAs(razorOk),
                "call 2 must succeed via Razor — cache miss for the alternate template tuple");

            Assert.That(razor.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IPageRendererStrategy.RenderAsync)),
                Is.EqualTo(2), "Razor must be attempted on BOTH calls — alternate template's cache key is distinct");
            Assert.That(loopback.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IPageRendererStrategy.RenderAsync)),
                Is.EqualTo(1), "Loopback fires exactly once — only call 1 triggered the fallback");

            // Direct cache-state assertions: only the hijacked tuple is
            // marked; the alternate-template tuple remains unmarked.
            Assert.That(cache.IsHijacked(LandingDoctype, LandingTemplate), Is.True,
                "hijacked-template tuple must be cached");
            Assert.That(cache.IsHijacked(LandingDoctype, LandingPrintTemplate), Is.False,
                "alternate-template tuple must NOT be over-cached — the architectural invariant the tuple key exists to protect");
        });
    }

    /// <summary>
    /// AC5 — the no-template-resolved case (either <c>TemplateId</c> is
    /// null/zero OR <c>ITemplateService.GetAsync</c> returns null) must cache
    /// against the <see cref="string.Empty"/> template-alias slot per
    /// architecture.md:1284. Parameterised because both inputs produce the
    /// same observable outcome.
    /// </summary>
    [TestCase(null, false, TestName = "TemplateId is null — service.GetAsync skipped, empty-slot used")]
    [TestCase(0, false, TestName = "TemplateId is 0 — service.GetAsync skipped, empty-slot used")]
    [TestCase(42, true, TestName = "TemplateService.GetAsync returns null — empty-slot used")]
    public async Task RenderAsync_TemplateAliasUnresolved_CacheKeyUsesEmptyTemplateAlias(
        int? templateId,
        bool callsTemplateService)
    {
        var content = StubPublishedContent(LandingDoctype, templateId);
        var razor = Substitute.For<IPageRendererStrategy>();
        var loopback = Substitute.For<IPageRendererStrategy>();
        var cache = Substitute.For<IRendererStrategyCache>();
        var templateService = Substitute.For<ITemplateService>();
        // GetAsync returns null for the templateId-but-service-returns-null
        // scenario; defensive for the other scenarios (the strategy
        // short-circuits before reaching GetAsync when TemplateId is null/0).
        templateService.GetAsync(Arg.Any<int>()).Returns((ITemplate?)null);

        var razorFail = PageRenderResult.Failed(
            new ModelBindingException("hijack with no template"), content, templateAlias: null, SampleCulture);
        razor.RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(razorFail);

        var loopbackOk = PageRenderResult.Ok(
            "<html>loopback-no-template</html>", content, templateAlias: null, SampleCulture);
        loopback.RenderAsync(content, SampleUri, SampleCulture, Arg.Any<CancellationToken>()).Returns(loopbackOk);

        var (auto, sp) = BuildStrategy(cache, templateService, razor, loopback);
        using var spScope = sp;

        var result = await auto.RenderAsync(content, SampleUri, SampleCulture, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(loopbackOk), "fallback fired against empty-template-alias key");
            cache.Received(1).MarkHijacked(LandingDoctype, string.Empty);

            if (callsTemplateService)
            {
                templateService.Received(1).GetAsync(Arg.Any<int>());
            }
            else
            {
                templateService.DidNotReceive().GetAsync(Arg.Any<int>());
            }
        });
    }

    /// <summary>
    /// Pins null-safe pass-through of <c>culture</c> across both the
    /// Razor-success branch and the ModelBindingException-fallback branch.
    /// <c>IPageRendererStrategy.RenderAsync</c> accepts <c>string?</c> —
    /// production callers can pass <c>null</c> when content negotiation
    /// yields no Accept-Language preference.
    /// </summary>
    [TestCase(false, TestName = "culture: null — Razor success branch")]
    [TestCase(true, TestName = "culture: null — ModelBindingException fallback branch")]
    public async Task RenderAsync_CultureNull_PassesThroughToSiblingStrategy(bool razorFailsWithModelBindingException)
    {
        var content = StubPublishedContent(LandingDoctype, templateId: 42);
        var razor = Substitute.For<IPageRendererStrategy>();
        var loopback = Substitute.For<IPageRendererStrategy>();
        var cache = Substitute.For<IRendererStrategyCache>();
        var templateService = StubTemplateService(42, LandingTemplate);

        if (razorFailsWithModelBindingException)
        {
            var razorFail = PageRenderResult.Failed(
                new ModelBindingException("hijack"), content, LandingTemplate, resolvedCulture: null);
            razor.RenderAsync(content, SampleUri, null, Arg.Any<CancellationToken>()).Returns(razorFail);
            var loopbackOk = PageRenderResult.Ok("<html>loopback-ok</html>", content, LandingTemplate, resolvedCulture: null);
            loopback.RenderAsync(content, SampleUri, null, Arg.Any<CancellationToken>()).Returns(loopbackOk);
        }
        else
        {
            var razorOk = PageRenderResult.Ok("<html>razor-ok</html>", content, LandingTemplate, resolvedCulture: null);
            razor.RenderAsync(content, SampleUri, null, Arg.Any<CancellationToken>()).Returns(razorOk);
        }

        var (auto, sp) = BuildStrategy(cache, templateService, razor, loopback);
        using var spScope = sp;

        await auto.RenderAsync(content, SampleUri, culture: null, CancellationToken.None);

        // The null culture must reach the sibling strategy verbatim — no
        // string.Empty coercion, no default-locale substitution.
        await razor.Received(1).RenderAsync(content, SampleUri, null, Arg.Any<CancellationToken>());
        if (razorFailsWithModelBindingException)
        {
            await loopback.Received(1).RenderAsync(content, SampleUri, null, Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public void RenderAsync_CancellationRequested_PropagatesOperationCanceledException()
    {
        var content = StubPublishedContent(LandingDoctype, templateId: 42);
        var razor = Substitute.For<IPageRendererStrategy>();
        var loopback = Substitute.For<IPageRendererStrategy>();
        var cache = Substitute.For<IRendererStrategyCache>();
        var templateService = StubTemplateService(42, LandingTemplate);

        var (auto, sp) = BuildStrategy(cache, templateService, razor, loopback);
        using var spScope = sp;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await auto.RenderAsync(content, SampleUri, SampleCulture, cts.Token));

        // Pre-cancellation: neither sibling strategy nor the cache is touched.
        razor.DidNotReceive().RenderAsync(
            Arg.Any<IPublishedContent>(), Arg.Any<Uri>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        loopback.DidNotReceive().RenderAsync(
            Arg.Any<IPublishedContent>(), Arg.Any<Uri>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        cache.DidNotReceive().MarkHijacked(Arg.Any<string>(), Arg.Any<string>());
    }

    private static (AutoPageRendererStrategy Strategy, ServiceProvider Sp) BuildStrategy(
        IRendererStrategyCache cache,
        ITemplateService templateService,
        IPageRendererStrategy razor,
        IPageRendererStrategy loopback)
    {
        var services = new ServiceCollection();

        // Sibling strategies — Auto resolves these via captured IServiceProvider
        // GetRequiredKeyedService calls. Instance-factory keyed transient matches
        // the production composer's TryAddKeyedTransient shape and returns the
        // captured substitute every time so Received() assertions still see
        // the invocations.
        services.AddKeyedTransient<IPageRendererStrategy>(RenderStrategyMode.Razor, (_, _) => razor);
        services.AddKeyedTransient<IPageRendererStrategy>(RenderStrategyMode.Loopback, (_, _) => loopback);

        // Cache + template service + logger.
        services.AddSingleton(cache);
        services.AddSingleton(templateService);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        // Build SP and resolve Auto directly (NOT through the orchestrator —
        // this fixture tests the Auto strategy in isolation; the orchestrator
        // is covered by PageRendererTests).
        services.AddTransient<AutoPageRendererStrategy>();

        var sp = services.BuildServiceProvider();
        var auto = sp.GetRequiredService<AutoPageRendererStrategy>();
        return (auto, sp);
    }

    private static IPublishedContent StubPublishedContent(string contentTypeAlias, int? templateId)
    {
        var content = Substitute.For<IPublishedContent>();
        var contentType = Substitute.For<IPublishedContentType>();
        contentType.Alias.Returns(contentTypeAlias);
        content.ContentType.Returns(contentType);
        content.TemplateId.Returns(templateId);
        content.Key.Returns(Guid.Parse("00000000-0000-0000-0000-000000000042"));
        return content;
    }

    private static ITemplateService StubTemplateService(int templateId, string templateAlias)
    {
        var service = Substitute.For<ITemplateService>();
        var template = Substitute.For<ITemplate>();
        template.Alias.Returns(templateAlias);
        service.GetAsync(templateId).Returns(template);
        return service;
    }
}
