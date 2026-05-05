namespace LlmsTxt.Umbraco.Caching;

/// <summary>
/// Story 2.3 — cache value for both manifest endpoints (<c>/llms.txt</c> and
/// <c>/llms-full.txt</c>). Pairs the manifest body with its content-derived ETag
/// so cache hits reuse the ETag without re-hashing on every request.
/// <para>
/// The cache key shape is unchanged from Story 2.1 / 2.2 (<c>llms:llmstxt:{host}:{culture}</c>
/// and <c>llms:llmsfull:{host}:{culture}</c>); only the cache <i>value</i> type
/// changed from <c>string</c> to this record.
/// </para>
/// <para>
/// Internal — adopters consuming <c>ILlmsTxtBuilder</c> / <c>ILlmsFullBuilder</c>
/// still see and return <c>string</c>; the cache wrapping happens inside the
/// controllers' cache-aside block.
/// </para>
/// </summary>
internal sealed record ManifestCacheEntry(string Body, string ETag);
