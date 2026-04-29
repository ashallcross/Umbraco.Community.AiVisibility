namespace LlmsTxt.Umbraco.Caching;

/// <summary>
/// Stable cache-key shapes for all LlmsTxt entries. Keys are inspectable,
/// human-readable, and lowercase-prefixed with <c>llms:</c> so
/// <see cref="Umbraco.Cms.Core.Cache.IAppCache.ClearByKey"/> can prefix-clear
/// the whole namespace on <c>RefreshAll</c>.
/// </summary>
public static class LlmsCacheKeys
{
    public const string Prefix = "llms:";
    public const string PagePrefix = "llms:page:";

    /// <summary>
    /// Per-page cache key. Culture is normalised to lowercase BCP-47;
    /// invariant content (no culture variation) keys with <c>"_"</c> — distinct
    /// from any real BCP-47 tag and stable across the <c>culture: null</c> path.
    /// </summary>
    public static string Page(Guid nodeKey, string? culture)
        => $"{PagePrefix}{nodeKey:N}:{NormaliseCulture(culture)}";

    /// <summary>
    /// Normalises a culture for cache-key composition: lowercases BCP-47 tags so
    /// <c>en-GB</c>/<c>en-gb</c>/<c>EN-GB</c> share an entry, and represents
    /// invariant content (null/empty culture) as <c>"_"</c> so it never collides
    /// with a real BCP-47 tag. Public so the ETag input on the controller layer can
    /// reuse the same normalisation — without it, a culture-casing mismatch between
    /// cache key and ETag input would defeat <c>If-None-Match</c> revalidation.
    /// </summary>
    public static string NormaliseCulture(string? culture)
        => string.IsNullOrEmpty(culture) ? "_" : culture.ToLowerInvariant();
}
