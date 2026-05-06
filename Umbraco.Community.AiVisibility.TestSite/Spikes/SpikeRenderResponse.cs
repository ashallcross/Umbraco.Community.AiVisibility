namespace Umbraco.Community.AiVisibility.TestSite.Spikes;

public sealed record SpikeRenderResponse(
    string Mode,
    string Path,
    string? Culture,
    string Status,
    string? Html,
    string? HttpHtml,
    SpikeDiff? Diff,
    SpikeDiagnostics Diagnostics);

public sealed record SpikeDiff(
    bool Identical,
    int InProcessLength,
    int HttpLength,
    string? FirstDifferenceContext);

public sealed record SpikeDiagnostics(
    long InProcessRenderMs,
    long HttpFetchMs,
    Guid? ContentKey,
    int? ContentId,
    string? TemplateAlias,
    string? ResolvedCulture,
    string? ExceptionType,
    string? ExceptionMessage,
    string? ExceptionStack);
