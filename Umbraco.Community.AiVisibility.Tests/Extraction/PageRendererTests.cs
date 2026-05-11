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
/// <see cref="PageRenderer"/>. Tests prove the orchestrator delegates to
/// the keyed strategy resolved from the DI container without doing any of its
/// own rendering work. Story 7.2 added the Loopback-delegation test
/// (replacing the original Loopback-throw test now that Loopback is
/// registered); Story 7.3 added the Auto-delegation test (replacing the
/// original Auto-throw test now that Auto is registered). All three keyed
/// strategies in production are now live; the only remaining throw shape
/// covers adopters who pin a custom <c>RenderStrategyMode</c> value without
/// registering a sibling strategy.
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
    /// Story 7.2 — replaces the Story 7.1 Loopback-throw test now that the
    /// Loopback strategy is registered. Mirror of
    /// <see cref="RenderAsync_ModeRazor_DelegatesToRazorStrategy"/>: the
    /// orchestrator reads <c>Mode=Loopback</c>, resolves the keyed strategy,
    /// and forwards arguments unchanged.
    /// </summary>
    [Test]
    public async Task RenderAsync_ModeLoopback_DelegatesToLoopbackStrategy()
    {
        var content = Substitute.For<IPublishedContent>();
        var uri = new Uri("https://example.test/foo");
        const string culture = "en-GB";
        var ct = CancellationToken.None;

        var stubResult = PageRenderResult.Ok(
            html: "<html>loopback-delegated</html>",
            content: content,
            templateAlias: "fooDoctype",
            resolvedCulture: culture);

        var loopbackStrategy = Substitute.For<IPageRendererStrategy>();
        loopbackStrategy.RenderAsync(content, uri, culture, ct).Returns(stubResult);

        var (renderer, sp) = BuildRenderer(
            mode: RenderStrategyMode.Loopback,
            keyedStrategies: new[]
            {
                (RenderStrategyMode.Razor, Substitute.For<IPageRendererStrategy>()),
                (RenderStrategyMode.Loopback, loopbackStrategy),
            });
        using var _ = sp;

        var result = await renderer.RenderAsync(content, uri, culture, ct);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(stubResult),
                "orchestrator must return the strategy's result unchanged");
            loopbackStrategy.Received(1).RenderAsync(content, uri, culture, ct);
        });
    }

    /// <summary>
    /// Story 7.3 — replaces the original Auto-throw test now that the Auto
    /// strategy is registered. Mirror of
    /// <see cref="RenderAsync_ModeRazor_DelegatesToRazorStrategy"/>: the
    /// orchestrator reads <c>Mode=Auto</c>, resolves the keyed strategy, and
    /// forwards arguments unchanged.
    /// </summary>
    [Test]
    public async Task RenderAsync_ModeAuto_DelegatesToAutoStrategy()
    {
        var content = Substitute.For<IPublishedContent>();
        var uri = new Uri("https://example.test/foo");
        const string culture = "en-GB";
        var ct = CancellationToken.None;

        var stubResult = PageRenderResult.Ok(
            html: "<html>auto-delegated</html>",
            content: content,
            templateAlias: "fooDoctype",
            resolvedCulture: culture);

        var autoStrategy = Substitute.For<IPageRendererStrategy>();
        autoStrategy.RenderAsync(content, uri, culture, ct).Returns(stubResult);

        var (renderer, sp) = BuildRenderer(
            mode: RenderStrategyMode.Auto,
            keyedStrategies: new[]
            {
                (RenderStrategyMode.Razor, Substitute.For<IPageRendererStrategy>()),
                (RenderStrategyMode.Loopback, Substitute.For<IPageRendererStrategy>()),
                (RenderStrategyMode.Auto, autoStrategy),
            });
        using var _ = sp;

        var result = await renderer.RenderAsync(content, uri, culture, ct);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(stubResult),
                "orchestrator must return the strategy's result unchanged");
            autoStrategy.Received(1).RenderAsync(content, uri, culture, ct);
        });
    }

    /// <summary>
    /// Story 7.3 — Failure & Edge Case 11. Adopters pinning a custom
    /// <see cref="RenderStrategyMode"/> integer value without a corresponding
    /// keyed registration must see the orchestrator's neutral fail-loud
    /// diagnostic. With Razor + Loopback + Auto now all registered, this is
    /// the remaining throw branch — covering adopter-shaped misconfigurations
    /// (numeric out-of-range bind producing an undefined enum value, or a
    /// custom strategy registration that was removed by an adopter composer
    /// running after the package composer).
    /// </summary>
    [Test]
    public void RenderAsync_ModeUnregisteredCustomValue_ThrowsInvalidOperationExceptionWithDiagnostic()
    {
        var content = Substitute.For<IPublishedContent>();
        var uri = new Uri("https://example.test/foo");

        // Cast int 99 into RenderStrategyMode — simulates a numeric
        // out-of-range bind (e.g. {"Mode": "99"}) producing an undefined
        // enum value that lands in the dispatcher's no-registration branch.
        var unregisteredMode = (RenderStrategyMode)99;

        var (renderer, sp) = BuildRenderer(
            mode: unregisteredMode,
            keyedStrategies: new[]
            {
                (RenderStrategyMode.Razor, Substitute.For<IPageRendererStrategy>()),
                (RenderStrategyMode.Loopback, Substitute.For<IPageRendererStrategy>()),
                (RenderStrategyMode.Auto, Substitute.For<IPageRendererStrategy>()),
            });
        using var _ = sp;

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await renderer.RenderAsync(content, uri, culture: null, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Mode=99"),
                "diagnostic must name the requested mode explicitly so the operator can map it back to the offending config value");
            Assert.That(ex.Message, Does.Contain("Mode=Auto"),
                "diagnostic must name a known-good recovery Mode so the operator has an immediate way out of the misconfiguration");
            Assert.That(ex.Message, Does.Contain("services.TryAddKeyedTransient"),
                "diagnostic must name the canonical extension shape so the operator can wire a custom strategy");
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
