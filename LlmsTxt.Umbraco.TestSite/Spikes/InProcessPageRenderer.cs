using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace LlmsTxt.Umbraco.TestSite.Spikes;

/// <summary>
/// SPIKE-ONLY in-process Razor renderer. Validates the documented v17 chain:
///   IPublishedRouter.RouteRequestAsync (with a constructed PublishedRequestBuilder)
///   -> IUmbracoContextFactory.EnsureUmbracoContext()
///   -> set IUmbracoContext.PublishedRequest
///   -> IRazorViewEngine.GetView/FindView
///   -> ViewExecutor (rendered to StringWriter).
///
/// This type intentionally lives in the TestSite, not in the package. The
/// production PageRenderer is built in Story 1.1, informed by what this spike
/// learns. Do NOT replicate this pattern verbatim into the package — Story 1.1
/// will rebuild it as a long-lived component with its own tests.
/// </summary>
public sealed class InProcessPageRenderer
{
    private readonly IPublishedRouter _publishedRouter;
    private readonly IUmbracoContextFactory _umbracoContextFactory;
    private readonly IFileService _fileService;
    private readonly IRazorViewEngine _razorViewEngine;
    private readonly ITempDataProvider _tempDataProvider;
    private readonly IVariationContextAccessor _variationContextAccessor;
    private readonly ILogger<InProcessPageRenderer> _logger;

    public InProcessPageRenderer(
        IPublishedRouter publishedRouter,
        IUmbracoContextFactory umbracoContextFactory,
        IFileService fileService,
        IRazorViewEngine razorViewEngine,
        ITempDataProvider tempDataProvider,
        IVariationContextAccessor variationContextAccessor,
        ILogger<InProcessPageRenderer> logger)
    {
        _publishedRouter = publishedRouter;
        _umbracoContextFactory = umbracoContextFactory;
        _fileService = fileService;
        _razorViewEngine = razorViewEngine;
        _tempDataProvider = tempDataProvider;
        _variationContextAccessor = variationContextAccessor;
        _logger = logger;
    }

    public async Task<InProcessRenderResult> RenderAsync(
        HttpContext httpContext,
        string path,
        string? culture,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var diagnostics = new RendererDiagnostics();

        try
        {
            using var ctxRef = _umbracoContextFactory.EnsureUmbracoContext();
            var umbracoContext = ctxRef.UmbracoContext;

            var absoluteUri = BuildAbsoluteUri(httpContext, path);

            var builder = new PublishedRequestBuilder(absoluteUri, _fileService);
            if (!string.IsNullOrWhiteSpace(culture))
            {
                builder.SetCulture(culture);
                _variationContextAccessor.VariationContext = new VariationContext(culture);
            }

            var routedRequest = await _publishedRouter.RouteRequestAsync(
                builder,
                new RouteRequestOptions(global::Umbraco.Cms.Core.Routing.RouteDirection.Inbound));

            cancellationToken.ThrowIfCancellationRequested();

            if (routedRequest.PublishedContent is null)
            {
                _logger.LogInformation(
                    "Spike render: route resolved no content {Path} {Culture}",
                    path,
                    culture);
                return InProcessRenderResult.NotFound(diagnostics with { ResolvedCulture = routedRequest.Culture });
            }

            var publishedContent = routedRequest.PublishedContent;
            diagnostics = diagnostics with
            {
                ContentKey = publishedContent.Key,
                ContentId = publishedContent.Id,
                TemplateAlias = routedRequest.Template?.Alias,
                ResolvedCulture = routedRequest.Culture
            };

            umbracoContext.PublishedRequest = routedRequest;

            var html = await RenderRazorAsync(
                httpContext,
                routedRequest,
                publishedContent,
                cancellationToken);

            sw.Stop();
            return InProcessRenderResult.Ok(html, diagnostics with { RenderMs = sw.ElapsedMilliseconds });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(
                ex,
                "Spike render failed {Path} {Culture} after {RenderMs}ms",
                path,
                culture,
                sw.ElapsedMilliseconds);
            return InProcessRenderResult.Error(
                ex,
                diagnostics with { RenderMs = sw.ElapsedMilliseconds });
        }
    }

    private static Uri BuildAbsoluteUri(HttpContext httpContext, string path)
    {
        var request = httpContext.Request;
        var scheme = request.Scheme;
        var host = request.Host.Value ?? "localhost";
        var normalizedPath = path.StartsWith('/') ? path : "/" + path;
        return new Uri($"{scheme}://{host}{normalizedPath}");
    }

    private async Task<string> RenderRazorAsync(
        HttpContext httpContext,
        IPublishedRequest publishedRequest,
        IPublishedContent publishedContent,
        CancellationToken cancellationToken)
    {
        var templateAlias = publishedRequest.Template?.Alias
            ?? publishedContent.ContentType.Alias;

        var viewName = $"~/Views/{templateAlias}.cshtml";
        var routeData = new RouteData();
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewResult = _razorViewEngine.GetView(executingFilePath: null, viewPath: viewName, isMainPage: true);
        if (!viewResult.Success)
        {
            viewResult = _razorViewEngine.FindView(actionContext, templateAlias, isMainPage: true);
        }

        if (!viewResult.Success || viewResult.View is null)
        {
            var searchedLocations = string.Join(", ", viewResult.SearchedLocations ?? Array.Empty<string>());
            throw new InvalidOperationException(
                $"Spike: could not resolve Razor view for template '{templateAlias}'. Searched: {searchedLocations}");
        }

        var view = viewResult.View;

        // Pass a non-generic ViewDataDictionary so Umbraco's ContentModelBinder
        // (invoked from UmbracoViewPage<TModel>.BindViewData) handles the
        // conversion from IPublishedContent to whatever typed PublishedModel
        // (Home / ArticleList / Content / etc.) the template was generated for
        // by ModelsBuilder. Passing ViewDataDictionary<IPublishedContent>
        // directly fails with InvalidCastException because the runtime can't
        // downcast a typed dictionary across PublishedModel hierarchies.
        var viewData = new ViewDataDictionary(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = publishedContent
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

public sealed record InProcessRenderResult(
    string Status,
    string? Html,
    RendererDiagnostics Diagnostics,
    string? ExceptionType,
    string? ExceptionMessage,
    string? ExceptionStack)
{
    public static InProcessRenderResult Ok(string html, RendererDiagnostics diagnostics) =>
        new("ok", html, diagnostics, null, null, null);

    public static InProcessRenderResult NotFound(RendererDiagnostics diagnostics) =>
        new("not-found", null, diagnostics, null, null, null);

    public static InProcessRenderResult Error(Exception ex, RendererDiagnostics diagnostics) =>
        new("error", null, diagnostics, ex.GetType().FullName, ex.Message, ex.StackTrace);
}

public sealed record RendererDiagnostics(
    long RenderMs = 0,
    Guid? ContentKey = null,
    int? ContentId = null,
    string? TemplateAlias = null,
    string? ResolvedCulture = null);
