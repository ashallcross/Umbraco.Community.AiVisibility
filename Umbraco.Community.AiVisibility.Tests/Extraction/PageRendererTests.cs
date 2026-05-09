using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;

namespace Umbraco.Community.AiVisibility.Tests.Extraction;

/// <summary>
/// Story 7.1 AC3 + AC8 — dispatcher behaviour for the new-shape thin-orchestrator
/// <see cref="PageRenderer"/>. Tests prove (a) the orchestrator delegates to
/// the keyed strategy resolved from the DI container without doing any of its
/// own rendering work, and (b) when the configured Mode resolves to a strategy
/// that is NOT registered (Loopback in 7.1, Auto in 7.1) the orchestrator
/// surfaces a project-context-aware diagnostic naming the missing strategy and
/// the Story that ships it — fail-loud, no silent fall-back.
/// </summary>
[TestFixture]
public class PageRendererTests
{
    /// <summary>
    /// AC3 happy path — the orchestrator reads
    /// <c>RenderStrategy.Mode</c>, resolves the keyed strategy, and forwards
    /// the four <c>RenderAsync</c> arguments unchanged. The returned
    /// <see cref="PageRenderResult"/> is the strategy's, byte-for-byte.
    /// </summary>
    [Test]
    public async Task RenderAsync_ModeRazor_DelegatesToRazorStrategy()
    {
        var content = Substitute.For<IPublishedContent>();
        var uri = new Uri("https://example.test/foo");
        const string culture = "en-GB";
        var ct = CancellationToken.None;

        var stubResult = PageRenderResult.Ok(
            html: "<html>delegated</html>",
            content: content,
            templateAlias: "fooDoctype",
            resolvedCulture: culture);

        var razorStrategy = Substitute.For<IPageRendererStrategy>();
        razorStrategy.RenderAsync(content, uri, culture, ct).Returns(stubResult);

        var (renderer, sp) = BuildRenderer(
            mode: RenderStrategyMode.Razor,
            keyedStrategies: new[] { (RenderStrategyMode.Razor, razorStrategy) });
        using var _ = sp;

        var result = await renderer.RenderAsync(content, uri, culture, ct);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(stubResult),
                "orchestrator must return the strategy's result unchanged");
            razorStrategy.Received(1).RenderAsync(content, uri, culture, ct);
        });
    }

    /// <summary>
    /// AC3 + Failure & Edge Case 1 — pinning Mode=Loopback before the
    /// Loopback strategy ships throws an <see cref="InvalidOperationException"/>
    /// whose message names both <c>Mode=Loopback</c> AND the missing strategy
    /// (<c>LoopbackPageRendererStrategy</c>). NO silent fall-back to Razor.
    /// </summary>
    [Test]
    public void RenderAsync_ModeLoopback_ThrowsInvalidOperationExceptionWithDiagnostic()
    {
        var content = Substitute.For<IPublishedContent>();
        var uri = new Uri("https://example.test/foo");

        var (renderer, sp) = BuildRenderer(
            mode: RenderStrategyMode.Loopback,
            keyedStrategies: new (RenderStrategyMode, IPageRendererStrategy)[]
            {
                (RenderStrategyMode.Razor, Substitute.For<IPageRendererStrategy>()),
                // Loopback intentionally NOT registered — a future release ships it.
            });
        using var _ = sp;

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await renderer.RenderAsync(content, uri, culture: null, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Mode=Loopback"),
                "diagnostic must name the requested mode explicitly");
            Assert.That(ex.Message, Does.Contain("LoopbackPageRendererStrategy"),
                "diagnostic must name the missing strategy that an upcoming release ships");
            Assert.That(ex.Message, Does.Contain("Pin Mode=Razor"),
                "diagnostic must name the workaround (pin Razor) so adopters can recover");
        });
    }

    /// <summary>
    /// AC3 + Failure & Edge Case 1 — sibling for Mode=Auto. Until the Auto
    /// strategy ships, pinning Mode=Auto must fail loudly with the missing
    /// strategy named.
    /// </summary>
    [Test]
    public void RenderAsync_ModeAuto_ThrowsInvalidOperationExceptionWithDiagnostic()
    {
        var content = Substitute.For<IPublishedContent>();
        var uri = new Uri("https://example.test/foo");

        var (renderer, sp) = BuildRenderer(
            mode: RenderStrategyMode.Auto,
            keyedStrategies: new (RenderStrategyMode, IPageRendererStrategy)[]
            {
                (RenderStrategyMode.Razor, Substitute.For<IPageRendererStrategy>()),
                // Auto + Loopback intentionally NOT registered — future releases ship them.
            });
        using var _ = sp;

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await renderer.RenderAsync(content, uri, culture: null, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Mode=Auto"),
                "diagnostic must name the requested mode explicitly");
            Assert.That(ex.Message, Does.Contain("AutoPageRendererStrategy"),
                "diagnostic must name the missing strategy that an upcoming release ships");
            Assert.That(ex.Message, Does.Contain("Pin Mode=Razor"),
                "diagnostic must name the workaround (pin Razor) so adopters can recover");
        });
    }

    /// <summary>
    /// Build a real <see cref="PageRenderer"/> backed by a real
    /// <see cref="ServiceProvider"/>. The orchestrator's keyed-service
    /// lookup exercises the true container's "no registration for this key"
    /// shape (which the orchestrator surfaces as a diagnostic
    /// <see cref="InvalidOperationException"/>).
    /// <para>
    /// Returns the <see cref="ServiceProvider"/> alongside the
    /// <see cref="PageRenderer"/> so the test can <c>using</c>-dispose it
    /// (every fixture-built provider owns the substitute strategies plus
    /// the keyed-service registry; leaking would accumulate across the
    /// fixture's lifetime).
    /// </para>
    /// <para>
    /// Strategies are registered as keyed transients via an instance
    /// factory rather than <c>AddKeyedSingleton</c> — the production
    /// composer registers the Razor strategy as
    /// <c>TryAddKeyedTransient&lt;IPageRendererStrategy, RazorPageRendererStrategy&gt;</c>
    /// (the strategy's per-render <c>IVariationContextAccessor.VariationContext</c>
    /// mutation makes Singleton lifetime unsafe). The factory delegate
    /// returns the same substitute instance the test holds a reference
    /// to, so assertions like <c>razorStrategy.Received(1).RenderAsync(...)</c>
    /// still observe the call — the lifetime is Transient by registration
    /// shape but functionally Singleton because the factory returns the
    /// same captured instance every time.
    /// </para>
    /// </summary>
    private static (PageRenderer Renderer, ServiceProvider Sp) BuildRenderer(
        RenderStrategyMode mode,
        IEnumerable<(RenderStrategyMode Key, IPageRendererStrategy Strategy)> keyedStrategies)
    {
        var settings = new AiVisibilitySettings
        {
            RenderStrategy = new RenderStrategySettings { Mode = mode },
        };
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(settings);

        var services = new ServiceCollection();
        services.AddSingleton(monitor);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        foreach (var (key, strategy) in keyedStrategies)
        {
            services.AddKeyedTransient<IPageRendererStrategy>(key, (_, _) => strategy);
        }

        var sp = services.BuildServiceProvider();
        var renderer = new PageRenderer(sp, monitor, NullLogger<PageRenderer>.Instance);
        return (renderer, sp);
    }
}
