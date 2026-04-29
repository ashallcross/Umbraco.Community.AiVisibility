using System.Linq;
using LlmsTxt.Umbraco.Caching;
using LlmsTxt.Umbraco.Composers;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed class FakeAdopterExtractor : IMarkdownContentExtractor
    {
        public Task<MarkdownExtractionResult> ExtractAsync(
            IPublishedContent content, string? culture, CancellationToken ct)
            => throw new NotImplementedException("test fake — never invoked");
    }
}
