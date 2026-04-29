using Umbraco.Cms.Core.Models.PublishedContent;

namespace LlmsTxt.Umbraco.Extraction;

/// <summary>
/// Public extension point — full Markdown extraction pipeline (in-process Razor render +
/// HTML parse + region selection + strip + URL absolutification + ReverseMarkdown convert +
/// frontmatter prepend) for an already-resolved <see cref="IPublishedContent"/>.
///
/// <para>
/// Story 1.2 moved route resolution UP to <see cref="Controllers.MarkdownController"/>:
/// the controller resolves <see cref="IPublishedContent"/> via
/// <see cref="Umbraco.Cms.Core.Routing.IPublishedRouter"/>, returns 404 directly when the
/// route doesn't resolve, and only invokes the extractor on resolution success. This shape
/// lets the <see cref="Caching.CachingMarkdownExtractorDecorator"/> compose a
/// <c>llms:page:{nodeKey}:{culture}</c> cache key without re-routing.
/// </para>
///
/// <para>
/// This is the <b>heavy</b> override seam — replace the entire extraction pipeline
/// (HTML parser, Markdown converter, frontmatter shape, etc.). Adopters who only need
/// to change the content-region boundary (e.g. their templates have an unusual
/// "main content" wrapper not covered by <c>data-llms-content</c> / <c>&lt;main&gt;</c>
/// / <c>&lt;article&gt;</c> / <see cref="Configuration.LlmsTxtSettings.MainContentSelectors"/>)
/// should override the lighter <see cref="IContentRegionSelector"/> instead.
/// </para>
///
/// <h3>DI registration discipline (AR17)</h3>
/// <para>
/// The package's default <see cref="DefaultMarkdownContentExtractor"/> is registered via
/// <c>services.TryAddTransient</c> — adopters override by registering their own
/// implementation in their composer; no need to <c>services.Remove(...)</c> the
/// default first. Adopter override examples:
/// </para>
/// <code>
/// // Adopter composer — runs after RoutingComposer (preferred for clarity).
/// [ComposeAfter(typeof(LlmsTxt.Umbraco.Composers.RoutingComposer))]
/// public sealed class AcmeExtractorComposer : IComposer
/// {
///     public void Compose(IUmbracoBuilder builder) =>
///         builder.Services.AddTransient&lt;IMarkdownContentExtractor, AcmeExtractor&gt;();
/// }
/// </code>
/// <para>
/// <c>[ComposeAfter]</c> is recommended but not strictly required: when adopter composers
/// run before <see cref="Composers.RoutingComposer"/>, our <c>TryAddTransient</c> is a
/// no-op against the existing adopter registration — the adopter still wins.
/// </para>
///
/// <h3>Caching interaction</h3>
/// <para>
/// When an adopter overrides <see cref="IMarkdownContentExtractor"/>, the package's
/// <see cref="Caching.CachingMarkdownExtractorDecorator"/> is <b>not</b> wrapped around
/// the adopter implementation (see <see cref="Composers.CachingComposer.IsAdopterOverride"/>).
/// Adopters who want caching against their custom extractor must wrap our decorator
/// themselves. The bypass is logged once at startup as an Information-level entry
/// (<c>Adopter has overridden IMarkdownContentExtractor; skipping caching decorator wrap</c>).
/// </para>
/// <para>
/// <b>Composer ordering for the bypass log:</b> the adopter override always wins via
/// DI's last-registration-wins rule, regardless of composer order. The startup bypass
/// log only fires when the adopter's registration is in the service collection BEFORE
/// <see cref="Composers.CachingComposer"/> runs its detection. To guarantee the log
/// fires, decorate the adopter composer with
/// <c>[ComposeBefore(typeof(LlmsTxt.Umbraco.Composers.CachingComposer))]</c> in addition
/// to <c>[ComposeAfter(typeof(LlmsTxt.Umbraco.Composers.RoutingComposer))]</c>. Without
/// the <c>ComposeBefore</c>, override behaviour is unaffected — only the observability
/// log is.
/// </para>
///
/// <h3>Lifetime guidance</h3>
/// <para>
/// Default lifetime: <b>Transient</b> — the default extractor may hold AngleSharp DOM
/// state across an extraction call. Adopters may register their own implementation as
/// Singleton if it is stateless and thread-safe; the DI container respects the
/// adopter's lifetime declaration even when it differs from the package's default.
/// Document the choice in the adopter's composer.
/// </para>
/// </summary>
public interface IMarkdownContentExtractor
{
    Task<MarkdownExtractionResult> ExtractAsync(
        IPublishedContent content,
        string? culture,
        CancellationToken cancellationToken);
}
