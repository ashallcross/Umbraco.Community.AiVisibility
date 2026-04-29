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

namespace LlmsTxt.Umbraco.Extraction;

/// <summary>
/// In-process Razor page renderer — locked by Spike 0.A.
///
/// <para>
/// Concurrent renders inside one HTTP request are NOT supported by Umbraco's
/// <c>IScopeProvider</c> (AsyncLocal-managed ambient scope races on worker threads).
/// Single render per request only; cross-request concurrency works naturally via the
/// ASP.NET Core request boundary.
/// </para>
/// </summary>
internal sealed class PageRenderer
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IPublishedRouter _publishedRouter;
    private readonly IUmbracoContextFactory _umbracoContextFactory;
    private readonly IFileService _fileService;
    private readonly IRazorViewEngine _razorViewEngine;
    private readonly ITempDataProvider _tempDataProvider;
    private readonly IVariationContextAccessor _variationContextAccessor;
    private readonly ILogger<PageRenderer> _logger;

    public PageRenderer(
        IHttpContextAccessor httpContextAccessor,
        IPublishedRouter publishedRouter,
        IUmbracoContextFactory umbracoContextFactory,
        IFileService fileService,
        IRazorViewEngine razorViewEngine,
        ITempDataProvider tempDataProvider,
        IVariationContextAccessor variationContextAccessor,
        ILogger<PageRenderer> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _publishedRouter = publishedRouter;
        _umbracoContextFactory = umbracoContextFactory;
        _fileService = fileService;
        _razorViewEngine = razorViewEngine;
        _tempDataProvider = tempDataProvider;
        _variationContextAccessor = variationContextAccessor;
        _logger = logger;
    }

    public async Task<PageRenderResult> RenderAsync(
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

        IPublishedRequest? routedRequest = null;
        IPublishedContent? publishedContent = null;
        string? templateAlias = null;
        string? resolvedCulture = null;

        try
        {
            var builder = new PublishedRequestBuilder(absoluteUri, _fileService);
            if (!string.IsNullOrWhiteSpace(culture))
            {
                builder.SetCulture(culture);
                _variationContextAccessor.VariationContext = new VariationContext(culture);
                variationContextMutated = true;
            }

            routedRequest = await _publishedRouter.RouteRequestAsync(
                builder,
                new RouteRequestOptions(global::Umbraco.Cms.Core.Routing.RouteDirection.Inbound));

            cancellationToken.ThrowIfCancellationRequested();

            resolvedCulture = routedRequest.Culture;

            if (routedRequest.PublishedContent is null)
            {
                _logger.LogInformation(
                    "PageRenderer: route resolved no content {Path} {Culture}",
                    absoluteUri.AbsolutePath,
                    culture);
                return PageRenderResult.NotFound(resolvedCulture);
            }

            publishedContent = routedRequest.PublishedContent;
            templateAlias = routedRequest.Template?.Alias ?? publishedContent.ContentType.Alias;

            // Mount the routed request on the active context so layouts and partials
            // resolve content from `IUmbracoContext.PublishedRequest` rather than `Model`.
            // Save/restore the previous value to avoid leaking into the outer HTTP request
            // when `EnsureUmbracoContext()` reuses the existing context — fixes the leak
            // documented in deferred-work.md from Spike 0.A code review.
            umbracoContext.PublishedRequest = routedRequest;

            var html = await RenderRazorAsync(
                httpContext,
                routedRequest,
                publishedContent,
                templateAlias,
                cancellationToken);

            return PageRenderResult.Ok(html, publishedContent, templateAlias, resolvedCulture);
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
                publishedContent?.Key,
                absoluteUri.AbsolutePath);
            return PageRenderResult.Failed(ex, publishedContent, templateAlias, resolvedCulture);
        }
        finally
        {
            // Always restore — on success, NotFound, AND failure — so the outer request
            // keeps its original PublishedRequest and VariationContext. Skipping the
            // VariationContext restore on the NotFound path was flagged at code review.
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
