using AngleSharp.Dom;

namespace LlmsTxt.Umbraco.Extraction;

/// <summary>
/// Public extension point — selects the &quot;main content&quot; region from a parsed HTML
/// document. Adopters override only this seam to change region selection while keeping
/// the rest of the extraction pipeline (parse / strip / absolutify / convert / frontmatter).
/// </summary>
public interface IContentRegionSelector
{
    /// <summary>
    /// Returns the selected element, or <c>null</c> if none of the selectors matched —
    /// in which case the caller invokes the SmartReader fallback.
    /// </summary>
    IElement? SelectRegion(IDocument document, IReadOnlyList<string> configuredSelectors);
}
