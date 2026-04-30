using LlmsTxt.Umbraco.Builders;
using LlmsTxt.Umbraco.Composers;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace LlmsTxt.Umbraco.Tests.Composers;

[TestFixture]
public class BuildersComposerTests
{
    [Test]
    public void Compose_RegistersILlmsTxtBuilderAsTransient()
    {
        // Story 2.1 Spec Drift Note #7 — `ILlmsTxtBuilder` must be Transient, NOT
        // Singleton. The default builder pulls `IMarkdownContentExtractor` (transient)
        // whose decorator factory pulls scoped `IOptionsSnapshot<LlmsTxtSettings>`.
        // Singleton-holding-transient with scoped sub-deps is a captive dependency
        // — it crashed the first manual-gate hit. This test pins the lifetime so a
        // future change can't silently re-introduce the bug.
        var (composer, builder, services) = BuildComposer();

        composer.Compose(builder);

        var descriptor = services.Single(d => d.ServiceType == typeof(ILlmsTxtBuilder));
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Lifetime, Is.EqualTo(ServiceLifetime.Transient),
                "ILlmsTxtBuilder must be Transient — Singleton would form a captive "
                + "dependency on IOptionsSnapshot<LlmsTxtSettings> via the extractor's "
                + "caching decorator (Spec Drift Note #7).");
            Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(DefaultLlmsTxtBuilder)),
                "default implementation is DefaultLlmsTxtBuilder");
        });
    }

    [Test]
    public void Compose_RegistersIHostnameRootResolverAsSingleton()
    {
        var (composer, builder, services) = BuildComposer();

        composer.Compose(builder);

        var descriptor = services.Single(d => d.ServiceType == typeof(IHostnameRootResolver));
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Lifetime, Is.EqualTo(ServiceLifetime.Singleton),
                "IHostnameRootResolver is a pure function over IDomainService + the "
                + "active IUmbracoContext; safe as Singleton.");
            Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(HostnameRootResolver)));
        });
    }

    [Test]
    public void AdopterOverride_PreRegisteredBeforeComposer_DefaultDoesNotReplace()
    {
        // Spec Task 9.2: "Adopter override (ILlmsTxtBuilder swapped) → controller
        // uses adopter's builder, default never instantiated."
        // Path 1 — adopter registers BEFORE BuildersComposer runs (TryAdd discipline):
        // our composer's TryAddTransient must notice the existing registration and
        // bow out, leaving the adopter's ServiceDescriptor untouched.
        var (composer, builder, services) = BuildComposer();
        services.AddTransient<ILlmsTxtBuilder, NoOpAdopterBuilder>();

        composer.Compose(builder);

        var registrations = services
            .Where(d => d.ServiceType == typeof(ILlmsTxtBuilder))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(registrations, Has.Length.EqualTo(1),
                "TryAdd* must not stack a duplicate registration");
            Assert.That(registrations[0].ImplementationType, Is.EqualTo(typeof(NoOpAdopterBuilder)),
                "adopter's pre-registration wins; DefaultLlmsTxtBuilder is NOT registered");
        });
    }

    [Test]
    public void AdopterOverride_PostRegisteredAfterComposer_LastWinsResolvesAdopter()
    {
        // Path 2 — adopter registers AFTER BuildersComposer (e.g.
        // [ComposeAfter(typeof(BuildersComposer))] + services.AddTransient).
        // DI's last-registration-wins semantics means GetService returns the adopter
        // even though both descriptors stay in the collection.
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        services.AddTransient<ILlmsTxtBuilder, NoOpAdopterBuilder>();

        // Build the provider with stub dependencies for DefaultLlmsTxtBuilder so
        // resolution doesn't NPE if the framework happens to instantiate it. The
        // assertion is about *which* concrete type comes out, not whether the
        // default is buildable.
        services.AddTransient<IPublishedUrlProvider>(_ => Substitute.For<IPublishedUrlProvider>());
        services.AddTransient<IPublishedValueFallback>(_ => Substitute.For<IPublishedValueFallback>());
        services.AddTransient<IMarkdownContentExtractor>(_ => Substitute.For<IMarkdownContentExtractor>());
        services.AddSingleton(_ => Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        services.AddTransient(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Logger<>));

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ILlmsTxtBuilder>();

        Assert.That(resolved, Is.InstanceOf<NoOpAdopterBuilder>(),
            "last-registration-wins: adopter's post-composer registration is what GetService returns");
    }

    private static (BuildersComposer Composer, IUmbracoBuilder Builder, IServiceCollection Services)
        BuildComposer()
    {
        var services = new ServiceCollection();
        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        return (new BuildersComposer(), builder, services);
    }

    private sealed class NoOpAdopterBuilder : ILlmsTxtBuilder
    {
        public Task<string> BuildAsync(LlmsTxtBuilderContext context, CancellationToken cancellationToken)
            => Task.FromResult("OVERRIDDEN");
    }
}
