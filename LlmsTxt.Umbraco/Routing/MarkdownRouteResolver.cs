using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace LlmsTxt.Umbraco.Routing;

/// <summary>
/// Default <see cref="IMarkdownRouteResolver"/> wrapping the v17 published-router trio
/// (<see cref="IPublishedRouter"/> + <see cref="IUmbracoContextFactory"/> +
/// <see cref="IFileService"/>). Idempotent <c>EnsureUmbracoContext</c> reuses any
/// ambient context.
/// </summary>
internal sealed class MarkdownRouteResolver : IMarkdownRouteResolver
{
    private readonly IPublishedRouter _publishedRouter;
    private readonly IUmbracoContextFactory _umbracoContextFactory;
    private readonly IFileService _fileService;

    public MarkdownRouteResolver(
        IPublishedRouter publishedRouter,
        IUmbracoContextFactory umbracoContextFactory,
        IFileService fileService)
    {
        _publishedRouter = publishedRouter;
        _umbracoContextFactory = umbracoContextFactory;
        _fileService = fileService;
    }

    public async Task<MarkdownRouteResolution> ResolveAsync(
        Uri absoluteUri,
        CancellationToken cancellationToken)
    {
        using var ctxRef = _umbracoContextFactory.EnsureUmbracoContext();
        var builder = new PublishedRequestBuilder(absoluteUri, _fileService);
        var routedRequest = await _publishedRouter.RouteRequestAsync(
            builder,
            new RouteRequestOptions(global::Umbraco.Cms.Core.Routing.RouteDirection.Inbound));
        cancellationToken.ThrowIfCancellationRequested();

        if (routedRequest?.PublishedContent is null)
        {
            return MarkdownRouteResolution.NotFound();
        }

        return MarkdownRouteResolution.Found(
            routedRequest.PublishedContent,
            routedRequest.Culture);
    }
}
