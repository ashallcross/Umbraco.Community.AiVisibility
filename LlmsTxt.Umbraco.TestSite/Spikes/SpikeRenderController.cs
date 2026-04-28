using Microsoft.AspNetCore.Mvc;

namespace LlmsTxt.Umbraco.TestSite.Spikes;

/// <summary>
/// SPIKE-ONLY HTTP harness for Story 0.A. Routes:
///   GET /spikes/render?path=&culture=&mode=in-process|http-fetch|both
///   GET /spikes/concurrency?paths=p1,p2,...&cultures=c1,c2,...&parallelism=4
///
/// This controller is wired into the TestSite ONLY, never into the package.
/// </summary>
[ApiController]
[Route("spikes")]
public sealed class SpikeRenderController : ControllerBase
{
    private readonly InProcessPageRenderer _inProcess;
    private readonly HttpFetchComparator _httpFetch;
    private readonly ConcurrentRenderProbe _concurrencyProbe;
    private readonly ILogger<SpikeRenderController> _logger;

    public SpikeRenderController(
        InProcessPageRenderer inProcess,
        HttpFetchComparator httpFetch,
        ConcurrentRenderProbe concurrencyProbe,
        ILogger<SpikeRenderController> logger)
    {
        _inProcess = inProcess;
        _httpFetch = httpFetch;
        _concurrencyProbe = concurrencyProbe;
        _logger = logger;
    }

    [HttpGet("render")]
    public async Task<IActionResult> Render(
        [FromQuery] string path,
        [FromQuery] string? culture,
        [FromQuery] string mode = "both",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { error = "path is required" });
        }

        if (mode is not ("in-process" or "http-fetch" or "both"))
        {
            return BadRequest(new { error = "mode must be one of: in-process, http-fetch, both" });
        }

        _logger.LogInformation("Spike render begin {Path} {Culture} {Mode}", path, culture, mode);

        InProcessRenderResult? inProcessResult = null;
        HttpFetchResult? httpResult = null;

        if (mode is "in-process" or "both")
        {
            inProcessResult = await _inProcess.RenderAsync(HttpContext, path, culture, cancellationToken);
        }

        if (mode is "http-fetch" or "both")
        {
            httpResult = await _httpFetch.FetchAsync(HttpContext, path, culture, cancellationToken);
        }

        SpikeDiff? diff = null;
        if (mode == "both" && inProcessResult is { Status: "ok", Html: not null } && httpResult is { Status: "ok", Html: not null })
        {
            diff = HtmlDiffer.Compare(inProcessResult.Html, httpResult.Html);
        }

        // Status surfacing — must distinguish single-mode results from mode=both
        // disagreements so the AC1 verdict isn't masked when one side fails.
        var status = (mode, inProcessResult, httpResult) switch
        {
            ("both", { Status: "ok" }, { Status: "ok" }) => "ok",
            ("both", { Status: "ok" }, _) => "mismatch",
            ("both", _, { Status: "ok" }) => "mismatch",
            ("both", { Status: "not-found" }, _) => "not-found",
            ("both", { Status: "error" }, _) => "error",
            ("in-process", { Status: var s }, _) => s,
            ("http-fetch", _, { Status: "ok" }) => "ok",
            ("http-fetch", _, { Status: var s }) => s,
            _ => "unknown"
        };

        var diagnostics = new SpikeDiagnostics(
            InProcessRenderMs: inProcessResult?.Diagnostics.RenderMs ?? 0,
            HttpFetchMs: httpResult?.FetchMs ?? 0,
            ContentKey: inProcessResult?.Diagnostics.ContentKey,
            ContentId: inProcessResult?.Diagnostics.ContentId,
            TemplateAlias: inProcessResult?.Diagnostics.TemplateAlias,
            ResolvedCulture: inProcessResult?.Diagnostics.ResolvedCulture,
            ExceptionType: inProcessResult?.ExceptionType,
            ExceptionMessage: inProcessResult?.ExceptionMessage ?? httpResult?.ExceptionMessage,
            ExceptionStack: inProcessResult?.ExceptionStack);

        var response = new SpikeRenderResponse(
            Mode: mode,
            Path: path,
            Culture: culture,
            Status: status,
            Html: inProcessResult?.Html,
            HttpHtml: httpResult?.Html,
            Diff: diff,
            Diagnostics: diagnostics);

        return Ok(response);
    }

    [HttpGet("concurrency")]
    public async Task<IActionResult> Concurrency(
        [FromQuery] string paths,
        [FromQuery] string? cultures = null,
        [FromQuery] string? titles = null,
        [FromQuery] int parallelism = 4,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(paths))
        {
            return BadRequest(new { error = "paths is required (comma-separated list)" });
        }

        if (parallelism is < 1 or > 16)
        {
            return BadRequest(new { error = "parallelism must be between 1 and 16" });
        }

        const StringSplitOptions splitOpts = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
        var pathList = paths.Split(',', splitOpts);
        var cultureList = (cultures ?? string.Empty).Split(',', splitOpts);
        var titleList = (titles ?? string.Empty).Split(',', splitOpts);

        if (pathList.Length == 0)
        {
            return BadRequest(new { error = "paths must contain at least one non-empty entry" });
        }

        if (cultureList.Length > pathList.Length)
        {
            return BadRequest(new { error = $"cultures has {cultureList.Length} entries but paths has only {pathList.Length}" });
        }

        if (titleList.Length > pathList.Length)
        {
            return BadRequest(new { error = $"titles has {titleList.Length} entries but paths has only {pathList.Length}" });
        }

        var jobs = new List<ConcurrentProbeJob>(pathList.Length);
        for (var i = 0; i < pathList.Length; i++)
        {
            var culture = i < cultureList.Length ? NullIfEmpty(cultureList[i]) : null;
            var title = i < titleList.Length ? titleList[i] : string.Empty;
            jobs.Add(new ConcurrentProbeJob(pathList[i], culture, title));
        }

        var result = await _concurrencyProbe.RunAsync(HttpContext, jobs, parallelism, cancellationToken);
        return Ok(result);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
