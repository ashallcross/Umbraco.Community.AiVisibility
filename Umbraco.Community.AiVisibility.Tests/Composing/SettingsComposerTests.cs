using System.Collections.Generic;
using Umbraco.Community.AiVisibility.Caching;
using Umbraco.Community.AiVisibility.Composing;
using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;

namespace Umbraco.Community.AiVisibility.Tests.Composing;

[TestFixture]
public class SettingsComposerTests
{
    [Test]
    public void Compose_RegistersILlmsSettingsResolver_AsScoped()
    {
        // Story 3.1 AC7 — resolver MUST be Scoped (architecture.md line 377).
        // Singleton would form a captive dependency on root provider via
        // IUmbracoContextAccessor (request-scoped). Pinned by this test +
        // Compose_StartupValidation_LlmsSettingsResolver_NoCaptiveDependency.
        var (composer, builder, services) = BuildComposer(skipDoctype: false);

        composer.Compose(builder);

        var descriptor = services.Single(d => d.ServiceType == typeof(ISettingsResolver));
        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Lifetime, Is.EqualTo(ServiceLifetime.Scoped),
                "ISettingsResolver must be Scoped — Singleton would form a captive "
                + "dependency on IUmbracoContextAccessor (Story 3.1 AC7).");
            Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(DefaultSettingsResolver)),
                "default implementation is DefaultSettingsResolver");
        });
    }

    [Test]
    public void Compose_DoesNotAccessPackageMigrationPlans_WhenSkipFlagFalse()
    {
        // When SkipSettingsDoctype is false (the default), the composer must
        // NOT call PackageMigrationPlans() at all — the framework's
        // auto-discovery path registers the plan; we leave it alone.
        // NSubstitute on IUmbracoBuilder records all calls; assert the
        // collection-builder accessor was never invoked.
        var (composer, builder, _) = BuildComposer(skipDoctype: false);

        composer.Compose(builder);

        // Inspect ALL recorded calls for any WithCollectionBuilder invocation
        // (PackageMigrationPlans() is sugar over WithCollectionBuilder<…>).
        var collectionBuilderCalls = builder.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "WithCollectionBuilder");
        Assert.That(collectionBuilderCalls, Is.EqualTo(0),
            "with skipDoctype=false, the composer must not touch the PackageMigrationPlans collection builder");
    }

    [Test]
    public void Compose_AccessesPackageMigrationPlans_WhenSkipFlagTrue()
    {
        // When SkipSettingsDoctype is true (uSync coexistence path), the composer
        // MUST call PackageMigrationPlans().Remove<LlmsTxtSettingsMigrationPlan>().
        // Verifying the exact Remove<T> call requires a real
        // PackageMigrationPlanCollectionBuilder (which requires a real
        // IUmbracoBuilder in the test). This test covers the OBSERVABLE-via-NSubstitute
        // half: the composer accesses the collection-builder accessor.
        // The end-to-end behaviour is pinned by Story 3.1 manual gate Step 7
        // (uSync coexistence flag — the integration test boots successfully
        // with the flag set, proving the migration plan was successfully removed
        // before unattended-upgrade ran).
        var (composer, builder, _) = BuildComposer(skipDoctype: true);

        // NSubstitute's stubbed IUmbracoBuilder cannot construct a real
        // PackageMigrationPlanCollectionBuilder, so the composer's call into
        // `builder.PackageMigrationPlans().Remove<T>()` may NRE on the chained
        // call. Catch the SPECIFIC exception(s) that derive from null returns
        // — never a bare `catch`, which would mask any real bug introduced
        // into the composer body and leave only the call-count assertion to
        // catch regressions.
        Assert.That(
            () =>
            {
                try
                {
                    composer.Compose(builder);
                }
                catch (NullReferenceException)
                {
                    // Expected: WithCollectionBuilder<T>() returns null on the
                    // substitute, and the chained .Remove<T>() NREs.
                }
                catch (InvalidOperationException)
                {
                    // Some NSubstitute versions throw IOE on uncovered method
                    // shapes (extension methods, generics).
                }
            },
            Throws.Nothing,
            "Compose must not throw any unexpected exception type — only the documented null-chain NRE/IOE from the NSubstitute stub");

        var collectionBuilderCalls = builder.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "WithCollectionBuilder");
        Assert.That(collectionBuilderCalls, Is.GreaterThan(0),
            "with skipDoctype=true, the composer must access the PackageMigrationPlans collection builder to call Remove<T>()");
    }

    [Test]
    public void AdopterOverride_PreRegisteredBeforeComposer_DefaultDoesNotReplace()
    {
        // Story 3.1 AC8 — adopter override path 1: register BEFORE our composer.
        // Our TryAddScoped must notice the existing registration and bow out.
        // Mirrors BuildersComposerTests.AdopterOverride_PreRegisteredBeforeComposer_DefaultDoesNotReplace
        // verbatim (Story 2.1 pattern).
        var (composer, builder, services) = BuildComposer(skipDoctype: false);
        services.AddScoped<ISettingsResolver, NoOpAdopterResolver>();

        composer.Compose(builder);

        var registrations = services
            .Where(d => d.ServiceType == typeof(ISettingsResolver))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(registrations, Has.Length.EqualTo(1),
                "TryAdd* must not stack a duplicate registration");
            Assert.That(registrations[0].ImplementationType, Is.EqualTo(typeof(NoOpAdopterResolver)),
                "adopter's pre-registration wins; DefaultSettingsResolver is NOT registered");
        });
    }

    [Test]
    public void AdopterOverride_PostRegisteredAfterComposer_LastWinsResolvesAdopter()
    {
        // Story 3.1 AC8 — adopter override path 2: register AFTER our composer.
        // DI's last-registration-wins semantics mean GetService returns the adopter.
        var (composer, builder, services) = BuildComposer(skipDoctype: false);
        composer.Compose(builder);

        services.AddScoped<ISettingsResolver, NoOpAdopterResolver>();

        // Stub dep graph for DefaultSettingsResolver so the framework can
        // build it if it wants; the assertion is about which type comes out.
        StubResolverDependencies(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();

        Assert.That(resolved, Is.InstanceOf<NoOpAdopterResolver>(),
            "last-registration-wins: adopter's post-composer registration is what GetService returns");
    }

    [Test]
    public void Compose_StartupValidation_LlmsSettingsResolver_NoCaptiveDependency()
    {
        // Story 3.1 AC7 + project-context.md § Testing Rules — canonical DI
        // gate for any new DI-resolved type from Epic 3 onward. ValidateOnBuild
        // + ValidateScopes catches captive dependencies at service-provider
        // construction time (e.g. if a future refactor re-registers the
        // resolver as Singleton, this assertion fails before any request runs).
        // Mirrors Story 2.2's
        // Compose_StartupValidation_LlmsFullBuilder_NoCaptiveDependency
        // (BuildersComposerTests:174-210).
        var (composer, builder, services) = BuildComposer(skipDoctype: false);
        composer.Compose(builder);

        StubResolverDependencies(services);

        Assert.DoesNotThrow(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            // Resolver is Scoped — must resolve from a child scope, not root.
            using var scope = provider.CreateScope();
            var resolved = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
            Assert.That(resolved, Is.Not.Null);
        }, "validation must succeed — Scoped lifetime keeps the dep graph captive-free");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static (SettingsComposer Composer, IUmbracoBuilder Builder, IServiceCollection Services)
        BuildComposer(bool skipDoctype)
    {
        var services = new ServiceCollection();
        var configEntries = new Dictionary<string, string?>
        {
            ["LlmsTxt:Migrations:SkipSettingsDoctype"] = skipDoctype ? "true" : "false",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configEntries)
            .Build();
        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        builder.Config.Returns(configuration);
        return (new SettingsComposer(), builder, services);
    }

    private static void StubResolverDependencies(IServiceCollection services)
    {
        // DefaultSettingsResolver ctor: IOptionsMonitor<AiVisibilitySettings>,
        // IUmbracoContextAccessor, IDocumentNavigationQueryService, AppCaches,
        // ILogger<DefaultSettingsResolver>.
        services.AddSingleton(_ => Substitute.For<IOptionsMonitor<AiVisibilitySettings>>());
        services.AddSingleton(_ => Substitute.For<IUmbracoContextAccessor>());
        services.AddSingleton(_ => Substitute.For<IDocumentNavigationQueryService>());
        services.AddSingleton(_ => new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache())));
        services.AddLogging();
    }

    private sealed class NoOpAdopterResolver : ISettingsResolver
    {
        public Task<ResolvedLlmsSettings> ResolveAsync(string? hostname, string? culture, CancellationToken cancellationToken)
            => Task.FromResult(new ResolvedLlmsSettings(
                SiteName: "OVERRIDDEN",
                SiteSummary: null,
                ExcludedDoctypeAliases: new HashSet<string>(),
                BaseSettings: new AiVisibilitySettings()));
    }
}
