namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Story 4.1 — resolves the effective Cloudflare <c>Content-Signal</c> header
/// value for a given doctype alias. Per-doctype override wins; falls back to
/// <see cref="ContentSignalSettings.Default"/>; returns <c>null</c> when
/// neither is set.
/// <para>
/// Lookup against <see cref="ContentSignalSettings.PerDocTypeAlias"/> is
/// case-insensitive even when the bound dictionary's comparer is not.
/// <c>Microsoft.Extensions.Configuration</c>'s binder may strip the property
/// initialiser's <see cref="StringComparer.OrdinalIgnoreCase"/>; this helper
/// compensates with an explicit <see cref="StringComparison.OrdinalIgnoreCase"/>
/// scan rather than relying on the dictionary's comparer.
/// </para>
/// </summary>
internal static class ContentSignalResolver
{
    /// <summary>
    /// Resolves the effective Content-Signal value for the given doctype.
    /// Caller passes <c>IPublishedContent.ContentType.Alias</c> (or null for
    /// "no doctype context"). Returned value is trimmed; null/whitespace
    /// inputs short-circuit to <c>null</c>.
    /// </summary>
    public static string? Resolve(AiVisibilitySettings settings, string? doctypeAlias)
    {
        // Both AiVisibilitySettings.ContentSignal and ContentSignalSettings.PerDocTypeAlias
        // default to non-null instances, but `init`-set properties permit explicit null
        // from adopters constructing settings manually or from binder edge cases. Defend
        // against NRE here rather than crash the request pipeline.
        var contentSignal = settings?.ContentSignal;
        if (contentSignal is null)
        {
            return null;
        }

        var perDocType = contentSignal.PerDocTypeAlias;
        if (!string.IsNullOrWhiteSpace(doctypeAlias) && perDocType is not null)
        {
            foreach (var kvp in perDocType)
            {
                if (string.Equals(kvp.Key, doctypeAlias, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    return kvp.Value.Trim();
                }
            }
        }

        return string.IsNullOrWhiteSpace(contentSignal.Default)
            ? null
            : contentSignal.Default!.Trim();
    }
}
