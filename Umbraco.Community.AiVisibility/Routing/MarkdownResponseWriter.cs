using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Umbraco.Community.AiVisibility.Caching;
using Umbraco.Community.AiVisibility.Configuration;
using LlmsTxt.Umbraco.Extraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace LlmsTxt.Umbraco.Routing;

/// <summary>
/// Default <see cref="IMarkdownResponseWriter"/>. Stateless — registered as
/// <c>TryAddSingleton</c>; reads <see cref="IOptionsMonitor{TOptions}"/> for
/// live cache-policy updates without recreating the writer.
/// </summary>
internal sealed class MarkdownResponseWriter : IMarkdownResponseWriter
{
    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;

    public MarkdownResponseWriter(IOptionsMonitor<AiVisibilitySettings> settings)
    {
        _settings = settings;
    }

    public async Task WriteAsync(
        MarkdownExtractionResult result,
        string canonicalPath,
        string? culture,
        string? contentSignal,
        HttpContext httpContext)
    {
        if (result.Status != MarkdownExtractionStatus.Found || string.IsNullOrEmpty(result.Markdown))
        {
            throw new InvalidOperationException(
                "MarkdownResponseWriter.WriteAsync requires a Found result with a non-empty Markdown body. "
                + "Callers must handle Error / empty-body cases upstream.");
        }

        var markdown = result.Markdown!;
        // Normalise the canonical path through a shared helper so the `.md`
        // controller and the Accept-negotiation middleware compute identical
        // ETag inputs for the same logical resource regardless of percent-
        // encoding or trailing-slash variants the client used.
        var normalisedPath = LlmsCanonicalPath.Normalise(canonicalPath);
        // Story 1.5: include request host in ETag input so multi-domain
        // bindings on the same node produce distinct ETags. Without this, a
        // CDN fronting both hosts could serve siteA's body to siteB clients
        // on a 304 revalidation. Host shape mirrors the cache-key normalisation
        // (lowercase, port stripped via Request.Host.Host) so cache hit / ETag
        // alignment is preserved across the controller and the writer.
        var host = httpContext.Request.Host.HasValue ? httpContext.Request.Host.Host : null;
        var etag = ComputeETag(host, normalisedPath, culture, result.UpdatedUtc ?? DateTime.UnixEpoch);

        var response = httpContext.Response;
        var headers = response.Headers;

        // Always emit Vary, Cache-Control, ETag — even on 304 (RFC 7232 § 4.1
        // "MUST send a Cache-Control, Content-Location, Date, ETag, Expires,
        // and Vary header field that would have been sent in a 200 response").
        // Vary uses the shared append-not-overwrite helper so adopters' upstream
        // Vary tokens (Accept-Encoding from ResponseCompression, etc.) survive.
        VaryHeaderHelper.AppendAccept(httpContext);
        headers[Constants.HttpHeaders.CacheControl] = BuildCacheControl();
        headers[Constants.HttpHeaders.ETag] = etag;

        // Story 4.1 — Cloudflare Content-Signal header. Emitted on BOTH 200 and
        // 304 paths because RFC 7232 § 4.1 requires representation-metadata
        // headers that would have been on the 200 to also appear on the 304.
        // Distinct from X-Markdown-Tokens (encoded-body-derived; misleading on
        // 304) — Content-Signal is a policy declaration that travels with the
        // resource regardless of representation freshness.
        if (!string.IsNullOrWhiteSpace(contentSignal)
            && contentSignal!.IndexOfAny(['\r', '\n']) < 0)
        {
            headers[Constants.HttpHeaders.ContentSignal] = contentSignal.Trim();
        }

        if (IfNoneMatchMatcher.Matches(httpContext.Request, etag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            // Defensive: ensure no upstream-set Content-Type leaks into the 304.
            // RFC 7232 § 4.1 says representation metadata that wouldn't be sent
            // in the 200 response (e.g. Content-Type, X-Markdown-Tokens) MUST NOT
            // be sent on the 304 either.
            response.ContentType = null;
            return;
        }

        headers[Constants.HttpHeaders.XMarkdownTokens] =
            EstimateTokenCount(markdown).ToString(CultureInfo.InvariantCulture);
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = Constants.HttpHeaders.MarkdownContentType;

        await response.WriteAsync(markdown, Encoding.UTF8, httpContext.RequestAborted);
    }

    private string BuildCacheControl()
    {
        var seconds = Math.Max(0, _settings.CurrentValue.CachePolicySeconds);
        return $"public, max-age={seconds.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Stable ETag derived from <c>(host + route + normalised-culture + contentVersion)</c>.
    /// Reuses <see cref="AiVisibilityCacheKeys.NormaliseHost"/> and
    /// <see cref="AiVisibilityCacheKeys.NormaliseCulture"/> so the ETag input shares casing with the
    /// cache key — without alignment, a follow-up request whose culture or host casing
    /// differs (e.g. <c>en-GB</c> vs <c>en-gb</c>, <c>SiteA.Example</c> vs
    /// <c>sitea.example</c>) would compute a different ETag against the same cached body
    /// and break <c>If-None-Match</c> revalidation.
    /// </summary>
    private static string ComputeETag(string? host, string canonicalPath, string? culture, DateTime updatedUtc)
    {
        var input = string.Concat(
            AiVisibilityCacheKeys.NormaliseHost(host),
            "|",
            canonicalPath,
            "|",
            AiVisibilityCacheKeys.NormaliseCulture(culture),
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

    /// <summary>
    /// Cloudflare-convention <c>X-Markdown-Tokens</c> header value — character-based
    /// estimator (<c>length / 4</c>, rounded). Full BPE tokenisation is out of scope
    /// for v1; the estimate is informational.
    /// </summary>
    private static int EstimateTokenCount(string markdown)
        => Math.Max(1, (int)Math.Round(markdown.Length / 4.0, MidpointRounding.AwayFromZero));
}
