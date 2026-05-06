using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Web.Common.Routing;

namespace LlmsTxt.Umbraco.Routing;

/// <summary>
/// Story 4.1 — <see cref="Umbraco.Cms.Web.Common.ApplicationBuilder.UmbracoPipelineFilter.PostRouting"/>
/// middleware that emits the HTTP <c>Link: rel="alternate"; type="text/markdown"</c>
/// header (and idempotent <c>Vary: Accept</c>) on opted-in HTML responses.
/// <para>
/// Gates (in order): method (GET/HEAD only) → suffix (skip <c>.md</c> /
/// <c>/index.html.md</c> self-requests) → kill switch
/// (<see cref="DiscoverabilityHeaderSettings.Enabled"/>) → route
/// (<see cref="UmbracoRouteValues.PublishedRequest"/> resolved) → exclusion
/// (<see cref="IExclusionEvaluator"/>) → URL provider success
/// (non-null/non-<c>#</c> response).
/// </para>
/// <para>
/// Headers are flushed via <see cref="HttpResponse.OnStarting(System.Func{System.Threading.Tasks.Task})"/>
/// gated on <c>StatusCode &lt; 300</c> so downstream filters that rewrite the response
/// to 4xx/5xx (Umbraco custom-error fallback, exception handlers, response-compression
/// status flips) do NOT carry a Link: rel="alternate" pointer onto an error response.
/// <c>VaryHeaderHelper.AppendAccept</c> is idempotent so the sibling
/// <c>AcceptHeaderNegotiationMiddleware</c>'s OnStarting Vary write does not produce a
/// duplicate token.
/// </para>
/// </summary>
internal sealed class DiscoverabilityHeaderMiddleware : IMiddleware
{
    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly IExclusionEvaluator _exclusion;
    private readonly IPublishedUrlProvider _urlProvider;
    private readonly ILogger<DiscoverabilityHeaderMiddleware> _logger;

    public DiscoverabilityHeaderMiddleware(
        IOptionsMonitor<AiVisibilitySettings> settings,
        IExclusionEvaluator exclusion,
        IPublishedUrlProvider urlProvider,
        ILogger<DiscoverabilityHeaderMiddleware> logger)
    {
        _settings = settings;
        _exclusion = exclusion;
        _urlProvider = urlProvider;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Method gate — only GET / HEAD carry HTML responses we'd annotate.
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        // Suffix gate — `.md` and `/index.html.md` requests are markdown
        // responses (their writer already sets X-Markdown-Tokens / Content-Signal);
        // a Link: rel="alternate" pointing back to themselves is wrong on those
        // routes. Skip. TrimEnd('/') normalises adopter rewrites that arrive with
        // a trailing slash (e.g. `/index.html.md/`) — without the trim, the
        // EndsWith gate misses and the middleware would attach Link to a
        // Markdown response.
        var path = context.Request.Path;
        if (path.HasValue)
        {
            var trimmed = path.Value!.TrimEnd('/');
            if (trimmed.EndsWith(Constants.Routes.MarkdownSuffix, StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(Constants.Routes.IndexHtmlMdSuffix, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }
        }

        // Kill switch — read live via IOptionsMonitor.
        if (!_settings.CurrentValue.DiscoverabilityHeader.Enabled)
        {
            await next(context);
            return;
        }

        // Route gate — Umbraco hasn't routed this request to a published page
        // (admin URL, static file, custom MVC route) → not ours.
        var routeValues = context.Features.Get<UmbracoRouteValues>();
        var content = routeValues?.PublishedRequest?.PublishedContent;
        if (content is null)
        {
            await next(context);
            return;
        }

        var culture = routeValues!.PublishedRequest!.Culture;
        var host = context.Request.Host.HasValue ? context.Request.Host.Host : null;

        // Exclusion gate — same evaluator as MarkdownController + Accept negotiation.
        if (await _exclusion.IsExcludedAsync(content, culture, host, context.RequestAborted))
        {
            await next(context);
            return;
        }

        // Compute alternate URL via IPublishedUrlProvider → MarkdownAlternateUrl helper.
        Uri requestUri;
        try
        {
            var hostHeader = host ?? "localhost";
            requestUri = new Uri($"{context.Request.Scheme}://{hostHeader}{context.Request.Path}{context.Request.QueryString}");
        }
        catch (UriFormatException ex)
        {
            _logger.LogDebug(
                ex,
                "DiscoverabilityHeaderMiddleware — could not build request Uri for {Host} {Path}; skipping Link header",
                host,
                context.Request.Path.ToString());
            await next(context);
            return;
        }

        string canonicalUrl;
        try
        {
            canonicalUrl = _urlProvider.GetUrl(content, UrlMode.Default, culture, requestUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "DiscoverabilityHeaderMiddleware — IPublishedUrlProvider.GetUrl threw for {ContentKey} {Culture}; suppressing Link header (fail-open)",
                content.Key,
                culture);
            await next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(canonicalUrl) || canonicalUrl == "#")
        {
            _logger.LogDebug(
                "DiscoverabilityHeaderMiddleware — IPublishedUrlProvider returned null/whitespace/'#' for {ContentKey} {Culture}; skipping Link header",
                content.Key,
                culture);
            await next(context);
            return;
        }

        var alternateUrl = MarkdownAlternateUrl.Append(canonicalUrl);

        // Defensive sanitisation — RFC 8288 § 3 forbids `<` `>` inside the Link
        // URI-reference, and a CR/LF in any header value enables header
        // injection (Kestrel rejects but only after the request enters the
        // pipeline). Skip the write rather than risk a malformed header from
        // an adopter-supplied IPublishedUrlProvider returning a hostile string.
        if (alternateUrl.IndexOfAny(['\r', '\n', '<', '>']) >= 0)
        {
            _logger.LogWarning(
                "DiscoverabilityHeaderMiddleware — alternate URL contained CR/LF or '<>/' chars for {ContentKey}; suppressing Link header",
                content.Key);
            await next(context);
            return;
        }

        // Headers flushed via OnStarting + StatusCode < 300 guard so downstream
        // filters that rewrite the response (Umbraco content-fallback 404,
        // exception handlers) don't carry the Link header onto an error
        // response. HasStarted check inside the callback is belt-and-braces
        // for upstream middleware that may have committed earlier.
        context.Response.OnStarting(() =>
        {
            if (context.Response.HasStarted)
            {
                return Task.CompletedTask;
            }
            if (context.Response.StatusCode >= 300)
            {
                return Task.CompletedTask;
            }

            // Vary: Accept (idempotent — VaryHeaderHelper handles dedup; the
            // sibling AcceptHeaderNegotiationMiddleware also writes this on
            // every published-content response via OnStarting).
            VaryHeaderHelper.AppendAccept(context);

            // RFC 8288 § 3 Link grammar: `<uri>; rel="alternate"; type="text/markdown"`.
            var linkValue = $"<{alternateUrl}>; rel=\"alternate\"; type=\"text/markdown\"";
            context.Response.Headers.Append(Constants.HttpHeaders.Link, linkValue);

            return Task.CompletedTask;
        });

        await next(context);
    }
}
