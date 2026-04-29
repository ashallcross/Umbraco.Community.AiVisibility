using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LlmsTxt.Umbraco.Caching;
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
/// <see cref="IPublishedRouter"/>, returns 404 directly when the route doesn't resolve, and
/// only invokes <see cref="IMarkdownContentExtractor"/> on resolution success — letting the
/// caching decorator key on <c>(nodeKey, culture)</c> without re-routing.
/// </summary>
public sealed class MarkdownController : Controller
{
    private readonly IMarkdownContentExtractor _extractor;
    private readonly IMarkdownRouteResolver _routeResolver;
    private readonly IOptionsMonitor<LlmsTxtSettings> _settings;
    private readonly ILogger<MarkdownController> _logger;

    public MarkdownController(
        IMarkdownContentExtractor extractor,
        IMarkdownRouteResolver routeResolver,
        IOptionsMonitor<LlmsTxtSettings> settings,
        ILogger<MarkdownController> logger)
    {
        _extractor = extractor;
        _routeResolver = routeResolver;
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

                return BuildSuccessResponse(result, canonicalPath, resolution.Culture);

            default:
                // Defensive — unreachable. New enum members must be handled explicitly.
                throw new InvalidOperationException(
                    $"Unhandled extraction status: {result.Status}");
        }
    }

    private IActionResult BuildSuccessResponse(
        MarkdownExtractionResult result,
        string canonicalPath,
        string? culture)
    {
        var markdown = result.Markdown!;
        var etag = ComputeETag(canonicalPath, culture, result.UpdatedUtc ?? DateTime.UnixEpoch);

        // Always emit Vary: Accept and Cache-Control on responses we serve from this
        // route — even on 304s — per RFC 7232 § 4.1 ("MUST send a Cache-Control,
        // Content-Location, Date, ETag, Expires, and Vary header field that would
        // have been sent in a 200 response to the same request").
        var headers = Response.Headers;
        headers[Constants.HttpHeaders.Vary] = "Accept";
        headers[Constants.HttpHeaders.CacheControl] = BuildCacheControl();
        headers[Constants.HttpHeaders.ETag] = etag;

        if (RequestMatchesETag(etag))
        {
            // 304 Not Modified — empty body, no Content-Type. ETag/Cache-Control/Vary
            // already set above so the response matches what the 200 would have carried.
            return StatusCode(StatusCodes.Status304NotModified);
        }

        headers[Constants.HttpHeaders.XMarkdownTokens] =
            EstimateTokenCount(markdown).ToString(CultureInfo.InvariantCulture);

        return new ContentResult
        {
            Content = markdown,
            ContentType = Constants.HttpHeaders.MarkdownContentType,
            StatusCode = StatusCodes.Status200OK,
        };
    }

    private string BuildCacheControl()
    {
        var seconds = Math.Max(0, _settings.CurrentValue.CachePolicySeconds);
        return $"public, max-age={seconds.ToString(CultureInfo.InvariantCulture)}";
    }

    private bool RequestMatchesETag(string etag)
    {
        if (!Request.Headers.TryGetValue(Constants.HttpHeaders.IfNoneMatch, out var values)
            || values.Count == 0)
        {
            return false;
        }

        // RFC 7232 § 3.2 — If-None-Match can carry a comma-separated list. Match against any.
        // We always emit strong validators (no W/ prefix); accept either form for input.
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var candidate in raw.Split(','))
            {
                var trimmed = candidate.Trim();

                // RFC 7232 § 3.2 wildcard is the bare token "*" only — `W/*` is malformed
                // and must NOT match. Check before the W/ strip so a malformed `W/*`
                // doesn't degrade into a bare `*` and short-circuit to 304.
                if (string.Equals(trimmed, "*", StringComparison.Ordinal))
                {
                    return true;
                }

                if (trimmed.StartsWith("W/", StringComparison.Ordinal))
                {
                    trimmed = trimmed[2..];
                }

                if (string.Equals(trimmed, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Stable ETag derived from <c>(route + culture + contentVersion)</c>. Uses
    /// <see cref="IPublishedContent.UpdateDate"/> (UTC) as the contentVersion proxy —
    /// every successful publish bumps it, and the cache invalidation broadcast fires on
    /// the same publish, so the ETag and the cached entry are always in lockstep.
    /// </summary>
    private static string ComputeETag(string canonicalPath, string? culture, DateTime updatedUtc)
    {
        // Culture normalisation must match the cache-key composition (LlmsCacheKeys.Page)
        // so an If-None-Match round-trip stays stable across casing variations between
        // request and cached entry — `en-GB` and `en-gb` both reduce to `en-gb`, null
        // and empty both reduce to `_`.
        var input = string.Concat(
            canonicalPath,
            "|",
            LlmsCacheKeys.NormaliseCulture(culture),
            "|",
            updatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // Base64-url first 12 bytes → 16 chars; quoted strong validator per RFC 7232.
        var hash = Convert.ToBase64String(bytes, 0, 12)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return $"\"{hash}\"";
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
