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
/// lets the
/// <see cref="Caching.CachingMarkdownExtractorDecorator"/> compose a
/// <c>llms:page:{nodeKey}:{culture}</c> cache key without re-routing.
/// </para>
///
/// <para>
/// Adopters override by registering their own implementation in a composer that runs
/// after <see cref="Composers.RoutingComposer"/>; the caching composer detects adopter
/// overrides and skips the cache decorator wrap.
/// </para>
/// </summary>
public interface IMarkdownContentExtractor
{
    Task<MarkdownExtractionResult> ExtractAsync(
        IPublishedContent content,
        string? culture,
        CancellationToken cancellationToken);
}
