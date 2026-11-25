using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Community.AiVisibility.Extraction;
using Umbraco.Community.AiVisibility.Routing;

namespace Umbraco.Community.AiVisibility.Tests.Extraction;

/// <summary>
/// Runs the handler with a real <see cref="DefaultHttpContextFactory"/> and <see cref="HttpContextAccessor"/>.
/// </summary>
[TestFixture]
public class InMemoryLoopbackHandlerTests
{
    [Test]
    public async Task Send_RunsPipelineWithMethodHostPathAndQuery_AndReturnsStatusAndBody()
    {
        string? seen = null;
        using var harness = Harness.Create(async ctx =>
        {
            seen = $"{ctx.Request.Method} {ctx.Request.Host} {ctx.Request.Path}{ctx.Request.QueryString}";
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("<h1>People</h1>");
        });

        using var response = await harness.SendAsync(Get("http://localhost/people/?page=2", host: "www.example.com"));

        Assert.Multiple(async () =>
        {
            Assert.That(seen, Is.EqualTo("GET www.example.com /people/?page=2"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("<h1>People</h1>"));
        });
    }

    [Test]
    public async Task Send_CopiesRequestHeaders()
    {
        string? accept = null;
        string? language = null;
        using var harness = Harness.Create(ctx =>
        {
            accept = ctx.Request.Headers.Accept;
            language = ctx.Request.Headers.AcceptLanguage;
            return Task.CompletedTask;
        });
        var request = Get("http://localhost/people/");
        request.Headers.Accept.ParseAdd("text/html");
        request.Headers.AcceptLanguage.ParseAdd("en-GB");

        using var _ = await harness.SendAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(accept, Is.EqualTo("text/html"));
            Assert.That(language, Is.EqualTo("en-GB"));
        });
    }

    [Test]
    public async Task Send_MapsRedirectStatusAndLocationHeader()
    {
        using var harness = Harness.Create(ctx =>
        {
            ctx.Response.Redirect("https://www.example.com/people/");
            return Task.CompletedTask;
        });

        using var response = await harness.SendAsync(Get("http://localhost/people"));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(response.Headers.Location, Is.EqualTo(new Uri("https://www.example.com/people/")));
        });
    }

    [Test]
    public async Task Send_UsesVisitorScheme_WhenAVisitorRequestIsCurrent()
    {
        string? scheme = null;
        using var harness = Harness.Create(ctx =>
        {
            scheme = ctx.Request.Scheme;
            return Task.CompletedTask;
        });
        harness.Accessor.HttpContext = Visitor(scheme: "https");

        using var _ = await harness.SendAsync(Get("http://localhost/people/"));

        Assert.That(scheme, Is.EqualTo("https"));
    }

    [Test]
    public async Task Send_SplitsTheVisitorPathBaseFromThePath()
    {
        string? seen = null;
        using var harness = Harness.Create(ctx =>
        {
            seen = $"{ctx.Request.PathBase}|{ctx.Request.Path}";
            return Task.CompletedTask;
        });
        harness.Accessor.HttpContext = Visitor(scheme: "https", pathBase: "/sub");

        using var _ = await harness.SendAsync(Get("http://localhost/sub/people/"));

        Assert.That(seen, Is.EqualTo("/sub|/people/"));
    }

    [Test]
    public void Send_PageCancelledItsOwnWork_SurfacesAsAFailedRequestNotACancellation()
    {
        using var harness = Harness.Create(_ => throw new TaskCanceledException("the page's own HttpClient timed out"));

        Assert.ThrowsAsync<HttpRequestException>(async () => await harness.SendAsync(Get("http://localhost/people/")));
    }

    [Test]
    public void Send_PageThrows_SurfacesAsWrappedRequestExceptionWithUnknownError()
    {
        var pageFailure = new HttpRequestException(HttpRequestError.ConnectionError, "the page's own HttpClient could not connect");
        using var harness = Harness.Create(_ => throw pageFailure);

        var ex = Assert.ThrowsAsync<HttpRequestException>(async () => await harness.SendAsync(Get("http://localhost/people/")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.HttpRequestError, Is.EqualTo(HttpRequestError.Unknown));
            Assert.That(ex.InnerException, Is.SameAs(pageFailure));
        });
    }

    [Test]
    public async Task Send_MarkedRequestIsDetectedByRecursionGuard()
    {
        bool? isRecursion = null;
        using var harness = Harness.Create(ctx =>
        {
            isRecursion = new RecursionGuard().IsRecursion(ctx);
            return Task.CompletedTask;
        });
        var request = Get("http://localhost/people/");
        request.Headers.TryAddWithoutValidation(Constants.Http.LoopbackMarkerHeaderName, "1");

        using var _ = await harness.SendAsync(request);

        Assert.That(isRecursion, Is.True);
    }

    [Test]
    public async Task Send_LeavesOuterRequestHttpContextIntact()
    {
        using var harness = Harness.Create(_ => Task.CompletedTask);
        var outer = Visitor(scheme: "https");
        harness.Accessor.HttpContext = outer;

        using var _ = await harness.SendAsync(Get("http://localhost/people/"));

        Assert.That(harness.Accessor.HttpContext, Is.SameAs(outer));
    }

    [Test]
    public async Task Send_DisposesInnerRequestServices_AfterTheResponse()
    {
        ScopedProbe? probe = null;
        using var harness = Harness.Create(
            ctx =>
            {
                probe = ctx.RequestServices.GetRequiredService<ScopedProbe>();
                return Task.CompletedTask;
            },
            services => services.AddScoped<ScopedProbe>());

        using var _ = await harness.SendAsync(Get("http://localhost/people/"));

        Assert.That(probe?.Disposed, Is.True);
    }

    [Test]
    public async Task Send_StopsWaiting_WhenCallerCancels_EvenIfPipelineIgnoresAbort()
    {
        var release = new TaskCompletionSource();
        using var harness = Harness.Create(async _ => await release.Task);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var send = harness.SendAsync(Get("http://localhost/people/"), cts.Token);
        var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(5)));
        release.SetResult();

        Assert.That(finished, Is.SameAs(send), "the handler kept waiting after the caller cancelled");
        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await send);
    }

    [Test]
    public async Task Send_UsesRequestUriScheme_WhenNoVisitorRequestIsCurrent()
    {
        string? scheme = null;
        using var harness = Harness.Create(ctx =>
        {
            scheme = ctx.Request.Scheme;
            return Task.CompletedTask;
        });

        using var _ = await harness.SendAsync(Get("http://localhost/people/"));

        Assert.That(scheme, Is.EqualTo("http"));
    }

    [Test]
    public void Send_NoCapturedPipeline_ThrowsAndPointsAtTheNetworkTransport()
    {
        using var harness = Harness.Create(pipeline: null);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.SendAsync(Get("http://localhost/people/")));

        Assert.That(ex!.Message, Does.Contain("LoopbackTransport"));
    }

    private static HttpRequestMessage Get(string url, string? host = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (host is not null)
        {
            request.Headers.TryAddWithoutValidation("Host", host);
        }

        return request;
    }

    private static HttpContext Visitor(string scheme, string? pathBase = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.PathBase = pathBase is null ? PathString.Empty : new PathString(pathBase);
        context.Request.Host = new HostString("www.example.com");
        context.Request.Path = "/people.md";
        return context;
    }

    private sealed class ScopedProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpMessageInvoker _invoker;

        private Harness(ServiceProvider services, HttpMessageInvoker invoker, IHttpContextAccessor accessor)
        {
            _services = services;
            _invoker = invoker;
            Accessor = accessor;
        }

        public IHttpContextAccessor Accessor { get; }

        public static Harness Create(RequestDelegate? pipeline, Action<IServiceCollection>? configure = null)
        {
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddHttpContextAccessor();
            configure?.Invoke(services);
            var provider = services.BuildServiceProvider();

            var accessor = provider.GetRequiredService<IHttpContextAccessor>();
            var holder = new InMemoryPipelineHolder { Pipeline = pipeline };
            var handler = new InMemoryLoopbackHandler(holder, new DefaultHttpContextFactory(provider), accessor);
            return new Harness(provider, new HttpMessageInvoker(handler), accessor);
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
            => _invoker.SendAsync(request, cancellationToken);

        public void Dispose()
        {
            _invoker.Dispose();
            _services.Dispose();
        }
    }
}
