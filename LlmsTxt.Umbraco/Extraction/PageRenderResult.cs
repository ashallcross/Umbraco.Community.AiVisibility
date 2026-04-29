using Umbraco.Cms.Core.Models.PublishedContent;

namespace LlmsTxt.Umbraco.Extraction;

/// <summary>
/// Internal outcome of <see cref="PageRenderer.RenderAsync"/>. Promoted to
/// <see cref="MarkdownExtractionResult"/> by <see cref="DefaultMarkdownContentExtractor"/>.
/// </summary>
internal sealed record PageRenderResult
{
    private PageRenderResult(PageRenderStatus status)
    {
        Status = status;
    }

    public PageRenderStatus Status { get; }
    public string? Html { get; private init; }
    public IPublishedContent? Content { get; private init; }
    public string? TemplateAlias { get; private init; }
    public string? ResolvedCulture { get; private init; }
    public Exception? Error { get; private init; }

    public static PageRenderResult Ok(
        string html,
        IPublishedContent content,
        string? templateAlias,
        string? resolvedCulture)
        => new(PageRenderStatus.Ok)
        {
            Html = html,
            Content = content,
            TemplateAlias = templateAlias,
            ResolvedCulture = resolvedCulture,
        };

    public static PageRenderResult NotFound(string? resolvedCulture)
        => new(PageRenderStatus.NotFound)
        {
            ResolvedCulture = resolvedCulture,
        };

    public static PageRenderResult Failed(
        Exception error,
        IPublishedContent? content,
        string? templateAlias,
        string? resolvedCulture)
        => new(PageRenderStatus.Error)
        {
            Error = error,
            Content = content,
            TemplateAlias = templateAlias,
            ResolvedCulture = resolvedCulture,
        };
}

internal enum PageRenderStatus
{
    Ok,
    NotFound,
    Error,
}
