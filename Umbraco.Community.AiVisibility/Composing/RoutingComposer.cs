using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Extraction;
using Umbraco.Community.AiVisibility.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Net.Http;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Cms.Web.Common.Routing;

namespace Umbraco.Community.AiVisibility.Composing;

/// <summary>
/// Registers the <c>{**path:nonfile}.md</c> route, the
/// <see cref="AiVisibilityPipelineFilter"/>, the
/// <see cref="UmbracoRequestOptions.HandleAsServerSideRequest"/> composition,
/// and the extraction-pipeline DI graph (<see cref="PageRenderer"/>,
/// <see cref="MarkdownConverter"/>, <see cref="IContentRegionSelector"/>,
/// <see cref="IMarkdownContentExtractor"/>).
/// </summary>
public sealed class RoutingComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Strongly-typed appsettings binding.
        builder.Services
            .AddOptions<AiVisibilitySettings>()
            .Bind(builder.Config.GetSection(AiVisibilitySettings.SectionName));

        // Pipeline filter — registers the .md endpoint via UmbracoPipelineFilter.Endpoints.
        builder.Services.Configure<UmbracoPipelineOptions>(opts =>
            opts.AddFilter(new AiVisibilityPipelineFilter()));

        // Compose UmbracoRequestOptions.HandleAsServerSideRequest with any pre-existing
        // delegate (per AR2 — never overwrite). PostConfigure runs after Configure so
        // we observe whatever Umbraco core / adopters have already set.
        builder.Services.PostConfigure<UmbracoRequestOptions>(opts =>
        {
            var previous = opts.HandleAsServerSideRequest;
            opts.HandleAsServerSideRequest =
                AiVisibilityPipelineFilter.ComposeHandleAsServerSideRequest(previous);
        });

        // HttpContextAccessor needed by PageRenderer to derive scheme/host.
        builder.Services.AddHttpContextAccessor();

        // Route resolution seam — controller calls this; default impl wraps
        // IPublishedRouter + IUmbracoContextFactory + IFileService.
        builder.Services.TryAddTransient<IMarkdownRouteResolver, MarkdownRouteResolver>();

        // Story 1.3 — shared response writer. Single source of truth for the
        // ETag / Cache-Control / Vary / 304 shape across the .md route AND the
        // Accept-header negotiation middleware. Stateless ⇒ singleton.
        builder.Services.TryAddSingleton<IMarkdownResponseWriter, MarkdownResponseWriter>();

        // Story 4.1 — shared exclusion evaluator. Lifts the per-page-bool-
        // then-resolver-throw-fail-open shape out of MarkdownController +
        // AcceptHeaderNegotiationMiddleware so the new DiscoverabilityHeaderMiddleware
        // and TagHelpers consume the same rule set. Scoped — depends on the
        // Scoped ISettingsResolver.
        builder.Services.TryAddScoped<IExclusionEvaluator, DefaultExclusionEvaluator>();

        // Story 1.3 — factory-activated middleware (IMiddleware). Transient because
        // it depends on the transient IMarkdownContentExtractor; one instance per
        // request keeps the cache decorator's per-request semantics intact.
        builder.Services.AddTransient<AcceptHeaderNegotiationMiddleware>();

        // Story 4.1 — factory-activated discoverability middleware. Transient
        // for the same reason: depends on Scoped IExclusionEvaluator via
        // request scope. Wired into AiVisibilityPipelineFilter.MapPostRouting in front
        // of the Accept-header negotiation middleware.
        builder.Services.AddTransient<DiscoverabilityHeaderMiddleware>();

        // Extraction pipeline — TryAdd* per AR17 so adopters can override before our
        // composer runs by registering their own implementation first.
        builder.Services.TryAddTransient<PageRenderer>();

        // Story 7.1 — strategy interface + Razor extraction. PageRenderer dispatches
        // to IPageRendererStrategy keyed by AiVisibility:RenderStrategy:Mode.
        // TryAddKeyedTransient so adopters can replace the Razor strategy without
        // removing-then-re-adding (matches AR17 TryAdd* discipline). Razor strategy
        // mutates IVariationContextAccessor.VariationContext per render — Singleton
        // would race; Transient is mandatory.
        builder.Services.TryAddKeyedTransient<IPageRendererStrategy, RazorPageRendererStrategy>(RenderStrategyMode.Razor);

        // Story 7.2 — Loopback strategy + HTTP infrastructure.
        builder.Services.TryAddKeyedTransient<IPageRendererStrategy, LoopbackPageRendererStrategy>(RenderStrategyMode.Loopback);

        // Story 7.3 — Auto strategy with Razor → Loopback ModelBindingException
        // fallback. Composes the two sibling strategies via captured
        // IServiceProvider + keyed-DI resolution (the same primitive
        // PageRenderer's orchestrator uses) so future strategies can be added
        // without rewriting Auto's body.
        builder.Services.TryAddKeyedTransient<IPageRendererStrategy, AutoPageRendererStrategy>(RenderStrategyMode.Auto);

        // Story 7.3 — per-(ContentTypeAlias, TemplateAlias) hijack-decision
        // cache. Singleton: process-lifetime, ConcurrentDictionary-backed, no
        // invalidation hook (hijack registration changes are code changes; a
        // deploy is the natural invalidation event).
        builder.Services.TryAddSingleton<IRendererStrategyCache, RendererStrategyCache>();

        // Story 7.4 — inbound-side recursion guard. Singleton: stateless;
        // reads only the HttpContext argument received per call (no injected
        // dependencies — captive-graph trivially clean). Defence-in-depth
        // against adopter middleware that aliases routes such that the
        // Loopback strategy's outbound HTTP call lands on this controller's
        // .md route. The primary mechanism is the outbound Accept: text/html
        // override on the Loopback strategy; this guard catches the bypass case.
        builder.Services.TryAddSingleton<IRecursionGuard, RecursionGuard>();

        // Story 7.2 — loopback URL resolver. Singleton: caches the first-call
        // resolution for the process lifetime; IServer.Features is stable
        // post-startup so re-walking on every render adds cost without value.
        // Lazy by contract — never throws at construction; environment-without-
        // a-binding adopters pinned to Mode=Razor boot clean.
        builder.Services.TryAddSingleton<ILoopbackUrlResolver, LoopbackUrlResolver>();

        // Story 7.2 — named HttpClient with primary handler whose
        // ServerCertificateCustomValidationCallback bypasses cert validation
        // IFF the transport-target host resolves to a loopback IP — never
        // blanket bypass. AllowAutoRedirect=false so 3xx responses surface as
        // render failures with Location-header diagnostic. 30s timeout absorbs
        // Spike 0.A's documented ~6s cold-start JIT cost + headroom. Adopters
        // layering Polly / instrumentation extend via .AddHttpMessageHandler(...)
        // on this same client name.
        builder.Services.AddHttpClient(Constants.Http.LoopbackHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = LoopbackCertificateValidator.Validate,
            })
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        builder.Services.TryAddSingleton<MarkdownConverter>();
        builder.Services.TryAddTransient<IContentRegionSelector, DefaultContentRegionSelector>();
        builder.Services.TryAddTransient<IMarkdownContentExtractor, DefaultMarkdownContentExtractor>();
    }
}
