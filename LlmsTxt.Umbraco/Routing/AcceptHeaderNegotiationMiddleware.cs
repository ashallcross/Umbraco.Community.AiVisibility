using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Web.Common.Routing;

namespace LlmsTxt.Umbraco.Routing;

/// <summary>
/// <see cref="Umbraco.Cms.Web.Common.ApplicationBuilder.UmbracoPipelineFilter.PostRouting"/>
/// middleware that performs <c>Accept: text/markdown</c> content negotiation on canonical
/// (non-<c>.md</c>) URLs. Reads <see cref="UmbracoRouteValues"/> from
/// <see cref="HttpContext.Features"/> after Umbraco's route resolution, short-circuits to
/// Markdown rendering when the client prefers it, otherwise calls <c>next()</c> and HTML
/// rendering proceeds.
///
/// <para>
/// Always emits <c>Vary: Accept</c> on the eventual response — required so downstream
/// caches don't reuse a Markdown body for an HTML caller (architecture.md § Caching &amp; HTTP).
/// </para>
/// </summary>
internal sealed class AcceptHeaderNegotiationMiddleware : IMiddleware
{
    private const string MarkdownMediaType = "text/markdown";
    private const string ExcludeFromLlmExportsAlias = "excludeFromLlmExports";

    private readonly IMarkdownContentExtractor _extractor;
    private readonly IMarkdownResponseWriter _writer;
    private readonly ILlmsSettingsResolver _settingsResolver;
    private readonly ILogger<AcceptHeaderNegotiationMiddleware> _logger;

    public AcceptHeaderNegotiationMiddleware(
        IMarkdownContentExtractor extractor,
        IMarkdownResponseWriter writer,
        ILlmsSettingsResolver settingsResolver,
        ILogger<AcceptHeaderNegotiationMiddleware> logger)
    {
        _extractor = extractor;
        _writer = writer;
        _settingsResolver = settingsResolver;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Always append `Vary: Accept` to the eventual response — even when we don't
        // divert. Use OnStarting so it lands on whatever the downstream pipeline writes
        // (HTML controller, static file, etc.). Append-not-overwrite to coexist with
        // adopters who set their own Vary headers.
        context.Response.OnStarting(AppendVaryAccept, context);

        // Method gate — only GET / HEAD negotiate. Other methods fall through.
        if (!HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        // Suffix gate — `.md` requests are owned by the LlmsPipelineFilter endpoints
        // route + MarkdownController; never double-handle.
        if (context.Request.Path.HasValue
            && context.Request.Path.Value!.EndsWith(
                Constants.Routes.MarkdownSuffix,
                StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Route gate — Umbraco hasn't routed this request to a published page (admin URL,
        // static file, custom MVC route) → not ours.
        var routeValues = context.Features.Get<UmbracoRouteValues>();
        if (routeValues?.PublishedRequest?.PublishedContent is null)
        {
            await next(context);
            return;
        }

        // Accept gate — does the client prefer text/markdown?
        if (!ClientPrefersMarkdown(context.Request.Headers.Accept))
        {
            await next(context);
            return;
        }

        var content = routeValues.PublishedRequest.PublishedContent;
        var culture = routeValues.PublishedRequest.Culture;
        var canonicalPath = context.Request.Path.Value ?? "/";

        // Story 3.1 § Failure & Edge Cases line 463 — exclusion check on the
        // negotiation path. Without it, an excluded page accessed via
        // `Accept: text/markdown` on its canonical URL would still be served
        // as Markdown (the middleware bypasses MarkdownController). Same
        // shape as MarkdownController.IsExcludedAsync: per-page bool first,
        // resolver second; resolver throws fail-open per spec.
        var host = context.Request.Host.HasValue ? context.Request.Host.Host : null;
        if (await IsExcludedAsync(content, culture, host, context.RequestAborted))
        {
            _logger.LogInformation(
                "Accept-negotiation — page excluded from LLM exports {ContentKey} {Path}",
                content.Key,
                canonicalPath);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.Headers[Constants.HttpHeaders.CacheControl] = "no-store";
            context.Response.ContentType = "application/problem+json; charset=utf-8";
            await context.Response.WriteAsync(
                "{\"title\":\"Page excluded from LLM exports\",\"status\":404}",
                context.RequestAborted);
            return;
        }

        var result = await _extractor.ExtractAsync(content, culture, context.RequestAborted);

        switch (result.Status)
        {
            case MarkdownExtractionStatus.Found:
                if (string.IsNullOrEmpty(result.Markdown))
                {
                    _logger.LogWarning(
                        "Accept-negotiation extractor returned Found with empty body {ContentKey} {Path}",
                        result.ContentKey,
                        canonicalPath);
                    // Fall through to HTML — better empty than a 500 with no body.
                    await next(context);
                    return;
                }

                _logger.LogDebug(
                    "Accept-negotiation diverted to Markdown {ContentKey} {Path}",
                    result.ContentKey,
                    canonicalPath);
                await _writer.WriteAsync(result, canonicalPath, culture, context);
                return;

            case MarkdownExtractionStatus.Error:
                _logger.LogWarning(
                    result.Error,
                    "Accept-negotiation extraction failed {ContentKey} {Path}",
                    result.ContentKey,
                    canonicalPath);
                // Symmetric with MarkdownController — surface a 500 ProblemDetails-shaped
                // body so adopters debugging via curl see something actionable rather
                // than a silent fall-through to HTML for a broken extraction.
                // Cache-Control: no-store prevents shared caches and CDNs from poisoning
                // the canonical URL with a transient extractor failure.
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.Headers[Constants.HttpHeaders.CacheControl] = "no-store";
                context.Response.ContentType = "application/problem+json; charset=utf-8";
                await context.Response.WriteAsync(
                    "{\"title\":\"Markdown extraction failed\",\"status\":500}",
                    context.RequestAborted);
                return;

            default:
                throw new InvalidOperationException(
                    $"Unhandled extraction status: {result.Status}");
        }
    }

    private static Task AppendVaryAccept(object state)
    {
        VaryHeaderHelper.AppendAccept((HttpContext)state);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Story 3.1 — return <c>true</c> when the page should be omitted from LLM
    /// exports on the Accept-negotiation divert path. Mirrors
    /// <c>MarkdownController.IsExcludedAsync</c>: per-page bool checked first
    /// (no try/catch — a throwing custom property converter is a defect that
    /// should surface), resolver second (fail-open on throw to avoid
    /// blackholing every request when the resolver glitches).
    /// </summary>
    private async Task<bool> IsExcludedAsync(
        IPublishedContent content,
        string? culture,
        string? host,
        CancellationToken cancellationToken)
    {
        // Per-page bool — invariant composition; pass culture: null. See
        // MarkdownController.TryReadExcludeBool for the trap rationale.
        _ = culture;
        var prop = content.GetProperty(ExcludeFromLlmExportsAlias);
        if (prop is not null && prop.HasValue(culture: null))
        {
            var value = prop.GetValue(culture: null);
            if (value is bool b && b)
            {
                return true;
            }
        }

        ResolvedLlmsSettings resolved;
        try
        {
            resolved = await _settingsResolver
                .ResolveAsync(host, culture, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Accept-negotiation — ILlmsSettingsResolver threw for {Host} {Culture}; treating as not-excluded (fail-open)",
                host,
                culture);
            return false;
        }

        return resolved.ExcludedDoctypeAliases
            .Contains(content.ContentType.Alias, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Append-not-overwrite <c>Vary: Accept</c>. Delegates to the shared
    /// <see cref="VaryHeaderHelper"/> so the same logic runs from the
    /// <see cref="HttpResponse.OnStarting(Func{Task})"/> callback (non-divert)
    /// AND from <see cref="MarkdownResponseWriter"/> (divert) — see test calls
    /// that exercise this directly because <c>OnStarting</c> is brittle on
    /// <see cref="DefaultHttpContext"/>.
    /// </summary>
    internal static void AppendVaryAcceptHeader(HttpContext context)
        => VaryHeaderHelper.AppendAccept(context);

    /// <summary>
    /// Returns true when <c>text/markdown</c> is the highest-quality match in the
    /// <c>Accept</c> header. <c>*/*</c> alone is treated as "no preference" → returns
    /// false (HTML wins, browser default). Tied quality resolves to the first listed
    /// media type for principle of least surprise.
    /// </summary>
    internal static bool ClientPrefersMarkdown(string? acceptHeaderValue)
    {
        if (string.IsNullOrWhiteSpace(acceptHeaderValue)) return false;

        // MediaTypeHeaderValue.TryParseList tolerates malformed entries by skipping them
        // (returns false on totally invalid input).
        if (!MediaTypeHeaderValue.TryParseList(
                new[] { acceptHeaderValue },
                out var parsed))
        {
            return false;
        }

        // Find the highest-quality entry. Quality defaults to 1.0 when q= is absent
        // (RFC 7231 § 5.3.1). Stable on insertion order so q-tied entries resolve to
        // the FIRST listed. Initial bestQuality = 0 (not -1) so a single q=0 entry
        // — which RFC 7231 § 5.3.1 says is "not acceptable" — never becomes `best`.
        MediaTypeHeaderValue? best = null;
        double bestQuality = 0.0;
        foreach (var mt in parsed)
        {
            var q = mt.Quality ?? 1.0;
            if (q > bestQuality)
            {
                best = mt;
                bestQuality = q;
            }
        }

        if (best is null) return false;

        // A bare `*/*` (or `*/*;q=1.0` etc.) is "no preference" — let HTML win.
        return best.MediaType.HasValue
               && best.MediaType.Value.Equals(MarkdownMediaType, StringComparison.OrdinalIgnoreCase);
    }
}
