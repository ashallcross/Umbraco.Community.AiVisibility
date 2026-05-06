using Umbraco.Community.AiVisibility.LlmsTxt;
using Umbraco.Community.AiVisibility.Composing;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace Umbraco.Community.AiVisibility.Tests.Composing;

[TestFixture]
public class BuildersComposerTests
{
    [Test]
    public void Compose_RegistersILlmsTxtBuilderAsTransient()
    {
        // Story 2.1 Spec Drift Note #7 — `ILlmsTxtBuilder` must be Transient, NOT
        // Singleton. The default builder pulls `IMarkdownContentExtractor` (transient)
        // whose decorator factory pulls scoped `IOptionsSnapshot<AiVisibilitySettings>`.
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
                + "dependency on IOptionsSnapshot<AiVisibilitySettings> via the extractor's "
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

    // ────────────────────────────────────────────────────────────────────────
    // Story 2.2 — ILlmsFullBuilder lifetime + adopter override
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void Compose_RegistersILlmsFullBuilderAsTransient()
    {
        // Same captive-dependency reasoning as ILlmsTxtBuilder (Story 2.1 Spec Drift
        // Note #7). DefaultLlmsFullBuilder pulls IMarkdownContentExtractor (transient
        // with scoped sub-deps) so Singleton would form a captive dependency on the
        // root provider. This test pins the lifetime so a future change can't
        // silently re-introduce the bug for /llms-full.txt.
        var (composer, builder, services) = BuildComposer();

        composer.Compose(builder);

        var descriptor = services.Single(d => d.ServiceType == typeof(ILlmsFullBuilder));
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Lifetime, Is.EqualTo(ServiceLifetime.Transient),
                "ILlmsFullBuilder must be Transient (same captive-dependency reason as ILlmsTxtBuilder)");
            Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(DefaultLlmsFullBuilder)));
        });
    }

    [Test]
    public void AdopterOverride_LlmsFullBuilder_PreRegistered_DefaultDoesNotReplace()
    {
        var (composer, builder, services) = BuildComposer();
        services.AddTransient<ILlmsFullBuilder, NoOpAdopterFullBuilder>();

        composer.Compose(builder);

        var registrations = services.Where(d => d.ServiceType == typeof(ILlmsFullBuilder)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(registrations, Has.Length.EqualTo(1));
            Assert.That(registrations[0].ImplementationType, Is.EqualTo(typeof(NoOpAdopterFullBuilder)),
                "adopter's pre-registration wins");
        });
    }

    [Test]
    public void AdopterOverride_LlmsFullBuilder_PostRegistered_LastWinsResolvesAdopter()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        services.AddTransient<ILlmsFullBuilder, NoOpAdopterFullBuilder>();

        services.AddTransient<IPublishedUrlProvider>(_ => Substitute.For<IPublishedUrlProvider>());
        services.AddTransient<IMarkdownContentExtractor>(_ => Substitute.For<IMarkdownContentExtractor>());
        services.AddSingleton(_ => Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        services.AddTransient(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Logger<>));

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ILlmsFullBuilder>();

        Assert.That(resolved, Is.InstanceOf<NoOpAdopterFullBuilder>());
    }

    [Test]
    public void Compose_StartupValidation_LlmsFullBuilder_NoCaptiveDependency()
    {
        // ValidateOnBuild + ValidateScopes catches captive dependencies at
        // service-provider construction time. If a future refactor flips
        // ILlmsFullBuilder back to Singleton (or its dependency graph grows a
        // scoped service), this assertion fails before any request runs — same
        // belt-and-braces guard the Story 2.1 review added for ILlmsTxtBuilder.
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        // Stub the dep graph for BOTH builders the composer registers (the test
        // is about ILlmsFullBuilder, but ValidateOnBuild walks every descriptor
        // in the collection and would flag missing deps for ILlmsTxtBuilder too).
        services.AddTransient<IPublishedUrlProvider>(_ => Substitute.For<IPublishedUrlProvider>());
        services.AddTransient<IPublishedValueFallback>(_ => Substitute.For<IPublishedValueFallback>());
        services.AddTransient<IMarkdownContentExtractor>(_ => Substitute.For<IMarkdownContentExtractor>());
        // Stubs for HostnameRootResolver's ctor as well — it's registered as
        // Singleton by the composer so ValidateOnBuild walks its graph too.
        services.AddSingleton(_ => Substitute.For<global::Umbraco.Cms.Core.Services.IDomainService>());
#pragma warning disable CS0618 // ILocalizationService is obsolete — pinned by architecture line 81 for now (project-context.md)
        services.AddSingleton(_ => Substitute.For<global::Umbraco.Cms.Core.Services.ILocalizationService>());
#pragma warning restore CS0618
        services.AddSingleton(_ => Substitute.For<global::Umbraco.Cms.Core.Services.Navigation.IDocumentNavigationQueryService>());
        services.AddLogging();

        Assert.DoesNotThrow(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            var resolved = provider.GetRequiredService<ILlmsFullBuilder>();
            Assert.That(resolved, Is.Not.Null);
        }, "validation must succeed — Transient lifetime keeps the dep graph captive-free");
    }

    [Test]
    public void Compose_RegistersHreflangVariantsResolver_AsSingleton()
    {
        // Story 2.3 — resolver is stateless; deps are Umbraco singleton
        // abstractions; safe as Singleton.
        var (composer, builder, services) = BuildComposer();

        composer.Compose(builder);

        var descriptor = services.Single(d => d.ServiceType == typeof(IHreflangVariantsResolver));
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Lifetime, Is.EqualTo(ServiceLifetime.Singleton),
                "IHreflangVariantsResolver is stateless — Singleton matches the existing IHostnameRootResolver shape");
            Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(HreflangVariantsResolver)));
        });
    }

    [Test]
    public void Compose_StartupValidation_HreflangResolver_NoCaptiveDependency()
    {
        // Same belt-and-braces validation Story 2.1 + 2.2 added for the builders.
        // If a future refactor adds a scoped dep to HreflangVariantsResolver,
        // ValidateOnBuild fails at service-provider construction time.
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        services.AddTransient<IPublishedUrlProvider>(_ => Substitute.For<IPublishedUrlProvider>());
        services.AddTransient<IPublishedValueFallback>(_ => Substitute.For<IPublishedValueFallback>());
        services.AddTransient<IMarkdownContentExtractor>(_ => Substitute.For<IMarkdownContentExtractor>());
        services.AddSingleton(_ => Substitute.For<global::Umbraco.Cms.Core.Services.IDomainService>());
#pragma warning disable CS0618
        services.AddSingleton(_ => Substitute.For<global::Umbraco.Cms.Core.Services.ILocalizationService>());
#pragma warning restore CS0618
        services.AddSingleton(_ => Substitute.For<global::Umbraco.Cms.Core.Services.Navigation.IDocumentNavigationQueryService>());
        services.AddLogging();

        Assert.DoesNotThrow(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            var resolved = provider.GetRequiredService<IHreflangVariantsResolver>();
            Assert.That(resolved, Is.Not.Null);
        }, "validation must succeed — Singleton lifetime keeps the dep graph captive-free");
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

    private sealed class NoOpAdopterFullBuilder : ILlmsFullBuilder
    {
        public Task<string> BuildAsync(LlmsFullBuilderContext context, CancellationToken cancellationToken)
            => Task.FromResult("FULL OVERRIDDEN");
    }
}
