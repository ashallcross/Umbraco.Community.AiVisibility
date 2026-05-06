using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Story 4.1 — built-in <see cref="IExclusionEvaluator"/> lifting the
/// per-page-bool-then-resolver-throw-fail-open shape that previously lived
/// independently in <c>MarkdownController.IsExcludedAsync</c> +
/// <c>AcceptHeaderNegotiationMiddleware.IsExcludedAsync</c>.
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
    private readonly ILogger<DefaultExclusionEvaluator> _logger;

    public DefaultExclusionEvaluator(
        ISettingsResolver settingsResolver,
        ILogger<DefaultExclusionEvaluator> logger)
    {
        _settingsResolver = settingsResolver;
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
        // false even when the bool is set (Story 3.1 manual gate Step 4).
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
