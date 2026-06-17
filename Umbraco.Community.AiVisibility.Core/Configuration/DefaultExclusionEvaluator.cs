using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Built-in <see cref="IExclusionEvaluator"/> consolidating the three exclusion
/// sources consulted across every LlmsTxt route surface: per-page
/// <c>excludeFromLlmExports</c> bool → Umbraco public-access protection →
/// resolved doctype-alias exclusion list.
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
    private readonly ILogger<DefaultExclusionEvaluator> _logger;

    public DefaultExclusionEvaluator(
        ISettingsResolver settingsResolver,
        IPublicAccessService publicAccessService,
        ILogger<DefaultExclusionEvaluator> logger)
    {
        _settingsResolver = settingsResolver;
        _publicAccessService = publicAccessService;
        _logger = logger;
    }

    public async Task<bool> IsExcludedAsync(
        IPublishedContent content,
        string? culture,
        string? host,
        CancellationToken cancellationToken)
    {
        // Per-page bool — read regardless of resolver outcome. Pages whose
        // doctype doesn't include the composition return null from GetProperty,
        // which we treat as "not excluded".
        // The excludeFromLlmExports property lives on llmsTxtSettingsComposition
        // which is invariant. Pass culture: null — passing the request culture
        // causes Umbraco to look for a non-existent culture-variant and return
        // false even when the bool is set.
        _ = culture; // intentionally unused on the bool read path
        var prop = content.GetProperty(ExcludeFromLlmExportsAlias);
        if (prop is not null && prop.HasValue(culture: null))
        {
            var value = prop.GetValue(culture: null);
            if (value is bool b && b)
            {
                return true;
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
                "IExclusionEvaluator — ISettingsResolver threw for {Host} {Culture}; treating as not-excluded (fail-open)",
                host,
                culture);
            return false;
        }

        return resolved.ExcludedDoctypeAliases
            .Contains(content.ContentType.Alias, StringComparer.OrdinalIgnoreCase);
    }
}
