using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.1 — in-process Razor page renderer extracted verbatim from
/// the v1.0 <see cref="PageRenderer"/>. Implements
/// <see cref="IPageRendererStrategy"/> and is registered as the
/// <c>RenderStrategyMode.Razor</c> keyed service. The orchestrator at
/// <see cref="PageRenderer"/> dispatches here when
/// <c>AiVisibility:RenderStrategy:Mode</c> resolves to <c>Razor</c>.
///
/// <para>
/// Story 1.2 moved route resolution UP to <see cref="Controllers.MarkdownController"/>;
/// this strategy accepts an already-resolved <see cref="IPublishedContent"/> and
/// builds the <see cref="IPublishedRequest"/> directly via <see cref="PublishedRequestBuilder"/>'s
/// <c>SetPublishedContent</c> + <c>Build</c> path. No second routing call — the controller's
/// <see cref="IPublishedRouter.RouteRequestAsync"/> already produced the content.
/// </para>
///
/// <para>
/// Concurrent renders inside one HTTP request are NOT supported by Umbraco's
/// <c>IScopeProvider</c> (AsyncLocal-managed ambient scope races on worker threads).
/// Single render per request only; cross-request concurrency works naturally via the
/// ASP.NET Core request boundary.
/// </para>
///
/// <para>
/// Lifetime is <c>Transient</c> (Story 7.1 AC5). The strategy mutates
/// <c>IVariationContextAccessor.VariationContext</c> in a try/finally during render;
/// a Singleton instance would race that mutation across concurrent renders.
/// </para>
/// </summary>
internal sealed class RazorPageRendererStrategy : IPageRendererStrategy
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IUmbracoContextFactory _umbracoContextFactory;
    private readonly IFileService _fileService;
    private readonly ITemplateService _templateService;
    private readonly IRazorViewEngine _razorViewEngine;
    private readonly ITempDataProvider _tempDataProvider;
    private readonly IVariationContextAccessor _variationContextAccessor;
    private readonly ILogger<RazorPageRendererStrategy> _logger;

    public RazorPageRendererStrategy(
        IHttpContextAccessor httpContextAccessor,
        IUmbracoContextFactory umbracoContextFactory,
        IFileService fileService,
        ITemplateService templateService,
        IRazorViewEngine razorViewEngine,
        ITempDataProvider tempDataProvider,
        IVariationContextAccessor variationContextAccessor,
        ILogger<RazorPageRendererStrategy> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _umbracoContextFactory = umbracoContextFactory;
        _fileService = fileService;
        _templateService = templateService;
        _razorViewEngine = razorViewEngine;
        _tempDataProvider = tempDataProvider;
        _variationContextAccessor = variationContextAccessor;
        _logger = logger;
    }

    public async Task<PageRenderResult> RenderAsync(
        IPublishedContent content,
        Uri absoluteUri,
        string? culture,
        CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "PageRenderer requires an active HttpContext — invoked outside the request pipeline.");

        using var ctxRef = _umbracoContextFactory.EnsureUmbracoContext();
        var umbracoContext = ctxRef.UmbracoContext;
        var previousPublishedRequest = umbracoContext.PublishedRequest;
        var previousVariationContext = _variationContextAccessor.VariationContext;
        var variationContextMutated = false;

        var templateAlias = content.ContentType.Alias;

        try
        {
            // Build an IPublishedRequest directly from the resolved content — no second
            // RouteRequestAsync call. The builder's URI ctor is needed for any URL-derived
            // template helpers (Url.Action, etc.) that inspect Request.Url.
            var builder = new PublishedRequestBuilder(absoluteUri, _fileService);
            builder.SetPublishedContent(content);

            // Preserve the content's chosen template so doctypes whose default template
            // alias differs from the doctype alias still resolve to the correct view.
            // `PublishedRequestBuilder.Build()` doesn't perform implicit template lookup —
            // that's `IPublishedRouter.RouteRequestAsync`'s job, which the controller
            // already called once (Story 1.2 moved routing up). Re-resolving the template
            // by `TemplateId` here keeps the routed-template branch reachable.
            if (content.TemplateId is int templateId && templateId > 0)
            {
                var template = await _templateService.GetAsync(templateId);
                if (template is not null)
                {
                    builder.SetTemplate(template);
                }
            }

            if (!string.IsNullOrWhiteSpace(culture))
            {
                builder.SetCulture(culture);
                _variationContextAccessor.VariationContext = new VariationContext(culture);
                variationContextMutated = true;
            }

            var publishedRequest = builder.Build();

            cancellationToken.ThrowIfCancellationRequested();

            // The template alias picked by the route may differ from the doctype alias
            // (e.g. when a doctype has multiple templates). Prefer the routed template
            // alias if present; otherwise the doctype alias is the conventional fallback
            // (ASP.NET MVC's view-resolution chain still finds `~/Views/{doctypeAlias}.cshtml`).
            templateAlias = publishedRequest.Template?.Alias ?? content.ContentType.Alias;

            // Mount the published request on the active context so layouts and partials
            // resolve content from `IUmbracoContext.PublishedRequest` rather than `Model`.
            // Save/restore the previous value to avoid leaking into the outer HTTP request
            // when `EnsureUmbracoContext()` reuses the existing context — fixes the leak
            // documented in deferred-work.md from Spike 0.A code review.
            umbracoContext.PublishedRequest = publishedRequest;

            var html = await RenderRazorAsync(
                httpContext,
                publishedRequest,
                content,
                templateAlias,
                cancellationToken);

            return PageRenderResult.Ok(html, content, templateAlias, culture);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "PageRenderer: rendering {TemplateAlias} for {ContentKey} {Path} failed",
                templateAlias,
                content.Key,
                absoluteUri.AbsolutePath);
            return PageRenderResult.Failed(ex, content, templateAlias, culture);
        }
        finally
        {
            // Always restore — on success AND failure — so the outer request keeps its
            // original PublishedRequest and VariationContext.
            umbracoContext.PublishedRequest = previousPublishedRequest;
            if (variationContextMutated)
            {
                _variationContextAccessor.VariationContext = previousVariationContext;
            }
        }
    }

    private async Task<string> RenderRazorAsync(
        HttpContext httpContext,
        IPublishedRequest publishedRequest,
        IPublishedContent publishedContent,
        string templateAlias,
        CancellationToken cancellationToken)
    {
        _ = publishedRequest; // mounted on UmbracoContext above; unused here directly
        var routeData = new RouteData();
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        // Per Spike 0.A deferred-work: try `FindView` first so adopter `_ViewLocations`
        // expanders are honoured on the primary path; fall back to `GetView` with the
        // explicit `~/Views/{alias}.cshtml` path for the common Umbraco layout.
        var viewResult = _razorViewEngine.FindView(actionContext, templateAlias, isMainPage: true);
        if (!viewResult.Success)
        {
            viewResult = _razorViewEngine.GetView(
                executingFilePath: null,
                viewPath: $"~/Views/{templateAlias}.cshtml",
                isMainPage: true);
        }

        if (!viewResult.Success || viewResult.View is null)
        {
            var searchedLocations = string.Join(", ", viewResult.SearchedLocations ?? Array.Empty<string>());
            throw new InvalidOperationException(
                $"PageRenderer: could not resolve Razor view for template '{templateAlias}'. Searched: {searchedLocations}");
        }

        var view = viewResult.View;

        // Non-generic ViewDataDictionary so Umbraco's ContentModelBinder handles the
        // conversion from IPublishedContent to whatever typed PublishedModel
        // (Home / ArticleList / Content / etc.) the ModelsBuilder-generated template
        // expects. Passing ViewDataDictionary<IPublishedContent> fails at runtime
        // against typed views — see Spike 0.A locked decision #7.
        var viewData = new ViewDataDictionary(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = publishedContent,
        };

        var tempData = new TempDataDictionary(httpContext, _tempDataProvider);

        await using var sw = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            view,
            viewData,
            tempData,
            sw,
            new HtmlHelperOptions());

        await view.RenderAsync(viewContext);
        cancellationToken.ThrowIfCancellationRequested();
        return sw.ToString();
    }
}
