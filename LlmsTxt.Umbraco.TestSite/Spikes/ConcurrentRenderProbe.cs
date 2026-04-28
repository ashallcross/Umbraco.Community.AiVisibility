namespace LlmsTxt.Umbraco.TestSite.Spikes;

/// <summary>
/// SPIKE-ONLY: fires N parallel in-process renders against mixed paths/cultures
/// and reports whether each output corresponds to its own input. AC5 — UmbracoContext
/// scope isolation under concurrency.
///
/// Per-job isolation strategy (locked into spike outcome as a Story 1.1
/// requirement): each parallel render runs inside its own AsyncScope so it
/// receives a fresh IUmbracoContextAccessor, AND each iteration suppresses
/// ExecutionContext flow so AsyncLocal state from the originating HTTP request
/// doesn't bleed into the worker thread. Either alone is insufficient — both
/// are required.
/// </summary>
public sealed class ConcurrentRenderProbe
{
    private readonly IServiceProvider _serviceProvider;

    public ConcurrentRenderProbe(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<ConcurrentProbeResult> RunAsync(
        HttpContext httpContext,
        IReadOnlyList<ConcurrentProbeJob> jobs,
        int maxParallelism,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentProbeJobResult[jobs.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, jobs.Count),
            options,
            async (i, ct) =>
            {
                // STORY 1.1 BINDING REQUIREMENT — every parallel render needs:
                //   (a) ExecutionContext.SuppressFlow() to sever AsyncLocal
                //       inheritance from the originating HTTP request, and
                //   (b) a fresh IServiceScope so scoped services
                //       (IUmbracoContextAccessor in particular) are new
                //       instances, not shared with the request scope.
                // SuppressFlow alone causes Umbraco IScope cross-disposal because
                // the AsyncLocal scope provider is a singleton; a fresh DI scope
                // alone still loses isolation because the parent's ambient scope
                // flows in. Both together produce clean per-job state.
                var afc = ExecutionContext.SuppressFlow();
                try
                {
                    await using var scope = _serviceProvider.CreateAsyncScope();
                    var renderer = scope.ServiceProvider.GetRequiredService<InProcessPageRenderer>();

                    var job = jobs[i];
                    var renderResult = await renderer.RenderAsync(httpContext, job.Path, job.Culture, ct);

                    // When no title was supplied for this job, treat the title-match
                    // assertion as not-applicable (true) so an off-by-one input on
                    // ?titles= doesn't flip AllOk to false and mask AC5's actual
                    // outcome. When a title IS supplied, an unrendered (null html)
                    // job is unambiguously a miss.
                    var ownTitleMatch = string.IsNullOrEmpty(job.ExpectedTitleSubstring)
                        ? true
                        : renderResult.Html is not null
                            && renderResult.Html.Contains(job.ExpectedTitleSubstring, StringComparison.OrdinalIgnoreCase);

                    results[i] = new ConcurrentProbeJobResult(
                        Index: i,
                        Path: job.Path,
                        Culture: job.Culture,
                        Status: renderResult.Status,
                        OwnTitleMatched: ownTitleMatch,
                        HtmlLength: renderResult.Html?.Length ?? 0,
                        RenderMs: renderResult.Diagnostics.RenderMs,
                        ContentKey: renderResult.Diagnostics.ContentKey,
                        ExceptionMessage: renderResult.ExceptionMessage);
                }
                finally
                {
                    // AsyncFlowControl.Undo() must be called on the same thread that
                    // captured the flow. Parallel.ForEachAsync continuations may resume
                    // on a different thread, so guard the call so a thread-mismatch
                    // throw doesn't replace the primary render exception.
                    try { afc.Undo(); } catch { /* swallow — primary exception (if any) wins */ }
                }
            });

        return new ConcurrentProbeResult(
            JobCount: jobs.Count,
            MaxParallelism: maxParallelism,
            AllOk: results.All(r => r.Status == "ok" && r.OwnTitleMatched),
            Jobs: results);
    }
}

public sealed record ConcurrentProbeJob(string Path, string? Culture, string ExpectedTitleSubstring);

public sealed record ConcurrentProbeJobResult(
    int Index,
    string Path,
    string? Culture,
    string Status,
    bool OwnTitleMatched,
    int HtmlLength,
    long RenderMs,
    Guid? ContentKey,
    string? ExceptionMessage);

public sealed record ConcurrentProbeResult(
    int JobCount,
    int MaxParallelism,
    bool AllOk,
    IReadOnlyList<ConcurrentProbeJobResult> Jobs);
