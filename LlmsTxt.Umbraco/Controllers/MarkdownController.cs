using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LlmsTxt.Umbraco.Controllers;

/// <summary>
/// Renders Umbraco published content as Markdown for the <c>{**path}</c> route guarded by
/// <see cref="LlmsPipelineFilter"/>'s <c>.md</c> suffix constraint. Story 1.2 made this
/// controller responsible for route resolution: it converts a captured path to an
/// <see cref="Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent"/> via
/// <see cref="IMarkdownRouteResolver"/>, returns 404 directly when the route doesn't resolve, and
/// only invokes <see cref="IMarkdownContentExtractor"/> on resolution success — letting the
/// caching decorator key on <c>(nodeKey, culture)</c> without re-routing. Story 1.3 extracted
/// the response shape (<c>ETag</c> / <c>Cache-Control</c> / <c>Vary</c> / 304) to
/// <see cref="IMarkdownResponseWriter"/> so the same shape is reused by
/// <see cref="AcceptHeaderNegotiationMiddleware"/> on canonical URLs.
/// </summary>
public sealed class MarkdownController : Controller
{
    private readonly IMarkdownContentExtractor _extractor;
    private readonly IMarkdownRouteResolver _routeResolver;
    private readonly IMarkdownResponseWriter _responseWriter;
    private readonly ILogger<MarkdownController> _logger;

    public MarkdownController(
        IMarkdownContentExtractor extractor,
        IMarkdownRouteResolver routeResolver,
        IMarkdownResponseWriter responseWriter,
        ILogger<MarkdownController> logger)
    {
        _extractor = extractor;
        _routeResolver = routeResolver;
        _responseWriter = responseWriter;
        _logger = logger;
    }

    [HttpGet]
    [HttpHead]
    public async Task<IActionResult> Render(string path, CancellationToken cancellationToken)
    {
        // Route values can deliver the captured path with or without leading slash;
        // the normaliser handles both, plus URL-decoding and suffix stripping.
        string canonicalPath;
        try
        {
            canonicalPath = MarkdownPathNormaliser.NormaliseToCanonical(path);
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation(
                ex,
                "Markdown route received malformed path {Path}",
                path);
            return NotFound();
        }

        Uri absoluteUri;
        try
        {
            absoluteUri = BuildAbsoluteUri(canonicalPath);
        }
        catch (UriFormatException ex)
        {
            // Malformed Host header (IPv6 oddities, double dots, etc.) — return 400 not 500.
            _logger.LogInformation(
                ex,
                "Markdown route could not build absolute URI for {Path}",
                canonicalPath);
            return BadRequest();
        }

        // Resolve the route → IPublishedContent. 404 directly when no content matches.
        var resolution = await _routeResolver.ResolveAsync(absoluteUri, cancellationToken);
        if (resolution.Content is null)
        {
            _logger.LogInformation(
                "Markdown route did not resolve {Path}",
                canonicalPath);
            return NotFound();
        }

        var result = await _extractor.ExtractAsync(resolution.Content, resolution.Culture, cancellationToken);

        switch (result.Status)
        {
            case MarkdownExtractionStatus.Error:
                _logger.LogWarning(
                    result.Error,
                    "Markdown extraction failed {ContentKey} {Path}",
                    result.ContentKey,
                    canonicalPath);
                return Problem(
                    title: "Markdown extraction failed",
                    statusCode: StatusCodes.Status500InternalServerError);

            case MarkdownExtractionStatus.Found:
                if (string.IsNullOrEmpty(result.Markdown))
                {
                    _logger.LogWarning(
                        "Markdown extractor returned Found with empty body {ContentKey} {Path}",
                        result.ContentKey,
                        canonicalPath);
                    return Problem(
                        title: "Markdown extraction produced no content",
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                await _responseWriter.WriteAsync(result, canonicalPath, resolution.Culture, HttpContext);
                return new EmptyResult();

            default:
                // Defensive — unreachable. New enum members must be handled explicitly.
                throw new InvalidOperationException(
                    $"Unhandled extraction status: {result.Status}");
        }
    }

    private Uri BuildAbsoluteUri(string canonicalPath)
    {
        var request = HttpContext.Request;
        var scheme = request.Scheme;
        var host = request.Host.HasValue ? request.Host.Value! : "localhost";
        // Note: the `Uri` ctor lowercases the authority component, so an incoming
        // `Host: SiteA.Example` surfaces as `sitea.example` in `AbsoluteUri`.
        return new Uri($"{scheme}://{host}{canonicalPath}");
    }
}
