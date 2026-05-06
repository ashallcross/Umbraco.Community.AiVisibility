using Microsoft.AspNetCore.Http;

namespace Umbraco.Community.AiVisibility.Routing;

/// <summary>
/// Append-not-overwrite <c>Vary: Accept</c> emission, deduped on case-insensitive
/// match. Used by both <see cref="AcceptHeaderNegotiationMiddleware"/>'s
/// <c>OnStarting</c> callback (so non-divert HTML responses still emit
/// <c>Vary: Accept</c>) AND <see cref="MarkdownResponseWriter"/> on the divert
/// path — without a shared helper the writer's overwrite would destroy any
/// pre-existing <c>Vary</c> tokens (e.g. <c>Accept-Encoding</c> from
/// <c>ResponseCompression</c> middleware).
/// </summary>
internal static class VaryHeaderHelper
{
    public static void AppendAccept(HttpContext context)
    {
        var existing = context.Response.Headers[Constants.HttpHeaders.Vary].ToString();
        if (string.IsNullOrEmpty(existing))
        {
            context.Response.Headers[Constants.HttpHeaders.Vary] = "Accept";
            return;
        }
        var alreadyHasAccept = existing
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(t => string.Equals(t, "Accept", StringComparison.OrdinalIgnoreCase));
        if (!alreadyHasAccept)
        {
            context.Response.Headers[Constants.HttpHeaders.Vary] = existing + ", Accept";
        }
    }
}
