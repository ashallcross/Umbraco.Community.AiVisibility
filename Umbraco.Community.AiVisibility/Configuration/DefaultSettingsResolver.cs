using System.Collections.Concurrent;
using System.Text;
using Umbraco.Community.AiVisibility.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Story 3.1 — built-in <see cref="ISettingsResolver"/>. Walks
/// <see cref="IDocumentNavigationQueryService.TryGetRootKeys"/> to find the
/// first <c>llmsSettings</c>-doctype root content node, reads its
/// <c>siteName</c> / <c>siteSummary</c> / <c>excludedDoctypeAliases</c>
/// properties, and overlays them onto the current
/// <see cref="AiVisibilitySettings"/> snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime: Scoped.</b> Architecture line 377 lists the resolver alongside
/// <see cref="Persistence.IRequestLog"/> as a scoped/transient seam (depends
/// on request-scoped <see cref="IUmbracoContextAccessor"/>). Singleton would
/// form a captive dependency at the root provider — pinned by
/// <c>SettingsComposerTests.Compose_StartupValidation_LlmsSettingsResolver_NoCaptiveDependency</c>.
/// </para>
/// <para>
/// <b>Cache layer:</b> <see cref="IAppPolicyCache"/> via
/// <see cref="AppCaches.RuntimeCache"/> at
/// <see cref="AiVisibilityCacheKeys.Settings"/>. Single-flight on cache miss is
/// provided by <c>GetCacheItem</c>'s factory-delegate serialisation per key
/// per instance (Story 1.2 / 2.3 contract).
/// </para>
/// <para>
/// <b>Property-access shape:</b> reads via
/// <see cref="IPublishedContent.GetProperty"/> +
/// <see cref="IPublishedProperty.GetValue"/> rather than the ambient
/// <c>page.Value&lt;T&gt;(alias, culture)</c> extension. The ambient overload
/// service-locates <see cref="IPublishedValueFallback"/> at static-init time
/// and NPEs in unit tests; the property-layer access avoids both that trap and
/// the <c>Umbraco.Extensions.PublishedContentExtensions</c> CS0433 ambiguity
/// (same shape <see cref="Builders.DefaultLlmsTxtBuilder"/> uses; see line
/// 191–194 of that file).
/// </para>
/// <para>
/// <b>Race trade-off:</b> a Settings-node edit that arrives mid-factory
/// returns the OLD value to callers parked on the factory delegate, which then
/// gets cached for the full TTL. Bounded staleness window equal to
/// <see cref="AiVisibilitySettings.SettingsResolverCachePolicySeconds"/>; same
/// shape as Stories 2.1 / 2.3 deferred D6.
/// </para>
/// </remarks>
internal sealed class DefaultSettingsResolver : ISettingsResolver
{
    private const string SettingsDoctypeAlias = "llmsSettings";
    private const string SiteNameAlias = "siteName";
    private const string SiteSummaryAlias = "siteSummary";
    private const string ExcludedAliasesAlias = "excludedDoctypeAliases";
    private const int SiteSummaryMaxChars = 500;
    private static readonly char[] AliasSeparators = { '\n', '\r', ',', ';' };

    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly IUmbracoContextAccessor _umbracoContextAccessor;
    private readonly IDocumentNavigationQueryService _navigation;
    private readonly AppCaches _appCaches;
    private readonly ILogger<DefaultSettingsResolver> _logger;

    // Process-wide first-seen guard so cache rebuilds after TTL expiry don't
    // re-flood the log on adopters with no Settings node configured. STATIC
    // because the resolver is registered Scoped (one instance per request);
    // an instance-field dictionary would reset on every request and the
    // "logged once per process" claim would degrade to "logged every request".
    // One entry per culture observed by this process — bounded by the adopter's
    // configured culture set, so growth is naturally constrained.
    // Test reset hook: <see cref="ResetForTestingDedupGuards"/>.
    private static readonly ConcurrentDictionary<string, bool> _missingNodeLogged = new();
    private static readonly ConcurrentDictionary<string, bool> _summaryTruncationLogged = new();

    /// <summary>
    /// Test-only reset of the process-wide log-once dedup state. Resolver tests
    /// that assert on log-once behaviour must call this from <c>[SetUp]</c>
    /// because the dedup state is static (production behaviour) and would
    /// otherwise leak across tests in the same fixture.
    /// </summary>
    internal static void ResetForTestingDedupGuards()
    {
        _missingNodeLogged.Clear();
        _summaryTruncationLogged.Clear();
    }

    public DefaultSettingsResolver(
        IOptionsMonitor<AiVisibilitySettings> settings,
        IUmbracoContextAccessor umbracoContextAccessor,
        IDocumentNavigationQueryService navigation,
        AppCaches appCaches,
        ILogger<DefaultSettingsResolver> logger)
    {
        _settings = settings;
        _umbracoContextAccessor = umbracoContextAccessor;
        _navigation = navigation;
        _appCaches = appCaches;
        _logger = logger;
    }

    public Task<ResolvedLlmsSettings> ResolveAsync(string? hostname, string? culture, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _settings.CurrentValue;
        var ttlSeconds = settings.SettingsResolverCachePolicySeconds;

        if (ttlSeconds < 0)
        {
            _logger.LogWarning(
                "SettingsResolverCachePolicySeconds {Value} is negative; treating as 0 (cache disabled)",
                ttlSeconds);
            ttlSeconds = 0;
        }

        // No cache when TTL=0 — re-walk every call.
        if (ttlSeconds == 0)
        {
            return Task.FromResult(BuildSnapshot(hostname, culture, settings));
        }

        // Cache key is host-independent — one global Settings node per install
        // (D1-A decision, code review 2026-04-30). The hostname parameter stays
        // on the public surface for adopter implementations that DO want
        // per-host overlays (TryAddScoped extension point per AC8).
        var cacheKey = AiVisibilityCacheKeys.Settings(culture);
        var resolved = _appCaches.RuntimeCache.GetCacheItem<ResolvedLlmsSettings>(
            cacheKey,
            () => BuildSnapshot(hostname, culture, settings),
            timeout: TimeSpan.FromSeconds(ttlSeconds));

        // GetCacheItem<T>'s factory delegate is non-null in our usage; the
        // returned value can only be null if a previous call cached null
        // (we never do). Defensive non-null guard for the static analyser.
        return Task.FromResult(resolved ?? BuildSnapshot(hostname, culture, settings));
    }

    /// <summary>
    /// Walk the published cache for the matching <c>llmsSettings</c> root
    /// content node and overlay its values onto the appsettings snapshot.
    /// Returns the appsettings snapshot verbatim when no Settings node exists
    /// or no <see cref="IUmbracoContext"/> is ambient.
    /// </summary>
    private ResolvedLlmsSettings BuildSnapshot(string? hostname, string? culture, AiVisibilitySettings settings)
    {
        var normalisedHost = AiVisibilityCacheKeys.NormaliseHost(hostname);
        var normalisedCulture = AiVisibilityCacheKeys.NormaliseCulture(culture);

        if (!_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext))
        {
            // Background scenario (no ambient HttpContext / UmbracoContext);
            // can't walk the published cache. Return appsettings verbatim.
            _logger.LogTrace(
                "ISettingsResolver — no ambient UmbracoContext for {Host} {Culture}; returning appsettings snapshot",
                normalisedHost,
                normalisedCulture);
            return BuildAppsettingsOnly(settings);
        }

        var publishedSnapshot = umbracoContext.Content;
        if (publishedSnapshot is null)
        {
            _logger.LogTrace(
                "ISettingsResolver — UmbracoContext.Content is null for {Host} {Culture}; returning appsettings snapshot",
                normalisedHost,
                normalisedCulture);
            return BuildAppsettingsOnly(settings);
        }

        if (!_navigation.TryGetRootKeys(out var rootKeys))
        {
            _logger.LogTrace(
                "ISettingsResolver — IDocumentNavigationQueryService.TryGetRootKeys returned false for {Host} {Culture}; returning appsettings snapshot",
                normalisedHost,
                normalisedCulture);
            return BuildAppsettingsOnly(settings);
        }

        IPublishedContent? settingsNode = null;
        foreach (var rootKey in rootKeys)
        {
            var node = publishedSnapshot.GetById(rootKey);
            if (node is not null
                && string.Equals(node.ContentType.Alias, SettingsDoctypeAlias, StringComparison.OrdinalIgnoreCase))
            {
                settingsNode = node;
                break;
            }
        }

        if (settingsNode is null)
        {
            // Information-once-per-culture, process-wide. Cache-rebuild after
            // TTL expiry must NOT re-log. Dedup keyed by culture only (one
            // global Settings node per install — D1-A); bounded by the
            // adopter's culture set. Adopters using uSync who set
            // SkipSettingsDoctype hit this path until they import their uSync
            // schema — bounded log volume keeps it observable without flooding.
            if (_missingNodeLogged.TryAdd(normalisedCulture, true))
            {
                _logger.LogInformation(
                    "ISettingsResolver — no llmsSettings root node found for {Culture}; using appsettings values (logged once per process per culture)",
                    normalisedCulture);
            }
            return BuildAppsettingsOnly(settings);
        }

        // The llmsSettings doctype is invariant (Variations: Nothing) — reads
        // MUST pass culture: null. Passing the request culture causes
        // IPublishedProperty.HasValue("en-gb") to return false on invariant
        // properties (Umbraco treats it as "no culture-variant value found"),
        // which silently zeroes out the resolver overlay. Surfaced at Story 3.1
        // manual gate Step 4. Captured as Spec Drift Note for retro.
        var doctypeSiteName = ReadStringProperty(settingsNode, SiteNameAlias);
        var doctypeSiteSummary = ReadStringProperty(settingsNode, SiteSummaryAlias);
        var doctypeExcluded = ReadStringProperty(settingsNode, ExcludedAliasesAlias);

        // Per-field fallback: empty/whitespace doctype value → use appsettings.
        var resolvedSiteName = !string.IsNullOrWhiteSpace(doctypeSiteName)
            ? SanitiseHeaderLine(doctypeSiteName!)
            : settings.SiteName;
        var resolvedSiteSummary = !string.IsNullOrWhiteSpace(doctypeSiteSummary)
            ? TruncateSummary(doctypeSiteSummary!, normalisedCulture)
            : settings.SiteSummary;

        // Union: appsettings list ∪ doctype list (case-insensitive).
        var excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in settings.ExcludedDoctypeAliases ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                excludedSet.Add(alias.Trim());
            }
        }
        if (!string.IsNullOrWhiteSpace(doctypeExcluded))
        {
            foreach (var token in doctypeExcluded!.Split(AliasSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                excludedSet.Add(token);
            }
        }

        return new ResolvedLlmsSettings(
            SiteName: resolvedSiteName,
            SiteSummary: resolvedSiteSummary,
            ExcludedDoctypeAliases: excludedSet,
            BaseSettings: settings);
    }

    /// <summary>
    /// Build a resolved record carrying only the appsettings values — used on
    /// every fallback path (no UmbracoContext, no settings node, etc.).
    /// </summary>
    private static ResolvedLlmsSettings BuildAppsettingsOnly(AiVisibilitySettings settings)
    {
        var excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in settings.ExcludedDoctypeAliases ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                excludedSet.Add(alias.Trim());
            }
        }

        return new ResolvedLlmsSettings(
            SiteName: settings.SiteName,
            SiteSummary: settings.SiteSummary,
            ExcludedDoctypeAliases: excludedSet,
            BaseSettings: settings);
    }

    /// <summary>
    /// Read a string property from the Settings node via the property layer
    /// (<see cref="IPublishedContent.GetProperty"/> +
    /// <see cref="IPublishedProperty.GetValue"/>) so unit tests don't depend
    /// on Umbraco's <c>StaticServiceProvider</c>-resolved
    /// <see cref="IPublishedValueFallback"/>. Same trap
    /// <see cref="Builders.DefaultLlmsTxtBuilder.ResolveSummaryAsync"/>
    /// already documents.
    /// </summary>
    private static string? ReadStringProperty(IPublishedContent settingsNode, string alias)
    {
        // Invariant doctype — pass null culture so Umbraco's HasValue/GetValue
        // looks up the invariant value, not a non-existent culture-variant.
        var prop = settingsNode.GetProperty(alias);
        if (prop is null || !prop.HasValue(culture: null))
        {
            return null;
        }
        return prop.GetValue(culture: null)?.ToString();
    }

    /// <summary>
    /// Truncate a Settings-doctype summary at <see cref="SiteSummaryMaxChars"/>
    /// (word boundary, ellipsis appended). Logs <c>Warning</c> once per culture
    /// on truncation. Newlines collapse to spaces via
    /// <see cref="CollapseWhitespace"/> first so a multi-line summary doesn't
    /// break the manifest blockquote shape.
    /// </summary>
    /// <remarks>
    /// The slice budget is <c>SiteSummaryMaxChars - 1</c> to leave room for the
    /// trailing ellipsis (U+2026, single BMP char). Output is guaranteed
    /// <c>≤ SiteSummaryMaxChars</c> chars total — pinned by
    /// <c>ResolveAsync_SettingsNodeWithSiteSummary501Chars_OutputCappedAt500</c>.
    /// </remarks>
    private string TruncateSummary(string raw, string normalisedCulture)
    {
        var collapsed = CollapseWhitespace(raw);
        if (collapsed.Length <= SiteSummaryMaxChars)
        {
            return collapsed;
        }

        if (_summaryTruncationLogged.TryAdd(normalisedCulture, true))
        {
            _logger.LogWarning(
                "ISettingsResolver — siteSummary for {Culture} exceeds {Max} chars; truncating (logged once per process per culture)",
                normalisedCulture,
                SiteSummaryMaxChars);
        }

        // Slice budget = max - 1 so the appended ellipsis stays within the cap.
        const int ellipsisLength = 1;
        var sliceBudget = SiteSummaryMaxChars - ellipsisLength;

        // Walk back from the budget until the previous char is whitespace OR
        // start of string. Guarantees we don't break a word in half.
        var cutoff = sliceBudget;
        while (cutoff > 0 && !char.IsWhiteSpace(collapsed[cutoff]))
        {
            cutoff--;
        }
        if (cutoff == 0)
        {
            // First sliceBudget chars are a single word — hard-cut at budget.
            cutoff = sliceBudget;
        }
        return collapsed[..cutoff].TrimEnd() + "…";
    }

    /// <summary>
    /// Collapse all whitespace runs to a single space, mirroring
    /// <see cref="Builders.DefaultLlmsTxtBuilder.CollapseWhitespace"/>. Pure
    /// duplication is acceptable here — that helper lives inside the builder
    /// (<c>Builders/</c> folder) and the <c>Configuration/</c> resolver can't
    /// take a dependency on a builder helper without leaking the boundary.
    /// </summary>
    private static string CollapseWhitespace(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var lastWasWhitespace = false;
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasWhitespace && sb.Length > 0)
                {
                    sb.Append(' ');
                }
                lastWasWhitespace = true;
            }
            else
            {
                sb.Append(c);
                lastWasWhitespace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Replace CR/LF in a single-line value (siteName) with single spaces,
    /// mirroring <see cref="Builders.DefaultLlmsTxtBuilder.SanitiseHeaderLine"/>
    /// so a multi-line setting doesn't break the manifest H1/blockquote.
    /// </summary>
    private static string SanitiseHeaderLine(string value)
    {
        if (value.IndexOfAny(new[] { '\r', '\n' }) < 0) return value;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c is '\r' or '\n' ? ' ' : c);
        }
        return sb.ToString();
    }
}
