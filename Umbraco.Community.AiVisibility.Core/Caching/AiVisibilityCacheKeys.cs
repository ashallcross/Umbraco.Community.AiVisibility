namespace Umbraco.Community.AiVisibility.Caching;

/// <summary>
/// Stable cache-key shapes for all LlmsTxt entries. Keys are inspectable,
/// human-readable, and lowercase-prefixed with <c>aiv:</c> so
/// <see cref="Umbraco.Cms.Core.Cache.IAppCache.ClearByKey"/> can prefix-clear
/// the whole namespace on <c>RefreshAll</c>.
/// </summary>
public static class AiVisibilityCacheKeys
{
    public const string Prefix = "aiv:";
    public const string PagePrefix = "aiv:page:";

    /// <summary>
    /// Story 2.1 — <c>/llms.txt</c> manifest cache prefix. Architecture line 287
    /// pins the shape: <c>aiv:llmstxt:{hostname}:{culture}</c> (lowercase, no
    /// internal hyphen). Manifest invalidation in <c>ContentCacheRefresherHandler</c>
    /// uses prefix-clear (<c>aiv:llmstxt:{host}:</c>) to nuke all cultures for an
    /// affected hostname in one call.
    /// </summary>
    public const string LlmsTxtPrefix = "aiv:llmstxt:";

    /// <summary>
    /// Per-page cache key. <para>
    /// Shape: <c>aiv:page:{nodeKey:N}:{host}:{culture}</c>. Story 1.5 added
    /// <paramref name="host"/> to the key so multi-domain bindings against the
    /// same content node never collide on a CDN/proxy fronting both hosts.
    /// </para>
    /// <para>
    /// <paramref name="nodeKey"/> stays as the second segment (immediately after the
    /// <c>aiv:page:</c> prefix) so <see cref="ContentCacheRefresherHandler"/>'s
    /// race-mitigating prefix-clear (<c>aiv:page:{nodeKey:N}:</c>) still finds and
    /// clears every per-host entry for that node, regardless of how many hostnames
    /// have warmed entries against it.
    /// </para>
    /// <para>
    /// Culture is normalised to lowercase BCP-47; invariant content (no culture
    /// variation) keys with <c>"_"</c> — distinct from any real BCP-47 tag and stable
    /// across the <c>culture: null</c> path. Host is normalised similarly: lowercased,
    /// port stripped (matches the request-host shape <see cref="MarkdownResponseWriter"/>
    /// extracts from <c>HttpContext.Request.Host.Host</c>); a null/empty host
    /// (background scenarios where no <see cref="Microsoft.AspNetCore.Http.HttpContext"/>
    /// is ambient) falls back to <c>"_"</c>.
    /// </para>
    /// </summary>
    public static string Page(Guid nodeKey, string? host, string? culture)
        => $"{PagePrefix}{nodeKey:N}:{NormaliseHost(host)}:{NormaliseCulture(culture)}";

    /// <summary>
    /// <c>/llms.txt</c> manifest cache key. Shape:
    /// <c>aiv:llmstxt:{host}:{culture}</c>. Reuses <see cref="NormaliseHost"/>
    /// and <see cref="NormaliseCulture"/> so casing/port-stripping aligns with
    /// the per-page key shape — adopters inspecting cache contents see one set of
    /// rules, not two.
    /// <para>
    /// Pessimistic invalidation in <c>ContentCacheRefresherHandler</c> calls
    /// <c>IAppPolicyCache.ClearByKey(AiVisibilityCacheKeys.LlmsTxtHostPrefix(host))</c> to
    /// drop every culture entry for a hostname in one call (manifests are cheap
    /// to rebuild and any node change can change the manifest output).
    /// </para>
    /// </summary>
    public static string LlmsTxt(string? host, string? culture)
        => $"{LlmsTxtPrefix}{NormaliseHost(host)}:{NormaliseCulture(culture)}";

    /// <summary>
    /// Per-host prefix for invalidation. Shape: <c>aiv:llmstxt:{host}:</c>.
    /// Passing this to <c>IAppPolicyCache.ClearByKey</c> drops every culture's
    /// manifest entry for the given hostname.
    /// </summary>
    public static string LlmsTxtHostPrefix(string? host)
        => $"{LlmsTxtPrefix}{NormaliseHost(host)}:";

    /// <summary>
    /// Story 2.2 — <c>/llms-full.txt</c> manifest cache prefix. Architecture line
    /// 288 pins the shape: <c>aiv:llmsfull:{hostname}:{culture}</c> (lowercase, no
    /// internal hyphen — matches <see cref="LlmsTxtPrefix"/> convention; the
    /// hyphenated form in <c>package-spec.md</c> § 11 is stale spec drift,
    /// architecture wins). Manifest invalidation in
    /// <see cref="ContentCacheRefresherHandler"/> uses prefix-clear
    /// (<c>aiv:llmsfull:{host}:</c>) to nuke all cultures for an affected
    /// hostname in one call — same pattern as <see cref="LlmsTxtPrefix"/>.
    /// </summary>
    public const string LlmsFullPrefix = "aiv:llmsfull:";

    /// <summary>
    /// <c>/llms-full.txt</c> manifest cache key. Shape:
    /// <c>aiv:llmsfull:{host}:{culture}</c>. Reuses <see cref="NormaliseHost"/>
    /// and <see cref="NormaliseCulture"/> so casing/port-stripping aligns with
    /// the per-page key shape and the <c>/llms.txt</c> shape — adopters
    /// inspecting cache contents see one set of rules, not three.
    /// <para>
    /// Pessimistic invalidation in <c>ContentCacheRefresherHandler</c> calls
    /// <c>IAppPolicyCache.ClearByKey(AiVisibilityCacheKeys.LlmsFullHostPrefix(host))</c> to
    /// drop every culture entry for a hostname in one call (manifests are cheap
    /// to rebuild and any node change can change the manifest output).
    /// </para>
    /// </summary>
    public static string LlmsFull(string? host, string? culture)
        => $"{LlmsFullPrefix}{NormaliseHost(host)}:{NormaliseCulture(culture)}";

    /// <summary>
    /// Per-host prefix for <c>/llms-full.txt</c> invalidation. Shape:
    /// <c>aiv:llmsfull:{host}:</c>. Same shape as
    /// <see cref="LlmsTxtHostPrefix"/> applied to the <c>llms-full</c> namespace.
    /// </summary>
    public static string LlmsFullHostPrefix(string? host)
        => $"{LlmsFullPrefix}{NormaliseHost(host)}:";

    /// <summary>
    /// Story 3.1 — resolver settings cache prefix. Shape:
    /// <c>aiv:settings:{culture}</c>. Caches the per-culture overlay of the
    /// Settings doctype values onto the appsettings snapshot. There is one
    /// global Settings node per Umbraco install (the doctype is allowed at root
    /// and the resolver picks the first match), so the cache key omits host —
    /// the resolved snapshot is identical for every host and a host segment
    /// would just produce N duplicate cache entries. Cleared by
    /// <see cref="ContentCacheRefresherHandler"/> via
    /// <c>ClearByKey(SettingsPrefix)</c> on every refresh notification (AC5).
    /// </summary>
    public const string SettingsPrefix = "aiv:settings:";

    /// <summary>
    /// Resolver settings cache key. Shape: <c>aiv:settings:{culture}</c>.
    /// Host-independent (one global Settings node per install). Reuses
    /// <see cref="NormaliseCulture"/> so casing/invariant-sentinel handling
    /// aligns with the per-page and manifest key shapes.
    /// </summary>
    public static string Settings(string? culture)
        => $"{SettingsPrefix}{NormaliseCulture(culture)}";

    /// <summary>
    /// Story 4.2 — robots audit cache prefix. Shape:
    /// <c>aiv:robots:{hostname}</c>. The robots audit lives under a
    /// different invalidation regime than per-page / manifest caches:
    /// <see cref="Umbraco.Community.AiVisibility.Telemetry.RobotsAuditRefreshJob"/>
    /// rewrites the entry on the configured cadence; content-cache
    /// refresher notifications do NOT clear this prefix (audit results
    /// don't change when content publishes). The host is normalised
    /// via <see cref="NormaliseHost"/> for cross-key shape consistency.
    /// </summary>
    public const string RobotsPrefix = "aiv:robots:";

    /// <summary>
    /// Robots audit cache key. Shape: <c>aiv:robots:{hostname}</c>.
    /// Host-only (no culture) — robots.txt is a host-level directive and
    /// not language-variant-aware.
    /// </summary>
    public static string Robots(string? hostname)
        => $"{RobotsPrefix}{NormaliseHost(hostname)}";

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

    /// <summary>
    /// Normalises a host for cache-key composition: lowercases the host portion so
    /// <c>SiteA.Example</c> and <c>sitea.example</c> share an entry, strips an explicit
    /// port (so <c>sitea.example:443</c> and <c>sitea.example</c> share an entry — the
    /// request-pipeline reads <c>HttpContext.Request.Host.Host</c> which is already
    /// port-stripped, but the helper is public and may be called by future Epic 2
    /// manifest builders that pull host strings from <c>IDomain</c> entries that
    /// include port), and represents a missing host (null/empty/whitespace — background
    /// scenarios with no ambient <see cref="Microsoft.AspNetCore.Http.HttpContext"/> or
    /// malformed <c>Host</c> header) as <c>"_"</c> so it never collides with a real
    /// hostname. Public so <see cref="MarkdownResponseWriter"/> can reuse the same
    /// normalisation when building ETag input — alignment between cache key and ETag
    /// input is what keeps <c>If-None-Match</c> revalidation working across host casings.
    /// <para>
    /// Story 6.0b AC7 — IPv6 contract:
    /// <list type="bullet">
    /// <item><description>Bracketed IPv6 literal (<c>[::1]:443</c>, <c>[::1]</c>) — brackets preserved; trailing
    /// <c>:port</c> outside the closing bracket stripped. Per RFC 3986 § 3.2.2.</description></item>
    /// <item><description>Bare IPv6 literal without brackets (<c>::1</c>, <c>fe80::1</c>) — treated as host-only,
    /// no port to strip. Detected by the presence of more than one <c>:</c> AND no closing bracket.</description></item>
    /// <item><description>Unbracketed <c>hostname:port</c> — port stripped (single-colon shape).</description></item>
    /// </list>
    /// The <see cref="Microsoft.AspNetCore.Http.HostString.FromUriComponent"/> path was considered
    /// but NOT taken: it would introduce a <c>Microsoft.AspNetCore.Http.Abstractions</c> reference
    /// inside <c>Caching/</c>, breaking the folder dependency boundary documented in
    /// architecture.md (<c>Caching/</c> is HTTP-agnostic so adopters replacing the cache layer
    /// don't drag ASP.NET into their substitute).
    /// </para>
    /// </summary>
    public static string NormaliseHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "_";
        }

        // Bracketed IPv6 literal (RFC 3986 § 3.2.2). Preserve brackets, strip
        // any trailing :port outside the closing bracket. e.g.
        //   "[::1]:443" → "[::1]", "[::1]" → "[::1]".
        if (host.StartsWith('['))
        {
            var closeBracket = host.IndexOf(']', StringComparison.Ordinal);
            if (closeBracket > 0)
            {
                var bracketed = host[..(closeBracket + 1)];
                return bracketed.ToLowerInvariant();
            }

            // Malformed (no closing bracket) — fall through to the generic path
            // below. Collision risk is negligible because malformed hosts are
            // non-routable anyway.
        }

        var firstColon = host.IndexOf(':', StringComparison.Ordinal);
        if (firstColon < 0)
        {
            // No colon — bare hostname or single-segment IP.
            return host.ToLowerInvariant();
        }

        // Bare IPv6 literal without brackets (e.g. "::1", "fe80::1"): if there's
        // more than one colon AND no closing bracket, treat the whole string as
        // host (no port). RFC 7230 requires brackets for IPv6 in HTTP Host
        // headers, but defensive handling is cheap and matches what
        // HostString.FromUriComponent does.
        var lastColon = host.LastIndexOf(':');
        if (firstColon != lastColon)
        {
            return host.ToLowerInvariant();
        }

        // Single colon: standard hostname:port shape; strip the port.
        var hostOnly = host[..firstColon];
        return string.IsNullOrWhiteSpace(hostOnly) ? "_" : hostOnly.ToLowerInvariant();
    }
}
