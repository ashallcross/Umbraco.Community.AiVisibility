using System.Linq;
using Umbraco.Community.AiVisibility.Caching;
using LlmsTxt.Umbraco.Composers;
using Umbraco.Community.AiVisibility.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace LlmsTxt.Umbraco.Tests.Composers;

[TestFixture]
public class CachingComposerTests
{
    [Test]
    public void IsAdopterOverride_NoRegistration_ReturnsFalse()
    {
        // RoutingComposer hasn't run yet — nothing to wrap; not an adopter-override.
        var services = new ServiceCollection();

        Assert.That(CachingComposer.IsAdopterOverride(services), Is.False);
    }

    [Test]
    public void IsAdopterOverride_DefaultExtractor_ReturnsFalse()
    {
        var services = new ServiceCollection();
        services.AddTransient<IMarkdownContentExtractor, DefaultMarkdownContentExtractor>();

        Assert.That(CachingComposer.IsAdopterOverride(services), Is.False);
    }

    [Test]
    public void IsAdopterOverride_AdopterCustomExtractor_ReturnsTrue()
    {
        var services = new ServiceCollection();
        services.AddTransient<IMarkdownContentExtractor, FakeAdopterExtractor>();

        Assert.That(CachingComposer.IsAdopterOverride(services), Is.True);
    }

    [Test]
    public void DecorateDefaultExtractor_ReplacesTypeRegistrationWithFactory()
    {
        // The default registration uses ImplementationType. After decoration the
        // descriptor must be a factory delegate (constructing the decorator), with
        // the same service type and exactly one registration.
        var services = new ServiceCollection();
        services.AddTransient<IMarkdownContentExtractor, DefaultMarkdownContentExtractor>();

        CachingComposer.DecorateDefaultExtractor(services);

        var registrations = services
            .Where(d => d.ServiceType == typeof(IMarkdownContentExtractor))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(registrations, Has.Length.EqualTo(1),
                "exactly one IMarkdownContentExtractor registration after decoration");
            Assert.That(registrations[0].ImplementationFactory, Is.Not.Null,
                "decorated registration must be a factory delegate (not ImplementationType)");
            Assert.That(registrations[0].ImplementationType, Is.Null,
                "ImplementationType is replaced when a factory is registered");
        });

        // The DefaultMarkdownContentExtractor should also be registered as a concrete
        // service so the decorator factory can resolve it.
        Assert.That(
            services.Any(d => d.ServiceType == typeof(DefaultMarkdownContentExtractor)),
            Is.True,
            "DefaultMarkdownContentExtractor must be registered as a concrete service");
    }

    [Test]
    public void CachingComposer_AdopterOverride_SkipsDecoration()
    {
        // Spec § Failure & Edge Cases: when the adopter has registered their own
        // IMarkdownContentExtractor BEFORE our composer runs, we must NOT wrap it —
        // the adopter's ServiceDescriptor stays put.
        var services = new ServiceCollection();
        services.AddTransient<IMarkdownContentExtractor, FakeAdopterExtractor>();

        // The composer's gate trips here, so DecorateDefaultExtractor is NOT called.
        // Asserting both halves: the gate detects the override AND the registration
        // remains the adopter's untouched.
        Assert.That(CachingComposer.IsAdopterOverride(services), Is.True);

        var registration = services.Single(d => d.ServiceType == typeof(IMarkdownContentExtractor));
        Assert.That(registration.ImplementationType, Is.EqualTo(typeof(FakeAdopterExtractor)),
            "adopter registration must remain unwrapped");
    }

    [Test]
    public void DecorateDefaultExtractor_RunTwice_RemainsSingleRegistration()
    {
        // Defensive: even if a future change accidentally re-runs decoration on the same
        // IServiceCollection, the IMarkdownContentExtractor registration count stays at 1.
        // Pins idempotency of the wrap operation against composer-graph re-execution.
        var services = new ServiceCollection();
        services.AddTransient<IMarkdownContentExtractor, DefaultMarkdownContentExtractor>();

        CachingComposer.DecorateDefaultExtractor(services);
        CachingComposer.DecorateDefaultExtractor(services);

        var registrations = services
            .Where(d => d.ServiceType == typeof(IMarkdownContentExtractor))
            .ToArray();

        Assert.That(registrations, Has.Length.EqualTo(1),
            "second DecorateDefaultExtractor call must not introduce a duplicate registration");
    }

    [Test]
    public async Task AdopterExtractorOverrideComponent_FirstBoot_LogsBypassAtInformationLevel()
    {
        // Pin the bypass logging contract documented in IMarkdownContentExtractor xmldoc.
        // Adopters debugging cache state need this signal at startup.
        var logger = Substitute.For<ILogger<CachingComposer>>();
        var component = new AdopterExtractorOverrideComponent(logger);

        await component.InitializeAsync(isRestart: false, CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o!.ToString()!.Contains("Adopter has overridden IMarkdownContentExtractor")),
            null,
            Arg.Any<Func<object, Exception?, string>>()!);
    }

    [Test]
    public async Task AdopterExtractorOverrideComponent_RestartInvocation_DoesNotLog()
    {
        // Umbraco fires InitializeAsync once on initial boot AND again on restart cycles
        // (via IRuntime restart). The `if (!isRestart)` guard inside
        // AdopterExtractorOverrideComponent suppresses the log on restart so admin logs
        // don't accumulate identical bypass notices on every config-driven restart.
        // Pairs with AdopterExtractorOverrideComponent_FirstBoot_LogsBypassAtInformationLevel
        // which proves the negative — together they pin both branches of the guard.
        // (Renamed from RestartCycle_DoesNotLogTwice for accuracy: this exercises a
        // single isRestart=true invocation, not a full boot+restart cycle on one
        // component instance — the component is stateless so the two-instance pair
        // is sufficient to pin the contract.)
        var logger = Substitute.For<ILogger<CachingComposer>>();
        var component = new AdopterExtractorOverrideComponent(logger);

        await component.InitializeAsync(isRestart: true, CancellationToken.None);

        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>()!);
    }

    private sealed class FakeAdopterExtractor : IMarkdownContentExtractor
    {
        public Task<MarkdownExtractionResult> ExtractAsync(
            IPublishedContent content, string? culture, CancellationToken ct)
            => throw new NotImplementedException("test fake — never invoked");
    }
}
