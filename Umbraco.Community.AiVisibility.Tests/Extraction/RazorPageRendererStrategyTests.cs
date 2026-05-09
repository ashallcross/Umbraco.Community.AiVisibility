using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Community.AiVisibility.Extraction;

namespace Umbraco.Community.AiVisibility.Tests.Extraction;

/// <summary>
/// Story 7.1 AC2 + AC8 — unit coverage of the
/// <see cref="RazorPageRendererStrategy"/>'s testable surface. The
/// implementation moves the existing v1.0 in-process Razor body verbatim
/// from <c>PageRenderer</c>; its coupling to concrete sealed Umbraco /
/// ASP.NET MVC types (<see cref="Umbraco.Cms.Core.Routing.PublishedRequestBuilder"/>,
/// <see cref="Umbraco.Cms.Core.Web.UmbracoContextReference"/>,
/// <see cref="Microsoft.AspNetCore.Mvc.ViewEngines.ViewEngineResult"/>)
/// makes mock-only happy-path coverage impractical.
///
/// <para>
/// Per project-context.md § Testing Rules — Adam's "not nasa" rule
/// (Epic 3 retro A2): test density targets are CEILINGS, not floors;
/// under-shipping with a per-test rationale is preferred to padding.
/// Story 7.6 integration tests (synthetic hijack fixture + clean-seed
/// parity + multi-site host preservation) close the renderer-level
/// coverage that the present unit fixture cannot reach. Story 6.1's
/// Manual Gate Step 5 dry-run already proved the v1.0 Razor body
/// renders correctly against Clean.Core 7.0.5 — the verbatim move at
/// AC2 carries that proof forward.
/// </para>
///
/// <para>
/// What this fixture pins:
/// <list type="bullet">
/// <item>The null-<c>HttpContext</c> guard's documented error message
/// shape — the only adopter-visible surface a unit test can reach
/// without standing up a real Umbraco harness.</item>
/// </list>
/// </para>
///
/// <para>
/// What this fixture intentionally does NOT pin (with rationale):
/// <list type="bullet">
/// <item>Happy-path Razor render — would require synthesising a
/// <see cref="Umbraco.Cms.Core.Routing.IPublishedRequest"/> via
/// <see cref="Umbraco.Cms.Core.Routing.PublishedRequestBuilder"/> (concrete,
/// sealed; constructor depends on <see cref="IFileService"/> which itself
/// transitively reaches into Umbraco's scope provider). Closed by Story 7.6
/// integration tests (clean-seed parity).</item>
/// <item>FindView + GetView failure diagnostic — same dep-graph issue;
/// closed by Story 7.6 integration coverage of the
/// <c>InvalidOperationException</c> path.</item>
/// <item>Culture-restoration on success / failure — requires the happy-path
/// dep graph above. Spike 0.A LD#9 + the try/finally restore are
/// architecture-locked at <c>architecture.md</c> § Epic 7 Amendment;
/// behaviour preserved by the verbatim AC2 move.</item>
/// <item>Cancellation propagation — same; cancellation behaviour flows
/// through the extractor → cache → controller stack and is exercised
/// end-to-end in the existing 781-test suite's controller / cache layers.</item>
/// </list>
/// </para>
///
/// <para>
/// The orchestrator-layer tests at <see cref="PageRendererTests"/> pin the
/// dispatcher's delegation contract. The composer-layer tests at
/// <c>RoutingComposerTests</c> pin the keyed-DI registration shape and the
/// captive-dependency invariant. Together those three fixtures are the
/// Story 7.1 unit-coverage envelope.
/// </para>
/// </summary>
[TestFixture]
public class RazorPageRendererStrategyTests
{
    /// <summary>
    /// AC2 log-prefix preservation rule + Failure & Edge Case 7
    /// (exception-shape preservation). Pin the documented
    /// "<c>PageRenderer requires an active HttpContext...</c>" message —
    /// the only externally-visible behaviour of the strategy that a unit
    /// test can reach without instantiating a real Umbraco request
    /// pipeline.
    /// </summary>
    [Test]
    public void RenderAsync_NoActiveHttpContext_ThrowsInvalidOperationException()
    {
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var strategy = new RazorPageRendererStrategy(
            httpContextAccessor: httpContextAccessor,
            umbracoContextFactory: Substitute.For<IUmbracoContextFactory>(),
            fileService: Substitute.For<IFileService>(),
            templateService: Substitute.For<ITemplateService>(),
            razorViewEngine: Substitute.For<IRazorViewEngine>(),
            tempDataProvider: Substitute.For<ITempDataProvider>(),
            variationContextAccessor: Substitute.For<IVariationContextAccessor>(),
            logger: NullLogger<RazorPageRendererStrategy>.Instance);

        var content = Substitute.For<IPublishedContent>();
        var uri = new Uri("https://example.test/foo");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await strategy.RenderAsync(content, uri, culture: null, CancellationToken.None));

        // Pin the literal message including the "PageRenderer:" prefix per
        // AC2 — the prefix is the observable contract for adopters tailing
        // structured logs (matches the post-rename log-template convention
        // in architecture.md § Cross-Cutting Conventions).
        Assert.That(ex!.Message, Is.EqualTo(
            "PageRenderer requires an active HttpContext — invoked outside the request pipeline."));
    }
}
