using Umbraco.Community.AiVisibility.Extraction;
using Microsoft.AspNetCore.Http;

namespace Umbraco.Community.AiVisibility.Routing;

/// <summary>
/// Writes a successful <see cref="MarkdownExtractionResult"/> to
/// <see cref="HttpResponse"/> — sets <c>Cache-Control</c> / <c>Vary</c> /
/// <c>ETag</c> (always, even on 304s, per RFC 7232 § 4.1), honours
/// <c>If-None-Match</c> → 304, and on 200 emits <c>Content-Type</c> /
/// <c>X-Markdown-Tokens</c> and the Markdown body.
///
/// <para>
/// Single source of truth for the HTTP shape across the <c>.md</c> suffix route
/// (<see cref="Controllers.MarkdownController"/>) and the Accept-header
/// negotiation middleware (<see cref="AcceptHeaderNegotiationMiddleware"/>) —
/// Story 1.3 AC1 demands byte-identical bodies + headers across both surfaces.
/// </para>
///
/// <para>
/// Public-but-package-stable so a public controller's ctor can take it as a
/// dependency. Not advertised as a general-purpose adopter extension point —
/// same convention as <see cref="IMarkdownRouteResolver"/>.
/// </para>
/// </summary>
public interface IMarkdownResponseWriter
{
    /// <summary>
    /// Writes the response. Caller short-circuits the rest of the request
    /// pipeline after a successful return (the controller returns
    /// <c>EmptyResult</c>; the middleware skips <c>next()</c>).
    /// <para>
    /// Story 4.1 — <paramref name="contentSignal"/> is the resolved Cloudflare
    /// <c>Content-Signal</c> header value (per-doctype override → site-default →
    /// null). Resolved by the caller via <c>ContentSignalResolver.Resolve(...)</c>
    /// so the writer stays Singleton (no captive Scoped resolver dependency).
    /// Pass <c>null</c> to suppress the header entirely.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="result"/> is not <see cref="MarkdownExtractionStatus.Found"/>
    /// or its <see cref="MarkdownExtractionResult.Markdown"/> body is null/empty.
    /// Callers must handle <see cref="MarkdownExtractionStatus.Error"/> upstream.
    /// </exception>
    Task WriteAsync(
        MarkdownExtractionResult result,
        string canonicalPath,
        string? culture,
        string? contentSignal,
        HttpContext httpContext);

    /// <summary>
    /// Pre-Story-4.1 3-arg overload — kept as a default interface method so
    /// callers that have no Content-Signal context can call <c>WriteAsync</c>
    /// without explicitly passing <c>null</c>. Adopters who fully <i>replace</i>
    /// the writer must implement the 4-arg overload above; this shim does not
    /// remove that requirement (it is a consumer convenience, not an
    /// implementer compat layer).
    /// </summary>
    [Obsolete("Pass null for contentSignal explicitly via the 4-arg overload. This overload is removed in v1.0.")]
    Task WriteAsync(
        MarkdownExtractionResult result,
        string canonicalPath,
        string? culture,
        HttpContext httpContext)
        => WriteAsync(result, canonicalPath, culture, contentSignal: null, httpContext);
}
