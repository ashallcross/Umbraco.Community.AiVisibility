using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Loopback handler for <c>LoopbackTransport: InMemory</c>: runs the request
/// through the site's own pipeline instead of the network.
/// </summary>
internal sealed class InMemoryLoopbackHandler : HttpMessageHandler
{
    private readonly InMemoryPipelineHolder _holder;
    private readonly IHttpContextFactory _httpContextFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public InMemoryLoopbackHandler(
        InMemoryPipelineHolder holder,
        IHttpContextFactory httpContextFactory,
        IHttpContextAccessor httpContextAccessor)
    {
        _holder = holder;
        _httpContextFactory = httpContextFactory;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var pipeline = _holder.Pipeline
            ?? throw new InvalidOperationException(
                "AiVisibility: the in-memory loopback transport has no captured request pipeline. "
                + "Set AiVisibility:RenderStrategy:LoopbackTransport to Network to use the network loopback instead.");
        var uri = request.RequestUri
            ?? throw new InvalidOperationException("AiVisibility: the loopback request has no RequestUri.");

        // No connection to read them from, so scheme and path base come from the visitor.
        var visitor = _httpContextAccessor.HttpContext?.Request;
        var scheme = visitor?.Scheme ?? uri.Scheme;
        var pathBase = visitor?.PathBase ?? PathString.Empty;

        // Otherwise creating the inner HttpContext clears the visitor's IHttpContextAccessor.
        Task<HttpResponseMessage> run;
        using (ExecutionContext.SuppressFlow())
        {
            run = Task.Run(() => RunAsync(pipeline, request, uri, scheme, pathBase, cancellationToken));
        }

        return await run.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> RunAsync(
        RequestDelegate pipeline,
        HttpRequestMessage request,
        Uri uri,
        string scheme,
        PathString pathBase,
        CancellationToken cancellationToken)
    {
        using var lifetime = new InMemoryRequestLifetimeFeature(cancellationToken);
        var responseFeature = new InMemoryResponseFeature();
        var responseBody = new MemoryStream();
        var bodyFeature = new StreamResponseBodyFeature(responseBody);

        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(CreateRequestFeature(request, uri, scheme, pathBase));
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(bodyFeature);
        features.Set<IHttpRequestLifetimeFeature>(lifetime);
        // Loopback IP so RecursionGuard recognises the request.
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            ConnectionId = Guid.NewGuid().ToString("N"),
            RemoteIpAddress = IPAddress.Loopback,
            LocalIpAddress = IPAddress.Loopback,
        });

        var context = _httpContextFactory.Create(features);
        try
        {
            try
            {
                await pipeline(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Cancelled inside the page (e.g. its own HttpClient timed out), not by
                // the caller. Report a failed request, as a network loopback's 500 would,
                // so one page can't cancel a whole /llms-full.txt build.
                throw new HttpRequestException(
                    $"AiVisibility: the in-memory loopback request for {uri.AbsolutePath} was cancelled inside the page.",
                    ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failure inside the page (e.g. its own HttpClient hitting a connection
                // error) is not a loopback reach failure; wrap it so its HttpRequestError
                // can't be mistaken for one.
                throw new HttpRequestException(
                    $"AiVisibility: the in-memory loopback request for {uri.AbsolutePath} failed inside the page.",
                    ex);
            }

            await responseFeature.FireOnStartingAsync().ConfigureAwait(false);
            await bodyFeature.CompleteAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return CreateResponseMessage(request, responseFeature, responseBody);
        }
        finally
        {
            await responseFeature.FireOnCompletedAsync().ConfigureAwait(false);
            _httpContextFactory.Dispose(context);
        }
    }

    private static HttpRequestFeature CreateRequestFeature(
        HttpRequestMessage request,
        Uri uri,
        string scheme,
        PathString pathBase)
    {
        var feature = new HttpRequestFeature
        {
            Protocol = "HTTP/1.1",
            Method = request.Method.Method,
            Scheme = scheme,
            RawTarget = uri.PathAndQuery,
            QueryString = uri.Query,
            Body = Stream.Null,
        };

        var fullPath = PathString.FromUriComponent(uri);
        if (pathBase.HasValue && fullPath.StartsWithSegments(pathBase, out var remainder))
        {
            feature.PathBase = pathBase.Value!;
            feature.Path = remainder.Value ?? string.Empty;
        }
        else
        {
            feature.PathBase = string.Empty;
            feature.Path = fullPath.Value ?? "/";
        }

        foreach (var header in request.Headers)
        {
            feature.Headers.Append(header.Key, header.Value.ToArray());
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                feature.Headers.Append(header.Key, header.Value.ToArray());
            }
        }

        if (!feature.Headers.ContainsKey(HeaderNames.Host))
        {
            feature.Headers.Host = uri.Authority;
        }

        return feature;
    }

    private static HttpResponseMessage CreateResponseMessage(
        HttpRequestMessage request,
        InMemoryResponseFeature responseFeature,
        MemoryStream responseBody)
    {
        var message = new HttpResponseMessage((HttpStatusCode)responseFeature.StatusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(responseBody.ToArray()),
        };

        if (!string.IsNullOrEmpty(responseFeature.ReasonPhrase))
        {
            message.ReasonPhrase = responseFeature.ReasonPhrase;
        }

        foreach (var header in responseFeature.Headers)
        {
            if (string.Equals(header.Key, HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = header.Value.ToArray();
            if (!message.Headers.TryAddWithoutValidation(header.Key, values))
            {
                message.Content.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }

        return message;
    }
}
