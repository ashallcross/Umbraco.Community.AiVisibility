using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// <para>
/// Story 3.1 inserts an exclusion-check step (architecture flow A line 882) between
/// route resolution and extraction: pages whose <c>ContentType.Alias</c> is in the
/// resolved <c>ExcludedDoctypeAliases</c> OR whose <c>excludeFromLlmExports</c>
/// composition property is <c>true</c> are returned as 404. Story 4.1 lifted that
/// rule into the shared <see cref="ILlmsExclusionEvaluator"/> so the discoverability
/// header middleware and <c>&lt;llms-link /&gt;</c> / <c>&lt;llms-hint /&gt;</c>
/// TagHelpers consume the same answer.
/// </para>
/// <para>
/// Story 4.1 also resolves the Cloudflare <c>Content-Signal</c> header per-request
/// via <see cref="ContentSignalResolver"/> and passes it down to the writer. The
/// writer stays Singleton (no captive Scoped resolver dependency).
/// </para>
/// </summary>
public sealed class MarkdownController : Controller
{
    private readonly IMarkdownContentExtractor _extractor;
    private readonly IMarkdownRouteResolver _routeResolver;
    private readonly IMarkdownResponseWriter _responseWriter;
    private readonly ILlmsExclusionEvaluator _exclusionEvaluator;
    private readonly IOptionsMonitor<LlmsTxtSettings> _settings;
    private readonly ILogger<MarkdownController> _logger;

    public MarkdownController(
        IMarkdownContentExtractor extractor,
        IMarkdownRouteResolver routeResolver,
        IMarkdownResponseWriter responseWriter,
        ILlmsExclusionEvaluator exclusionEvaluator,
        IOptionsMonitor<LlmsTxtSettings> settings,
        ILogger<MarkdownController> logger)
    {
        _extractor = extractor;
        _routeResolver = routeResolver;
        _responseWriter = responseWriter;
        _exclusionEvaluator = exclusionEvaluator;
        _settings = settings;
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

        // Story 3.1 — exclusion check via shared evaluator (Story 4.1 lift).
        // Pages whose doctype alias is in the resolved exclusion list OR whose
        // `excludeFromLlmExports` composition property is true return 404.
        var host = HttpContext.Request.Host.HasValue ? HttpContext.Request.Host.Host : null;
        if (await _exclusionEvaluator.IsExcludedAsync(resolution.Content, resolution.Culture, host, cancellationToken))
        {
            _logger.LogInformation(
                "Markdown route — page excluded from LLM exports {ContentKey} {Path}",
                resolution.Content.Key,
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

                // Story 4.1 — Cloudflare Content-Signal header. Resolved per-doctype
                // (override) → site-default → null. Caller-resolved so the writer
                // stays Singleton (no captive Scoped resolver dependency).
                var contentSignal = ContentSignalResolver.Resolve(
                    _settings.CurrentValue,
                    resolution.Content.ContentType.Alias);

                await _responseWriter.WriteAsync(result, canonicalPath, resolution.Culture, contentSignal, HttpContext);
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
