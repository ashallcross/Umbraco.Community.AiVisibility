namespace LlmsTxt.Umbraco.Extraction;

/// <summary>
/// Discriminated outcome of <see cref="IMarkdownContentExtractor.ExtractAsync"/>.
/// One of three states — <see cref="MarkdownExtractionStatus.Found"/> (success),
/// <see cref="MarkdownExtractionStatus.NotFound"/> (route resolved no content),
/// or <see cref="MarkdownExtractionStatus.Error"/> (render or extraction failed).
/// </summary>
public sealed record MarkdownExtractionResult
{
    private MarkdownExtractionResult(MarkdownExtractionStatus status)
    {
        Status = status;
    }

    public MarkdownExtractionStatus Status { get; }

    public string? Markdown { get; private init; }
    public Guid? ContentKey { get; private init; }
    public string? Culture { get; private init; }
    public DateTime? UpdatedUtc { get; private init; }
    public string? SourceUrl { get; private init; }

    public string? PathThatFailedToResolve { get; private init; }

    public Exception? Error { get; private init; }

    public static MarkdownExtractionResult Found(
        string markdown,
        Guid contentKey,
        string culture,
        DateTime updatedUtc,
        string sourceUrl)
        => new(MarkdownExtractionStatus.Found)
        {
            Markdown = markdown,
            ContentKey = contentKey,
            Culture = culture,
            UpdatedUtc = updatedUtc,
            SourceUrl = sourceUrl,
        };

    public static MarkdownExtractionResult NotFound(string path)
        => new(MarkdownExtractionStatus.NotFound)
        {
            PathThatFailedToResolve = path,
        };

    public static MarkdownExtractionResult Failed(
        Exception error,
        string? sourceUrl,
        Guid? contentKey)
        => new(MarkdownExtractionStatus.Error)
        {
            Error = error,
            SourceUrl = sourceUrl,
            ContentKey = contentKey,
        };
}

public enum MarkdownExtractionStatus
{
    Found,
    NotFound,
    Error,
}
