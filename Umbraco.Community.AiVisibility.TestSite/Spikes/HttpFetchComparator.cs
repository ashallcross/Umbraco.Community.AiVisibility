using System.Diagnostics;

namespace Umbraco.Community.AiVisibility.TestSite.Spikes;

/// <summary>
/// SPIKE-ONLY HTTP self-fetcher. Used as the BASELINE TRUTH for AC1 — the
/// in-process renderer's output must match this for the same path.
/// This is NOT the production rendering technique (architecture forbids HTTP
/// self-call); it is solely a control to prove the in-process chain produces
/// equivalent HTML.
/// </summary>
public sealed class HttpFetchComparator
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpFetchComparator> _logger;

    public HttpFetchComparator(IHttpClientFactory httpClientFactory, ILogger<HttpFetchComparator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<HttpFetchResult> FetchAsync(
        HttpContext httpContext,
        string path,
        string? culture,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var request = httpContext.Request;
            var scheme = request.Scheme;
            var host = request.Host.Value ?? "localhost";
            var normalizedPath = path.StartsWith('/') ? path : "/" + path;
            var absoluteUri = new Uri($"{scheme}://{host}{normalizedPath}");

            using var client = _httpClientFactory.CreateClient("Spike");
            client.Timeout = TimeSpan.FromSeconds(15);

            using var http = new HttpRequestMessage(HttpMethod.Get, absoluteUri);
            if (!string.IsNullOrWhiteSpace(culture))
            {
                http.Headers.AcceptLanguage.Clear();
                http.Headers.AcceptLanguage.ParseAdd(culture);
            }

            using var response = await client.SendAsync(http, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            sw.Stop();

            return new HttpFetchResult(
                Status: response.IsSuccessStatusCode ? "ok" : "http-error",
                StatusCode: (int)response.StatusCode,
                Html: body,
                FetchMs: sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Spike http fetch failed {Path}", path);
            return new HttpFetchResult(
                Status: "error",
                StatusCode: 0,
                Html: null,
                FetchMs: sw.ElapsedMilliseconds,
                ExceptionMessage: ex.Message);
        }
    }
}

public sealed record HttpFetchResult(
    string Status,
    int StatusCode,
    string? Html,
    long FetchMs,
    string? ExceptionMessage = null);
