using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;

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
/// route resolution and extraction: <see cref="ILlmsSettingsResolver"/> overlays the
/// Settings doctype values onto appsettings, and pages whose <c>ContentType.Alias</c>
/// is in <c>resolved.ExcludedDoctypeAliases</c> OR whose <c>excludeFromLlmExports</c>
/// composition property is <c>true</c> are returned as 404 (excluded pages are not
/// addressable as Markdown). Resolver-throw graceful degradation: catch + log Warning
/// + treat as not-excluded (fail-open — same shape as Story 2.3 hreflang resolver).
/// </para>
/// </summary>
public sealed class MarkdownController : Controller
{
    internal const string ExcludeFromLlmExportsAlias = "excludeFromLlmExports";

    private readonly IMarkdownContentExtractor _extractor;
    private readonly IMarkdownRouteResolver _routeResolver;
    private readonly IMarkdownResponseWriter _responseWriter;
    private readonly ILlmsSettingsResolver _settingsResolver;
    private readonly ILogger<MarkdownController> _logger;

    public MarkdownController(
        IMarkdownContentExtractor extractor,
        IMarkdownRouteResolver routeResolver,
        IMarkdownResponseWriter responseWriter,
        ILlmsSettingsResolver settingsResolver,
        ILogger<MarkdownController> logger)
    {
        _extractor = extractor;
        _routeResolver = routeResolver;
        _responseWriter = responseWriter;
        _settingsResolver = settingsResolver;
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

        // Story 3.1 — exclusion check. Pages whose doctype alias is in the
        // resolved exclusion list OR whose `excludeFromLlmExports` composition
        // property is true return 404 (excluded pages are not addressable as
        // Markdown). Resolver-throw graceful degradation: log + fail-open.
        var host = HttpContext.Request.Host.HasValue ? HttpContext.Request.Host.Host : null;
        if (await IsExcludedAsync(resolution.Content, resolution.Culture, host, cancellationToken))
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

    /// <summary>
    /// Story 3.1 — return <c>true</c> when the page should be omitted from LLM
    /// exports. Two routes:
    /// <list type="bullet">
    ///   <item>The page carries the <c>llmsTxtSettingsComposition</c> composition
    ///   AND its <c>excludeFromLlmExports</c> property is <c>true</c>.</item>
    ///   <item>The page's <c>ContentType.Alias</c> is in the resolved
    ///   <see cref="ResolvedLlmsSettings.ExcludedDoctypeAliases"/> set.</item>
    /// </list>
    /// <para>
    /// <b>Failure mode:</b> exceptions from
    /// <see cref="ILlmsSettingsResolver.ResolveAsync"/> are caught and treated
    /// as "not excluded" + Warning log (fail-open on the resolver path). The
    /// per-page bool read at <see cref="TryReadExcludeBool"/> is NOT wrapped —
    /// a throwing custom property converter would propagate to the caller. This
    /// is intentional: a throwing per-page bool is a content/extension defect
    /// and silently fail-opening would mask it. Adopters who want fail-open on
    /// all paths register a custom <see cref="ILlmsSettingsResolver"/> that
    /// also handles per-page bool semantics.
    /// </para>
    /// </summary>
    private async Task<bool> IsExcludedAsync(
        IPublishedContent content,
        string? culture,
        string? host,
        CancellationToken cancellationToken)
    {
        // Per-page bool — read regardless of resolver outcome. Pages whose
        // doctype doesn't include the composition return null from GetProperty,
        // which we treat as "not excluded".
        if (TryReadExcludeBool(content, culture))
        {
            return true;
        }

        ResolvedLlmsSettings resolved;
        try
        {
            resolved = await _settingsResolver.ResolveAsync(host, culture, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ILlmsSettingsResolver threw for {Host} {Culture}; treating as not-excluded (fail-open)",
                host,
                culture);
            return false;
        }

        return resolved.ExcludedDoctypeAliases
            .Contains(content.ContentType.Alias, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Read <c>excludeFromLlmExports</c> via the property layer
    /// (<see cref="IPublishedContent.GetProperty"/> +
    /// <see cref="IPublishedProperty.GetValue"/>) — same trap-avoiding shape
    /// as <see cref="Builders.DefaultLlmsTxtBuilder"/> (the ambient
    /// <c>page.Value&lt;bool&gt;()</c> extension service-locates
    /// <c>IPublishedValueFallback</c> at static-init time and NPEs in unit tests).
    /// Defensive cast: pages with a string-typed property (legacy data import)
    /// fall through as not-excluded.
    /// </summary>
    private static bool TryReadExcludeBool(IPublishedContent content, string? culture)
    {
        // The excludeFromLlmExports property lives on llmsTxtSettingsComposition
        // which is invariant. Pass culture: null to match the invariant value;
        // passing a non-null culture causes Umbraco to look for a non-existent
        // culture-variant and return false even when the bool is set.
        // Same gotcha hit at Story 3.1 manual gate Step 4 for the resolver.
        _ = culture; // intentionally unused — kept on signature for interface-level symmetry
        var prop = content.GetProperty(ExcludeFromLlmExportsAlias);
        if (prop is null || !prop.HasValue(culture: null))
        {
            return false;
        }

        var value = prop.GetValue(culture: null);
        return value is bool b && b;
    }
}
