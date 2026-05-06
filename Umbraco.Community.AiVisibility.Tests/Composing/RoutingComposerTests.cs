using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Umbraco.Community.AiVisibility.Composing;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;
using Umbraco.Community.AiVisibility.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace Umbraco.Community.AiVisibility.Tests.Composing;

/// <summary>
/// AC1 + AC2 DI-layer regression tests pinning the public extension-point contract:
/// <list type="bullet">
/// <item>AC1 — <see cref="IMarkdownContentExtractor"/> default is registered via
/// <c>services.TryAddTransient</c>; adopter overrides do not need to remove the default first</item>
/// <item>AC2 — adopter <see cref="IContentRegionSelector"/> slots into the default
/// extractor's pipeline while the rest of the pipeline (parse / strip / absolutify /
/// convert / frontmatter) keeps running</item>
/// </list>
/// </summary>
[TestFixture]
public class RoutingComposerTests
{
    /// <summary>
    /// AC1 — sanity: <see cref="RoutingComposer.Compose"/> does register
    /// <see cref="IMarkdownContentExtractor"/> with the default impl when the service
    /// collection is empty. Pins the registration shape (transient, ImplementationType)
    /// so a future refactor away from <c>TryAddTransient</c> would surface here.
    /// </summary>
    [Test]
    public void Compose_FreshServiceCollection_RegistersDefaultExtractorAsTransient()
    {
        var (composer, builder, services) = BuildComposer();

        composer.Compose(builder);

        var registration = services.Single(d => d.ServiceType == typeof(IMarkdownContentExtractor));
        Assert.Multiple(() =>
        {
            Assert.That(registration.Lifetime, Is.EqualTo(ServiceLifetime.Transient),
                "default extractor lifetime must be Transient (AngleSharp DOM state)");
            Assert.That(registration.ImplementationType, Is.EqualTo(typeof(DefaultMarkdownContentExtractor)),
                "default extractor must be DefaultMarkdownContentExtractor");
        });
    }

    /// <summary>
    /// AC1 — sanity: same shape as above for <see cref="IContentRegionSelector"/>.
    /// </summary>
    [Test]
    public void Compose_FreshServiceCollection_RegistersDefaultRegionSelectorAsTransient()
    {
        var (composer, builder, services) = BuildComposer();

        composer.Compose(builder);

        var registration = services.Single(d => d.ServiceType == typeof(IContentRegionSelector));
        Assert.Multiple(() =>
        {
            Assert.That(registration.Lifetime, Is.EqualTo(ServiceLifetime.Transient),
                "default region selector lifetime must be Transient");
            Assert.That(registration.ImplementationType, Is.EqualTo(typeof(DefaultContentRegionSelector)),
                "default region selector must be DefaultContentRegionSelector");
        });
    }

    /// <summary>
    /// AC1 — adopter wins when their composer runs BEFORE ours.
    /// <para>
    /// Adopter registers <see cref="IMarkdownContentExtractor"/> first; our
    /// <c>TryAddTransient</c> is a no-op against the existing registration. The
    /// adopter's <see cref="ServiceDescriptor"/> stays put — they did NOT have to
    /// remove our default first because we never registered. AR17 contract.
    /// </para>
    /// </summary>
    [Test]
    public void Compose_AdopterRegistersFirst_TryAddTransientNoOps_AdopterWins()
    {
        var (composer, builder, services) = BuildComposer();
        services.AddTransient<IMarkdownContentExtractor, FakeAdopterExtractor>();

        composer.Compose(builder);

        var registrations = services.Where(d => d.ServiceType == typeof(IMarkdownContentExtractor)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(registrations, Has.Length.EqualTo(1),
                "exactly one IMarkdownContentExtractor registration — TryAddTransient must NOT append a duplicate");
            Assert.That(registrations[0].ImplementationType, Is.EqualTo(typeof(FakeAdopterExtractor)),
                "adopter registration must remain in place");
        });
    }

    /// <summary>
    /// AC1 — adopter wins when their composer runs AFTER ours.
    /// <para>
    /// <see cref="RoutingComposer"/> registers the default first. The adopter then
    /// calls <c>services.AddTransient&lt;IMarkdownContentExtractor, AdopterExtractor&gt;()</c>
    /// (note: <c>AddTransient</c> not <c>TryAdd</c> — adopter explicitly chooses to
    /// override). DI's last-one-wins rule means the adopter's instance is what gets
    /// resolved. The adopter still does NOT have to <c>services.Remove(...)</c> the default.
    /// </para>
    /// </summary>
    [Test]
    public void Compose_AdopterRegistersAfter_AdopterWinsByLastRegistration()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        // Adopter chooses to override AFTER our composer ran. Use AddTransient
        // (not TryAdd) — adopter wants to win. Append-and-resolve-last is DI's contract.
        services.AddTransient<IMarkdownContentExtractor, FakeAdopterExtractor>();

        // Pin the descriptor-list shape directly. This is a stronger contract than
        // resolving and checking the instance type: it catches a future regression
        // where the package switches to `Replace` semantics (which would remove our
        // default and leave only the adopter — resolution would still return the
        // adopter, hiding the contract change). The shape we want is "two descriptors,
        // adopter is the last one" so DI's last-wins rule is what produces the override.
        var registrations = services
            .Where(d => d.ServiceType == typeof(IMarkdownContentExtractor))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(registrations, Has.Length.EqualTo(2),
                "package default + adopter — both ServiceDescriptors must coexist; " +
                "TryAddTransient must NOT have been replaced with Replace/RemoveAll semantics");
            Assert.That(registrations[0].ImplementationType, Is.EqualTo(typeof(DefaultMarkdownContentExtractor)),
                "package default registered first by RoutingComposer.Compose");
            Assert.That(registrations[^1].ImplementationType, Is.EqualTo(typeof(FakeAdopterExtractor)),
                "adopter is the LAST descriptor — DI resolves last-registration-wins");
        });

        // Provide a stub ILogger so the test doesn't need NullLoggerFactory wiring on
        // every dependency; AngleSharp / extractor wiring is not exercised here, just
        // service-resolution semantics.
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        // Note: ValidateOnBuild=true is intentionally NOT used here. RoutingComposer
        // also registers PageRenderer / MarkdownConverter / IContentRegionSelector
        // whose constructors depend on Umbraco core services (IPublishedUrlProvider,
        // IPageRenderer's deps, etc.) — those services are wired by other composers
        // outside the scope of this DI-shape test. The descriptor-ordering assertion
        // above pins the contract that ValidateOnBuild would otherwise have caught.
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
        var resolved = sp.GetRequiredService<IMarkdownContentExtractor>();

        Assert.That(resolved, Is.InstanceOf<FakeAdopterExtractor>(),
            "DI resolves the LAST registered ServiceDescriptor for a type — adopter wins");
    }

    /// <summary>
    /// AC1 (lifetime case) — adopter chooses Singleton even though the default is
    /// Transient. The DI container respects the adopter's lifetime declaration; same
    /// instance is returned across distinct scopes. Documents the implication noted
    /// in <see cref="IMarkdownContentExtractor"/> xmldoc — singleton extractors must
    /// be thread-safe.
    /// </summary>
    [Test]
    public void Compose_AdopterSingletonOverride_DiContainerRespectsAdopterLifetime()
    {
        var (composer, builder, services) = BuildComposer();
        composer.Compose(builder);

        // Adopter overrides AFTER our composer with Singleton lifetime. Default was Transient.
        services.AddSingleton<IMarkdownContentExtractor, FakeAdopterExtractor>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        // Pin the descriptor-list shape — same rationale as
        // Compose_AdopterRegistersAfter_AdopterWinsByLastRegistration.
        var registrations = services
            .Where(d => d.ServiceType == typeof(IMarkdownContentExtractor))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(registrations, Has.Length.EqualTo(2),
                "package default + adopter — both ServiceDescriptors must coexist");
            Assert.That(registrations[^1].Lifetime, Is.EqualTo(ServiceLifetime.Singleton),
                "adopter's Singleton lifetime declaration is preserved on the descriptor");
        });

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        IMarkdownContentExtractor first;
        IMarkdownContentExtractor second;
        using (var scopeA = sp.CreateScope())
        {
            first = scopeA.ServiceProvider.GetRequiredService<IMarkdownContentExtractor>();
        }
        using (var scopeB = sp.CreateScope())
        {
            second = scopeB.ServiceProvider.GetRequiredService<IMarkdownContentExtractor>();
        }

        Assert.That(first, Is.SameAs(second),
            "Singleton-registered adopter extractor must yield the same instance across scopes");
    }

    /// <summary>
    /// AC2 — the default extractor's pipeline calls the adopter's
    /// <see cref="IContentRegionSelector"/> and continues to run AngleSharp parse,
    /// strip-inside-region, URL absolutification, image-empty-alt drop, ReverseMarkdown
    /// convert, and frontmatter prepend.
    /// </summary>
    [Test]
    public async Task DefaultExtractor_AdopterRegionSelector_RestOfPipelineRuns()
    {
        var html = """
            <!DOCTYPE html>
            <html><body>
            <main>
              <h1>Default region — should not be picked</h1>
              <p>This region MUST NOT be selected by the adopter selector.</p>
            </main>
            <section data-acme-content>
              <h1>Adopter region heading</h1>
              <p>Article body — <a href="/about">About link</a>.</p>
              <img src="/media/hero.png" alt="hero" />
              <img src="/media/decorative.png" alt="" />
              <script>console.log('strip me')</script>
            </section>
            </body></html>
            """;

        var fakeSelector = new FakeAdopterRegionSelector();
        var extractor = BuildDefaultExtractor(fakeSelector);

        var meta = new ContentMetadata(
            Title: "Adopter Region Pin",
            AbsoluteUrl: "https://example.test/page",
            UpdatedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            ContentKey: Guid.NewGuid(),
            Culture: "en-GB");

        var result = await extractor.ExtractFromHtmlAsync(
            html,
            new Uri("https://example.test/page"),
            meta,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fakeSelector.InvocationCount, Is.EqualTo(1),
                "adopter region selector must be called exactly once per extraction");
            Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found));

            var md = result.Markdown!;
            // Adopter region content survives.
            Assert.That(md, Does.Contain("Adopter region heading"),
                "default-extractor pipeline must use the adopter's region");
            Assert.That(md, Does.Not.Contain("Default region"),
                "non-selected region must not bleed through");
            // Rest of pipeline ran:
            Assert.That(md, Does.Contain("[About link](https://example.test/about)"),
                "URL absolutification still runs after adopter region selection");
            Assert.That(md, Does.Contain("![hero](https://example.test/media/hero.png)"),
                "alt-text-bearing image survives, with absolutified src");
            Assert.That(md, Does.Not.Contain("decorative.png"),
                "empty-alt image dropped by the default extractor pipeline");
            Assert.That(md, Does.Not.Contain("strip me"),
                "<script> stripped inside the adopter-selected region");
            Assert.That(md, Does.StartWith("---\ntitle: Adopter Region Pin"),
                "YAML frontmatter still prepended after adopter region selection");
        });
    }

    /// <summary>
    /// AC2 (fallback path) — adopter selector returns null; default extractor falls
    /// through to SmartReader. Pin the contract documented in
    /// <see cref="IContentRegionSelector"/> xmldoc.
    /// </summary>
    [Test]
    public async Task DefaultExtractor_AdopterSelectorReturnsNull_FallsThroughToSmartReader()
    {
        // SmartReader (the readability fallback) needs realistic body text density to
        // score positively; build an article-shaped fragment with enough content to pass.
        // Sentinel rationale: the prose contains a unique nonsense token. The assertion
        // pins ONLY that token. This is stronger than asserting on a word that happens
        // to also appear in the prose — earlier this test asserted `Does.Contain("SmartReader")`
        // which would have passed for ANY extraction path that emitted prose, since the
        // word "SmartReader" was in the source text. The sentinel is invisible to any
        // path that doesn't actually ingest article body content.
        var html = """
            <!DOCTYPE html>
            <html><body>
            <article>
              <h1>Article that the readability fallback must extract</h1>
              <p>Paragraph one with enough body text to register as a content region under
              the readability heuristic's text-density scoring. This needs to look like
              prose, not navigation chrome, or the score falls below the threshold.</p>
              <p>Paragraph two extends the prose so the readability score stays well above
              the "not readable" cut-off. UNIQUE-FALLBACK-SENTINEL-Φ7 — only the readability
              fallback path can surface this token because the adopter selector returns null
              and there is no other configured extraction route in this test.</p>
              <p>Paragraph three keeps adding content for good measure.</p>
            </article>
            </body></html>
            """;

        var nullSelector = new FakeAdopterRegionSelector(returnNull: true);
        var extractor = BuildDefaultExtractor(nullSelector);

        var meta = new ContentMetadata(
            Title: "Selector Returns Null",
            AbsoluteUrl: "https://example.test/article",
            UpdatedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            ContentKey: Guid.NewGuid(),
            Culture: "en-GB");

        var result = await extractor.ExtractFromHtmlAsync(
            html,
            new Uri("https://example.test/article"),
            meta,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(nullSelector.InvocationCount, Is.EqualTo(1),
                "adopter region selector still consulted before fallback");
            Assert.That(result.Status, Is.EqualTo(MarkdownExtractionStatus.Found),
                "readability fallback must produce a Found result");
            Assert.That(result.Markdown, Is.Not.Null);
            Assert.That(result.Markdown, Does.Contain("UNIQUE-FALLBACK-SENTINEL-Φ7"),
                "fallback must emit prose-only content the adopter selector explicitly skipped");
        });
    }

    /// <summary>
    /// Story 4.1 DoD bullet 1 — Architect note A4 (epics.md:1164). Pins that
    /// <see cref="DiscoverabilityHeaderMiddleware"/> + <see cref="DefaultExclusionEvaluator"/>
    /// + their transitive package-side deps resolve under
    /// <c>ValidateScopes = ValidateOnBuild = true</c> without forming a captive
    /// dependency.
    /// <para>
    /// <b>Stub-driven gate (NOT composer-driven).</b> The architect note's
    /// preferred shape was composer-driven, but <see cref="RoutingComposer"/>
    /// also registers the full extraction pipeline (<c>PageRenderer</c>,
    /// <c>DefaultMarkdownContentExtractor</c>, <c>MarkdownRouteResolver</c>) whose
    /// constructors depend on Umbraco core services
    /// (<c>IPublishedRouter</c>, <c>IUmbracoContextFactory</c>) that this DI-shape
    /// test does not wire. Same trade-off Story 3.2's
    /// <c>Compose_StartupValidation_LlmsSettingsApiController_NoCaptiveDependency</c>
    /// adopted (Spec Drift Note #6 — deliberate simplification documented).
    /// What this test pins is exactly the new Story 4.1 surface: the middleware
    /// + evaluator constructors do not capture a Scoped dep into a Singleton.
    /// </para>
    /// </summary>
    [Test]
    public void Compose_StartupValidation_DiscoverabilityHeaderMiddleware_NoCaptiveDependency()
    {
        var services = new ServiceCollection();

        // Mirror RoutingComposer's lifetime declarations for the new Story 4.1
        // surface only — captive deps would surface at BuildServiceProvider time.
        services.AddTransient<DiscoverabilityHeaderMiddleware>();
        services.TryAddScoped<IExclusionEvaluator, DefaultExclusionEvaluator>();

        // Stub the seams the new types depend on. Lifetimes match the
        // production composer (resolver Scoped, options monitor Singleton,
        // URL provider Singleton).
        services.AddSingleton<IOptionsMonitor<AiVisibilitySettings>>(_ =>
        {
            var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
            monitor.CurrentValue.Returns(new AiVisibilitySettings());
            return monitor;
        });
        services.AddScoped<ISettingsResolver>(_ => Substitute.For<ISettingsResolver>());
        services.AddSingleton<global::Umbraco.Cms.Core.Routing.IPublishedUrlProvider>(
            Substitute.For<global::Umbraco.Cms.Core.Routing.IPublishedUrlProvider>());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        // ValidateScopes catches captive-scope-via-singleton at the root
        // provider; ValidateOnBuild eagerly constructs every registered service
        // so misconfigurations surface immediately.
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        using var scope = sp.CreateScope();
        var middleware = scope.ServiceProvider.GetRequiredService<DiscoverabilityHeaderMiddleware>();
        var evaluator = scope.ServiceProvider.GetRequiredService<IExclusionEvaluator>();

        Assert.Multiple(() =>
        {
            Assert.That(middleware, Is.Not.Null,
                "DiscoverabilityHeaderMiddleware must resolve cleanly under ValidateOnBuild");
            Assert.That(evaluator, Is.Not.Null,
                "IExclusionEvaluator must resolve cleanly under ValidateOnBuild");
        });
    }

    private static (RoutingComposer Composer, IUmbracoBuilder Builder, IServiceCollection Services)
        BuildComposer()
    {
        var services = new ServiceCollection();
        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);

        // RoutingComposer binds AiVisibilitySettings to the AiVisibility: section. An empty
        // configuration is fine — the bind succeeds with default values.
        var config = new ConfigurationBuilder().Build();
        builder.Config.Returns(config);

        return (new RoutingComposer(), builder, services);
    }

    private static DefaultMarkdownContentExtractor BuildDefaultExtractor(IContentRegionSelector regionSelector)
    {
        var settings = new AiVisibilitySettings { MainContentSelectors = Array.Empty<string>() };
        var optionsSnapshot = new StubOptionsSnapshot<AiVisibilitySettings>(settings);

        // Pipeline tests exercise the internal ExtractFromHtmlAsync seam — the
        // PageRenderer / IPublishedUrlProvider / IHttpContextAccessor params are not
        // touched, so null! is intentional (matches DefaultMarkdownContentExtractorTests).
        return new DefaultMarkdownContentExtractor(
            pageRenderer: null!,
            regionSelector: regionSelector,
            converter: new MarkdownConverter(),
            publishedUrlProvider: null!,
            httpContextAccessor: null!,
            settings: optionsSnapshot,
            logger: NullLogger<DefaultMarkdownContentExtractor>.Instance);
    }

    /// <summary>
    /// Test fake — records invocations so tests can assert the selector was called.
    /// Returns the <c>data-acme-content</c> element to mimic an adopter who has tagged
    /// their main-content boundary with a custom attribute.
    /// </summary>
    private sealed class FakeAdopterRegionSelector : IContentRegionSelector
    {
        private readonly bool _returnNull;

        public FakeAdopterRegionSelector(bool returnNull = false)
        {
            _returnNull = returnNull;
        }

        public int InvocationCount { get; private set; }

        public IElement? SelectRegion(IDocument document, IReadOnlyList<string> configuredSelectors)
        {
            InvocationCount++;
            if (_returnNull)
            {
                return null;
            }
            return document.QuerySelector("[data-acme-content]");
        }
    }

    /// <summary>
    /// Test fake — never invoked; only used to drive ServiceDescriptor / DI shape assertions.
    /// </summary>
    private sealed class FakeAdopterExtractor : IMarkdownContentExtractor
    {
        public Task<MarkdownExtractionResult> ExtractAsync(
            IPublishedContent content, string? culture, CancellationToken ct)
            => throw new NotImplementedException("test fake — never invoked");
    }

    private sealed class StubOptionsSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        public StubOptionsSnapshot(T value) { Value = value; }
        public T Value { get; }
        public T Get(string? name) => Value;
    }
}
