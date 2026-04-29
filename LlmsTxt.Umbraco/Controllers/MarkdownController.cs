using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LlmsTxt.Umbraco.Controllers;

/// <summary>
/// Renders Umbraco published content as Markdown for the <c>{**path:nonfile}.md</c> route.
/// Delegates the entire rendering+extraction+conversion pipeline to
/// <see cref="IMarkdownContentExtractor"/> — the controller's job is path normalisation,
/// HTTP shape, structured logging, and the seam to <c>ProblemDetails</c>.
/// </summary>
public sealed class MarkdownController : Controller
{
    private readonly IMarkdownContentExtractor _extractor;
    private readonly ILogger<MarkdownController> _logger;

    public MarkdownController(
        IMarkdownContentExtractor extractor,
        ILogger<MarkdownController> logger)
    {
        _extractor = extractor;
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

        var result = await _extractor.ExtractAsync(absoluteUri, cancellationToken);

        switch (result.Status)
        {
            case MarkdownExtractionStatus.NotFound:
                _logger.LogInformation(
                    "Markdown route did not resolve {Path}",
                    canonicalPath);
                return NotFound();

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

                var markdown = result.Markdown;
                Response.Headers[Constants.HttpHeaders.XMarkdownTokens] =
                    EstimateTokenCount(markdown).ToString(System.Globalization.CultureInfo.InvariantCulture);

                return new ContentResult
                {
                    Content = markdown,
                    ContentType = Constants.HttpHeaders.MarkdownContentType,
                    StatusCode = StatusCodes.Status200OK,
                };

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

    /// <summary>
    /// Cloudflare-convention <c>X-Markdown-Tokens</c> header value — character-based
    /// estimator (<c>length / 4</c>, rounded). Full BPE tokenisation is out of scope
    /// for v1; the estimate is informational. See <c>package-spec.md § 6 Public surface</c>.
    /// </summary>
    private static int EstimateTokenCount(string markdown)
        => Math.Max(1, (int)Math.Round(markdown.Length / 4.0, MidpointRounding.AwayFromZero));
}
