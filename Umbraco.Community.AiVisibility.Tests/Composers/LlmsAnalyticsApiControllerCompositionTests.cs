using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Controllers.Backoffice;
using Umbraco.Community.AiVisibility.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LlmsTxt.Umbraco.Tests.Composers;

/// <summary>
/// Story 5.2 AC12 — DI lifetime correctness gate for
/// <see cref="LlmsAnalyticsManagementApiController"/>. Mirrors Story 3.2's
/// <c>LlmsSettingsApiControllerCompositionTests</c> stub-driven shape per
/// project-context.md § Testing Rules — the canonical gate ratified at the
/// Epic 4 → 5 reconciliation gate (architecture.md:393).
/// </summary>
[TestFixture]
public class LlmsAnalyticsApiControllerCompositionTests
{
    [Test]
    public void Compose_StartupValidation_LlmsAnalyticsApiController_NoCaptiveDependency()
    {
        // Build the dep graph the controller actually consumes:
        //   - IAnalyticsReader (Singleton — internal default impl wraps
        //     IScopeProvider + NPoco; Story 5.2 testability seam)
        //   - IOptionsMonitor<LlmsTxtSettings> (Singleton)
        //   - TimeProvider (Singleton — TimeProvider.System per Story 5.1
        //     NotificationsComposer line 47).
        //   - ILogger<T> (Singleton — services.AddLogging registers it).
        // Register the controller as Transient (ASP.NET Core's IControllerActivator
        // does this at runtime; ValidateOnBuild only walks DI-known descriptors,
        // so we register explicitly to force the dep-graph walk).
        var services = new ServiceCollection();

        services.AddSingleton(_ => Substitute.For<IAnalyticsReader>());
        services.AddSingleton(_ => Substitute.For<IOptionsMonitor<LlmsTxtSettings>>());
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();

        services.AddTransient<LlmsAnalyticsManagementApiController>();

        Assert.DoesNotThrow(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            using var scope = provider.CreateScope();
            var controller = scope.ServiceProvider.GetRequiredService<LlmsAnalyticsManagementApiController>();
            Assert.That(controller, Is.Not.Null);
        }, "ValidateOnBuild + ValidateScopes must succeed — Transient controller with all-Singleton deps is captive-free");
    }
}
