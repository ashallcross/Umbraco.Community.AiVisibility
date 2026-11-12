using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Built-in <see cref="IExclusionEvaluator"/> consolidating the three exclusion
/// sources consulted across every LlmsTxt route surface: per-page
/// <c>excludeFromLlmExports</c> bool (plus any configured
/// <see cref="AiVisibilitySettings.ExclusionPropertyAliases"/>) → Umbraco
/// public-access protection → resolved doctype-alias exclusion list.
/// <para>
/// The public-access source uses
/// <see cref="IPublicAccessService.IsProtected(string)"/> against the
/// <see cref="IPublishedContent.Path"/> tree-path string — the same shape
/// Umbraco's own <c>PublicAccessFilter</c> middleware uses, avoiding a content
/// service round-trip per render.
/// </para>
/// <para>
/// Public so adopters can wrap-and-delegate via the DI Decorator pattern
/// (e.g. register their own <see cref="IExclusionEvaluator"/> that
/// constructor-injects this default and applies extra rules before/after
/// delegating). <c>sealed</c> keeps subclassing off the table — adopters
/// who need to alter behaviour compose, not inherit.
/// </para>
/// </summary>
public sealed class DefaultExclusionEvaluator : IExclusionEvaluator
{
    internal const string ExcludeFromLlmExportsAlias = "excludeFromLlmExports";

    private readonly ISettingsResolver _settingsResolver;
    private readonly IPublicAccessService _publicAccessService;
    private readonly IOptionsMonitor<AiVisibilitySettings>? _settings;
    private readonly ILogger<DefaultExclusionEvaluator> _logger;

    /// <summary>
    /// Constructs the evaluator without access to appsettings: configured
    /// <see cref="AiVisibilitySettings.ExclusionPropertyAliases"/> are ignored
    /// (only <c>excludeFromLlmExports</c> is read) and a throwing
    /// <see cref="ISettingsResolver"/> skips the doctype check. Kept for
    /// adopters who construct the default directly; DI uses the
    /// <see cref="IOptionsMonitor{TOptions}"/> overload.
    /// </summary>
    public DefaultExclusionEvaluator(
        ISettingsResolver settingsResolver,
        IPublicAccessService publicAccessService,
        ILogger<DefaultExclusionEvaluator> logger)
        : this(settingsResolver, publicAccessService, settings: null, logger)
    {
    }

    /// <summary>
    /// DI constructor — reads
    /// <see cref="AiVisibilitySettings.ExclusionPropertyAliases"/> and falls
    /// back to the appsettings <see cref="AiVisibilitySettings.ExcludedDoctypeAliases"/>
    /// when the <see cref="ISettingsResolver"/> throws.
    /// </summary>
    public DefaultExclusionEvaluator(
        ISettingsResolver settingsResolver,
        IPublicAccessService publicAccessService,
        IOptionsMonitor<AiVisibilitySettings>? settings,
        ILogger<DefaultExclusionEvaluator> logger)
    {
        _settingsResolver = settingsResolver;
        _publicAccessService = publicAccessService;
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> IsExcludedAsync(
        IPublishedContent content,
        string? culture,
        string? host,
        CancellationToken cancellationToken)
    {
        // Per-page bools — read regardless of resolver outcome. The package's
        // own excludeFromLlmExports is always honoured; configured
        // ExclusionPropertyAliases add to it. Pages whose doctype lacks a
        // property get null from GetProperty → "not excluded".
        var settingsSnapshot = _settings?.CurrentValue;
        if (IsFlagSet(content, ExcludeFromLlmExportsAlias, culture))
        {
            return true;
        }

        var configuredAliases = settingsSnapshot?.ExclusionPropertyAliases;
        if (configuredAliases is not null)
        {
            foreach (var rawAlias in configuredAliases)
            {
                if (string.IsNullOrWhiteSpace(rawAlias))
                {
                    continue;
                }

                if (IsFlagSet(content, rawAlias.Trim(), culture))
                {
                    return true;
                }
            }
        }

        // Umbraco public-access protection — checked after the per-page bool
        // (more explicit) but before the settings resolver (cheaper —
        // in-memory cache lookup against Umbraco's public-access entries).
        // Path-string overload mirrors Umbraco's own PublicAccessFilter
        // middleware; the IContent overload would force a DB round-trip per
        // render. Throws fail-open: a public-access cache glitch should not
        // 404 every page until it resolves — the latent issue is content
        // quality (login-redirect HTML rendered as Markdown), not security
        // (Umbraco's own middleware still gates the actual content render).
        // The Umbraco API returns Attempt<PublicAccessEntry>; .Success is true
        // when a matching entry exists for the path.
        bool isProtected;
        try
        {
            isProtected = _publicAccessService.IsProtected(content.Path).Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "IExclusionEvaluator — IPublicAccessService.IsProtected threw for {ContentKey} {Path}; treating as not-excluded (fail-open)",
                content.Key,
                content.Path);
            isProtected = false;
        }

        if (isProtected)
        {
            return true;
        }

        ResolvedLlmsSettings resolved;
        try
        {
            resolved = await _settingsResolver
                .ResolveAsync(host, culture, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "IExclusionEvaluator — ISettingsResolver threw for {Host} {Culture}; falling back to appsettings ExcludedDoctypeAliases",
                host,
                culture);
            return IsDoctypeExcluded(content, settingsSnapshot?.ExcludedDoctypeAliases);
        }

        return resolved.ExcludedDoctypeAliases
            .Contains(content.ContentType.Alias, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>true</c> only when the property exists and holds boolean
    /// <c>true</c>. Invariant properties are read with <c>culture: null</c> —
    /// passing the request culture to an invariant property makes Umbraco look
    /// for a non-existent culture variant and report no value. A property
    /// whose type varies by culture is read for <paramref name="culture"/>.
    /// </summary>
    private static bool IsFlagSet(IPublishedContent content, string alias, string? culture)
    {
        var prop = content.GetProperty(alias);
        if (prop is null)
        {
            return false;
        }

        var readCulture = prop.PropertyType is not null && prop.PropertyType.VariesByCulture()
            ? culture
            : null;
        if (!prop.HasValue(culture: readCulture))
        {
            return false;
        }

        return prop.GetValue(culture: readCulture) is bool b && b;
    }

    private static bool IsDoctypeExcluded(IPublishedContent content, IReadOnlyList<string>? aliases)
    {
        if (aliases is null || aliases.Count == 0)
        {
            return false;
        }

        var doctypeAlias = content.ContentType.Alias;
        foreach (var alias in aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias)
                && string.Equals(alias.Trim(), doctypeAlias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
