namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Metadata needed by the extraction pipeline's HTML-only seam — populated by the
/// public <see cref="DefaultMarkdownContentExtractor.ExtractAsync"/> path from
/// Umbraco's <c>IPublishedContent</c>, or hand-built by tests against captured fixtures.
/// </summary>
internal sealed record ContentMetadata(
    string Title,
    string AbsoluteUrl,
    DateTime UpdatedUtc,
    Guid ContentKey,
    string Culture);
