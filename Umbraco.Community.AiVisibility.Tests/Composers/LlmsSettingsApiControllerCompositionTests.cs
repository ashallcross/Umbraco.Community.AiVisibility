using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Backoffice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace LlmsTxt.Umbraco.Tests.Composers;

/// <summary>
/// Story 3.2 AC10 — DI lifetime correctness gate for
/// <see cref="SettingsManagementApiController"/>. Mirrors Story 2.2's
/// <c>Compose_StartupValidation_LlmsFullBuilder_NoCaptiveDependency</c> +
/// Story 3.1's <c>Compose_StartupValidation_LlmsSettingsResolver_NoCaptiveDependency</c>
/// — the canonical DI gate per project-context.md § Testing Rules.
/// </summary>
[TestFixture]
public class LlmsSettingsApiControllerCompositionTests
{
    [Test]
    public void Compose_StartupValidation_LlmsSettingsApiController_NoCaptiveDependency()
    {
        // Build the dep graph the controller actually consumes — same shape as
        // SettingsComposerTests.StubResolverDependencies. Register the
        // controller as Transient (ASP.NET Core's IControllerActivator does
        // this automatically at runtime; ValidateOnBuild only walks DI-known
        // descriptors, so we register explicitly to force the dep-graph walk).
        var services = new ServiceCollection();

        // Resolver — Scoped (matches SettingsComposer's TryAddScoped).
        services.AddScoped<ISettingsResolver>(_ => Substitute.For<ISettingsResolver>());

        // Cross-Umbraco services — match the lifetimes Umbraco itself uses.
        services.AddScoped(_ => Substitute.For<IContentService>());
        services.AddScoped(_ => Substitute.For<IContentTypeService>());
        services.AddScoped(_ => Substitute.For<IUmbracoContextAccessor>());
        services.AddScoped(_ => Substitute.For<IDocumentNavigationQueryService>());
        services.AddScoped(_ => Substitute.For<IPublishedUrlProvider>());
        services.AddSingleton(_ => Substitute.For<IOptionsMonitor<AiVisibilitySettings>>());
        services.AddSingleton(new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache())));
        services.AddLogging();

        // The controller is the unit under validation — Transient (ASP.NET
        // Core's default for controllers).
        services.AddTransient<SettingsManagementApiController>();

        Assert.DoesNotThrow(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            using var scope = provider.CreateScope();
            var controller = scope.ServiceProvider.GetRequiredService<SettingsManagementApiController>();
            Assert.That(controller, Is.Not.Null);
        }, "ValidateOnBuild + ValidateScopes must succeed — Transient controller with Scoped/Singleton deps is captive-free");
    }
}
