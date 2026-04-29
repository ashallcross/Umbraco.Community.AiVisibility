using AngleSharp.Dom;
using Microsoft.Extensions.Logging;

namespace LlmsTxt.Umbraco.Extraction;

/// <summary>
/// Built-in selector chain (in priority order):
/// <list type="number">
/// <item><c>[data-llms-content]</c> — adopter-controlled boundary, the killer feature</item>
/// <item><c>&lt;main&gt;</c></item>
/// <item><c>&lt;article&gt;</c></item>
/// <item>each selector in <c>LlmsTxtSettings.MainContentSelectors</c> (Story 3.1 fills this)</item>
/// </list>
/// Returns null when nothing matches → caller falls through to SmartReader.
/// </summary>
internal sealed class DefaultContentRegionSelector : IContentRegionSelector
{
    private readonly ILogger<DefaultContentRegionSelector> _logger;

    public DefaultContentRegionSelector(ILogger<DefaultContentRegionSelector> logger)
    {
        _logger = logger;
    }

    public IElement? SelectRegion(IDocument document, IReadOnlyList<string> configuredSelectors)
    {
        var dataLlmsContentMatches = document.QuerySelectorAll("[data-llms-content]");
        if (dataLlmsContentMatches.Length > 0)
        {
            if (dataLlmsContentMatches.Length > 1)
            {
                _logger.LogWarning(
                    "Multiple [data-llms-content] elements found ({Count}); using first match",
                    dataLlmsContentMatches.Length);
            }
            return dataLlmsContentMatches[0];
        }

        var main = document.QuerySelector("main");
        if (main is not null)
        {
            return main;
        }

        var article = document.QuerySelector("article");
        if (article is not null)
        {
            return article;
        }

        foreach (var selector in configuredSelectors)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                continue;
            }

            try
            {
                var match = document.QuerySelector(selector);
                if (match is not null)
                {
                    return match;
                }
            }
            catch (Exception ex)
            {
                // Malformed adopter selector — log and skip rather than crashing the
                // extraction. SmartReader fallback still has a chance.
                _logger.LogWarning(
                    ex,
                    "Configured selector {Selector} threw during region match; skipping",
                    selector);
            }
        }

        return null;
    }
}
