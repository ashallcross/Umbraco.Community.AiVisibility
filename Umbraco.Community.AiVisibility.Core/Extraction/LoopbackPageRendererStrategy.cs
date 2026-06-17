using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Story 7.2 — HTTP loopback strategy. Issues an inbound HTTP request
/// against the package's own host so Umbraco's full request pipeline
/// (controller hijacks, custom view models, route filters, model binders)
/// renders the page exactly as a real browser would. Compensates for the
/// Razor strategy's <see cref="RazorPageRendererStrategy"/> limitation that
/// fails on agency-built sites whose templates declare
/// <c>@inherits UmbracoViewPage&lt;TCustomViewModel&gt;</c> bound by a
/// <c>RenderController</c> hijack — the binder cannot convert the
/// in-process renderer's <see cref="IPublishedContent"/> argument to the
/// custom view model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime: Transient.</b> Matches the Story 7.1
/// <see cref="RazorPageRendererStrategy"/> precedent. The strategy is in
/// principle stateless and could be Singleton, but the captive-IServiceProvider
/// safety constraint pinned by <see cref="IPageRendererStrategy"/> means
/// strategies MUST be Singleton- or Transient-safe; Transient is the safest
/// default until a captive-dependency proof exists.
/// </para>
/// <para>
/// <b>Outbound Host header decoupling.</b> Multi-site Umbraco installs
/// resolve the request site-root + culture from the inbound <c>Host:</c>
/// header via <c>IDomainService</c>. If the loopback request hits
/// <c>127.0.0.1</c> with no <c>Host</c> override, Umbraco may resolve to
/// the wrong site or to the unbound root. This strategy therefore decouples
/// the TCP transport target (local Kestrel binding from
/// <see cref="ILoopbackUrlResolver"/>) from the HTTP <c>Host:</c> header
/// (the published-content authority from
/// <see cref="IPublishedUrlProvider.GetUrl(IPublishedContent, UrlMode, string?, Uri?)"/>).
/// When the published-URL provider returns a relative path (single-site dev
/// install with no <c>IDomain</c> bound), the strategy falls back to
/// <c>absoluteUri.Authority</c> so the loopback at least matches the
/// inbound request's host.
/// </para>
/// <para>
/// <b>Outbound Accept: text/html.</b> The Story 4.1 Accept-header
/// negotiation middleware reroutes inbound <c>Accept: text/markdown</c>
/// requests to the <c>.md</c> route. If the loopback inherited
/// <c>Accept: text/markdown</c> from the inbound request, the middleware
/// would route the loopback hit to the package's own <c>.md</c> controller
/// and trigger a recursion. This strategy explicitly clears the Accept
/// header and re-adds <c>text/html</c>. Defence in depth — Story 7.4 ships
/// the inbound-side <c>IRecursionGuard</c> that catches the case where
/// something else bypasses this primary mechanism.
/// </para>
/// <para>
/// <b>Outbound X-AiVisibility-Loopback marker.</b> The strategy sets
/// <c>X-AiVisibility-Loopback: 1</c> on every outbound request. Story 7.4's
/// inbound-side recursion guard reads the same constant
/// (<c>Constants.Http.LoopbackMarkerHeaderName</c>) — the marker present
/// AND <c>HttpContext.Connection.RemoteIpAddress</c> being a loopback
/// address together signal recursion. Spoof-resistance lives in the
/// inbound matcher (Story 7.4); the outbound strategy only writes the
/// marker.
/// </para>
/// <para>
/// <b>3xx responses</b> surface as render failures. The route resolver has
/// already produced the canonical URL form; a redirect at this point
/// indicates conflicting middleware (Umbraco Redirects rule, custom HTTPS
/// redirector, trailing-slash mismatch). Fail-loud surfaces the issue
/// rather than masking it. <c>AllowAutoRedirect = false</c> on the named
/// HttpClient ensures 3xx surfaces as the response status (not
/// auto-followed); the diagnostic carries the <c>Location</c> header value.
/// </para>
/// </remarks>
internal sealed class LoopbackPageRendererStrategy : IPageRendererStrategy
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoopbackUrlResolver _loopbackUrlResolver;
    private readonly IPublishedUrlProvider _publishedUrlProvider;
    private readonly ILogger<LoopbackPageRendererStrategy> _logger;

    public LoopbackPageRendererStrategy(
        IHttpClientFactory httpClientFactory,
        ILoopbackUrlResolver loopbackUrlResolver,
        IPublishedUrlProvider publishedUrlProvider,
        ILogger<LoopbackPageRendererStrategy> logger)
    {
        _httpClientFactory = httpClientFactory;
        _loopbackUrlResolver = loopbackUrlResolver;
        _publishedUrlProvider = publishedUrlProvider;
        _logger = logger;
    }

    public async Task<PageRenderResult> RenderAsync(
        IPublishedContent content,
        Uri absoluteUri,
        string? culture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Resolve the local TCP transport target. Lazy — first call may
        //    walk IServer.Features or parse the configured override.
        //    Environmental failures (no usable binding) bubble out as
        //    InvalidOperationException; do NOT wrap them into PageRenderResult.Failed
        //    because the failure is a misconfiguration, not a per-render issue.
        var target = _loopbackUrlResolver.Resolve();

        // 2. Resolve the published URL the loopback should impersonate.
        //    UrlMode.Absolute returns the host-bound URL when an Umbraco
        //    IDomain is configured for the matched root; falls back to a
        //    relative path for single-site dev installs without a domain
        //    binding.
        //
        //    Uri.TryCreate with UriKind.Absolute is fragile on Unix systems:
        //    a leading-slash path like "/about" parses successfully as a
        //    file:// URI on macOS/Linux (because `/about` is a valid absolute
        //    Unix path). We therefore additionally require the parsed scheme
        //    to be http(s) and the Authority to be non-empty before treating
        //    the result as a usable absolute URL.
        var publishedUrl = _publishedUrlProvider.GetUrl(content, UrlMode.Absolute, culture);
        Uri? publishedUri = null;
        if (!string.IsNullOrWhiteSpace(publishedUrl)
            && Uri.TryCreate(publishedUrl, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(parsed.Authority))
        {
            publishedUri = parsed;
        }

        var hostHeader = publishedUri is not null
            ? publishedUri.Authority
            : absoluteUri.Authority;

        var templateAlias = content.ContentType.Alias;

        // 3. Build the loopback URI: transport-target authority + published path.
        //    Use UriBuilder so the scheme/host/port come from the resolver
        //    (local binding) while the path/query come from the published URL
        //    (or the inbound absoluteUri when GetUrl returned relative).
        var pathSource = publishedUri ?? absoluteUri;
        var transportUri = new UriBuilder(target.TransportUri)
        {
            Path = pathSource.AbsolutePath,
            Query = pathSource.Query.TrimStart('?'),
        }.Uri;

        // 4. Build the request — Host header decoupled from transport target;
        //    Accept: text/html prevents Story 4.1 middleware from re-routing
        //    the loopback hit to .md; X-AiVisibility-Loopback: 1 is the
        //    Story 7.4 inbound-recursion-guard marker.
        using var request = new HttpRequestMessage(HttpMethod.Get, transportUri);

        // TryAddWithoutValidation, not the typed Headers.Host setter — the
        // typed setter validates against Uri.CheckHostName which silently
        // rejects values containing ":port". Multi-site Umbraco installs
        // commonly bind hosts on non-default ports (`localhost:8080`,
        // `sitea.example:5000`); preserving the port in the Host header is
        // load-bearing for IDomainService resolution. Using
        // TryAddWithoutValidation writes the value verbatim onto the wire.
        // CRLF/NUL injection is not a concern: hostHeader is sourced from a
        // typed Uri's Authority, and Uri.TryCreate rejects control characters
        // in URIs (both raw CRLF and percent-encoded %0D%0A) — plus modern
        // .NET's TryAddWithoutValidation itself rejects header values with
        // control characters.
        request.Headers.TryAddWithoutValidation("Host", hostHeader);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("text/html");
        request.Headers.TryAddWithoutValidation(
            Constants.Http.LoopbackMarkerHeaderName,
            "1");
        if (!string.IsNullOrWhiteSpace(culture))
        {
            request.Headers.AcceptLanguage.Clear();
            try
            {
                // ParseAdd throws FormatException on malformed culture values
                // (CRLF injection, invalid IETF tag shape). The strategy's
                // try/catch boundary starts at SendAsync below, so without an
                // inner guard a malformed culture would escape uncaught instead
                // of returning a clean PageRenderResult.Failed. Skip-and-continue
                // is preferred over Failed here because the inbound render is
                // still useful without Accept-Language.
                request.Headers.AcceptLanguage.ParseAdd(culture);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(
                    ex,
                    "PageRenderer: Loopback skipped malformed culture {Culture}; render proceeding without Accept-Language",
                    culture);
            }
        }

        // 5. Issue the request via the named client. The factory's contract
        //    means we must NOT dispose the returned HttpClient — that would
        //    dispose the pooled handler too.
        var client = _httpClientFactory.CreateClient(Constants.Http.LoopbackHttpClientName);

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);

            // 6a. 3xx → render failure with diagnostic naming the Location.
            //     AllowAutoRedirect=false on the named client keeps 3xx
            //     surfacing as the response status; the diagnostic surfaces
            //     conflicting-middleware misconfigurations (redirect rules,
            //     HTTPS redirector, trailing-slash mismatch).
            var statusCode = (int)response.StatusCode;
            if (statusCode >= 300 && statusCode < 400)
            {
                var location = response.Headers.Location?.ToString() ?? "(none)";
                _logger.LogWarning(
                    "PageRenderer: Loopback received non-success {Status} for {Path} — Location: {Location}",
                    statusCode,
                    transportUri.AbsolutePath,
                    location);
                return PageRenderResult.Failed(
                    new InvalidOperationException(
                        $"PageRenderer: loopback received HTTP {statusCode} for {transportUri.AbsolutePath} — Location: {location}. Route resolution already produced canonical form; investigate conflicting middleware (redirect rule, HTTPS redirector, trailing-slash mismatch)."),
                    content,
                    templateAlias,
                    culture);
            }

            // 6b. Other non-success → render failure with diagnostic.
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "PageRenderer: Loopback received non-success {Status} for {Path}",
                    statusCode,
                    transportUri.AbsolutePath);
                return PageRenderResult.Failed(
                    new InvalidOperationException(
                        $"PageRenderer: loopback received HTTP {statusCode} for {transportUri.AbsolutePath}."),
                    content,
                    templateAlias,
                    culture);
            }

            // 7. Read body, return Ok. ReadAsStringAsync(cancellationToken)
            //    honours cancellation through the body read.
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return PageRenderResult.Ok(html, content, templateAlias, culture);
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates without being wrapped — matches
            // RazorPageRendererStrategy convention.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "PageRenderer: Loopback render failed for {ContentKey} {Path}",
                content.Key,
                transportUri.AbsolutePath);
            return PageRenderResult.Failed(ex, content, templateAlias, culture);
        }
    }
}
