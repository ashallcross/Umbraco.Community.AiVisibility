using LlmsTxt.Umbraco.Extraction;
using Microsoft.AspNetCore.Http;

namespace LlmsTxt.Umbraco.Routing;

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
        HttpContext httpContext);
}
