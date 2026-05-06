using AngleSharp.Dom;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Public extension point — selects the &quot;main content&quot; region from a parsed HTML
/// document. Adopters override only this seam to change region selection while keeping
/// the rest of the extraction pipeline (parse / strip / absolutify / convert / frontmatter).
///
/// <para>
/// This is the <b>light</b> override seam — adopters whose templates have an unusual
/// "main content" wrapper that the package's default chain does not catch
/// (<c>[data-llms-content]</c> → <c>&lt;main&gt;</c> → <c>&lt;article&gt;</c> →
/// <see cref="Configuration.AiVisibilitySettings.MainContentSelectors"/>) can replace just
/// this step. AngleSharp parse, strip-inside-region, URL absolutify, ReverseMarkdown
/// convert, and frontmatter prepend continue to run from the package default
/// <see cref="DefaultMarkdownContentExtractor"/>. Adopters who need to change those
/// pipeline stages should override <see cref="IMarkdownContentExtractor"/> instead.
/// </para>
///
/// <h3>DI registration discipline (AR17)</h3>
/// <para>
/// The package's default <see cref="DefaultContentRegionSelector"/> is registered via
/// <c>services.TryAddTransient</c> — adopters override by registering their own
/// implementation in their composer; no need to <c>services.Remove(...)</c> the
/// default first. Example:
/// </para>
/// <code>
/// [ComposeAfter(typeof(LlmsTxt.Umbraco.Composers.RoutingComposer))]
/// public sealed class AcmeRegionSelectorComposer : IComposer
/// {
///     public void Compose(IUmbracoBuilder builder) =>
///         builder.Services.AddTransient&lt;IContentRegionSelector, AcmeRegionSelector&gt;();
/// }
/// </code>
///
/// <h3>Lifetime guidance</h3>
/// <para>
/// Default lifetime: <b>Transient</b> — the default selector receives the parsed
/// <see cref="IDocument"/> and may walk its DOM during selection. Adopters may register
/// their own implementation as Singleton if it is stateless and thread-safe; the DI
/// container respects the adopter's lifetime declaration.
/// </para>
/// </summary>
public interface IContentRegionSelector
{
    /// <summary>
    /// Returns the selected element, or <c>null</c> if none of the selectors matched —
    /// in which case the caller invokes the SmartReader fallback.
    ///
    /// <para>
    /// Returning a deliberately empty element (e.g.
    /// <c>document.CreateElement("div")</c>) bypasses the SmartReader fallback and
    /// produces an empty Markdown body — useful for adopters who want explicit
    /// "no content" semantics on certain pages.
    /// </para>
    /// </summary>
    /// <param name="document">
    /// The parsed HTML document to select from.
    /// </param>
    /// <param name="configuredSelectors">
    /// CSS selector list from <see cref="Configuration.AiVisibilitySettings.MainContentSelectors"/>
    /// (sourced from <c>appsettings.json</c> under <c>LlmsTxt:MainContentSelectors</c>).
    /// Adopters' implementations may honour or ignore this list as they see fit;
    /// the package's default selector consults it after the built-in
    /// <c>data-llms-content</c> / <c>&lt;main&gt;</c> / <c>&lt;article&gt;</c> chain.
    /// </param>
    IElement? SelectRegion(IDocument document, IReadOnlyList<string> configuredSelectors);
}
