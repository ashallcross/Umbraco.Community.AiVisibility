namespace LlmsTxt.Umbraco.Extraction;

/// <summary>
/// Public extension point — entire Markdown extraction pipeline (in-process Razor render +
/// HTML parse + region selection + strip + URL absolutification + ReverseMarkdown convert +
/// frontmatter prepend). Adopters override by registering their own implementation in a
/// composer that runs after <see cref="Composers.RoutingComposer"/>.
/// </summary>
public interface IMarkdownContentExtractor
{
    Task<MarkdownExtractionResult> ExtractAsync(Uri absoluteUri, CancellationToken cancellationToken);
}
