using System.Net;
using Umbraco.Community.AiVisibility.Caching;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Robots;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Cache;

namespace Umbraco.Community.AiVisibility.Tests.Robots;

/// <summary>
/// Story 4.2 — pinpoints the URL composition in
/// <see cref="DefaultRobotsAuditor"/>'s fetch path. Captures the request
/// URI via a stub <see cref="HttpMessageHandler"/> instead of going to
/// the network. Verifies both the production-default path (no port —
/// scheme default) and the dev-knob path
/// (<see cref="RobotsAuditorSettings.DevFetchPort"/>).
/// </summary>
[TestFixture]
public class DefaultRobotsAuditorUrlBuilderTests
{
    [Test]
    public async Task FetchUri_NoDevPort_UsesSchemeDefault()
    {
        // Production-correct path: https://example.com/robots.txt
        // (port 443 implicit — no port in the URI's authority).
        var capturedUri = await CaptureFetchUriAsync(devFetchPort: null);

        Assert.Multiple(() =>
        {
            Assert.That(capturedUri!.Scheme, Is.EqualTo("https"));
            Assert.That(capturedUri.Host, Is.EqualTo("example.com"));
            Assert.That(capturedUri.IsDefaultPort, Is.True,
                "no DevFetchPort → port is scheme default (443 for HTTPS)");
            Assert.That(capturedUri.AbsolutePath, Is.EqualTo("/robots.txt"));
        });
    }

    [Test]
    public async Task FetchUri_DevPortSet_OverridesSchemeDefault()
    {
        // Dev knob: https://example.com:44314/robots.txt
        var capturedUri = await CaptureFetchUriAsync(devFetchPort: 44314);

        Assert.Multiple(() =>
        {
            Assert.That(capturedUri!.Scheme, Is.EqualTo("https"));
            Assert.That(capturedUri.Host, Is.EqualTo("example.com"));
            Assert.That(capturedUri.Port, Is.EqualTo(44314),
                "DevFetchPort overrides the scheme default");
            Assert.That(capturedUri.AbsolutePath, Is.EqualTo("/robots.txt"));
        });
    }

    [Test]
    public async Task FetchUri_DevPortZeroOrNegative_TreatedAsUnset()
    {
        // Defensive: an adopter who sets DevFetchPort: 0 or -1 in
        // appsettings (operator typo) should not produce a malformed URI.
        // The auditor's `> 0` guard treats those as "use scheme default".
        var fromZero = await CaptureFetchUriAsync(devFetchPort: 0);
        var fromNegative = await CaptureFetchUriAsync(devFetchPort: -1);

        Assert.Multiple(() =>
        {
            Assert.That(fromZero!.IsDefaultPort, Is.True);
            Assert.That(fromNegative!.IsDefaultPort, Is.True);
        });
    }

    [Test]
    public async Task FetchUri_HttpScheme_UsesPort80()
    {
        var captured = await CaptureFetchUriAsync(devFetchPort: null, scheme: "http");

        Assert.Multiple(() =>
        {
            Assert.That(captured!.Scheme, Is.EqualTo("http"));
            Assert.That(captured.IsDefaultPort, Is.True, "http defaults to port 80");
            Assert.That(captured.AbsolutePath, Is.EqualTo("/robots.txt"));
        });
    }

    [Test]
    public async Task Audit_EmptyScheme_FallsBackToHttps()
    {
        // The interface XML doc says scheme defaults to "https" at the
        // consumer layer. Pin the auditor's defensive empty-scheme path —
        // an adopter passing string.Empty should not crash and should fetch
        // over HTTPS.
        var captured = await CaptureFetchUriAsync(devFetchPort: null, scheme: string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(captured!.Scheme, Is.EqualTo("https"));
            Assert.That(captured.AbsolutePath, Is.EqualTo("/robots.txt"));
        });
    }

    [TestCase("file")]
    [TestCase("ftp")]
    [TestCase("gopher")]
    [TestCase("javascript")]
    public async Task Audit_UnsupportedScheme_RefusedWithFetchFailed(string scheme)
    {
        // Defends against a hostile adopter override or settings-drift
        // passing in a non-http(s) scheme.
        var (auditor, _) = BuildAuditor(devFetchPort: null);
        var result = await auditor.AuditAsync("example.com", scheme, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(RobotsAuditOutcome.FetchFailed));
            Assert.That(result.ErrorMessage, Does.Contain("Unsupported scheme"));
        });
    }

    [TestCase("user@evil.example")]
    [TestCase("evil.example/path")]
    public async Task Audit_HostnameWithUserinfoOrPath_RefusedWithFetchFailed(string hostname)
    {
        var (auditor, _) = BuildAuditor(devFetchPort: null);
        var result = await auditor.AuditAsync(hostname, "https", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(RobotsAuditOutcome.FetchFailed));
            Assert.That(result.ErrorMessage, Does.Contain("Malformed hostname"));
        });
    }

    [TestCase("localhost")]
    [TestCase("127.0.0.1")]
    [TestCase("169.254.169.254")] // cloud metadata endpoint
    [TestCase("10.0.0.1")]
    [TestCase("192.168.1.1")]
    [TestCase("172.16.0.1")]
    [TestCase("0.0.0.0")]
    public async Task Audit_BlockedHost_RefusedBeforeFetch(string hostname)
    {
        // SSRF defence: refuse to fetch from loopback / link-local / private
        // RFC1918 ranges before any HTTP request is dispatched.
        var (auditor, requestSeen) = BuildAuditor(devFetchPort: null);
        var result = await auditor.AuditAsync(hostname, "https", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(RobotsAuditOutcome.FetchFailed));
            Assert.That(result.ErrorMessage, Does.Contain("loopback / link-local / private"));
            Assert.That(requestSeen.Count, Is.Zero,
                "blocked host MUST NOT issue an HTTP request");
        });
    }

    [Test]
    public async Task Audit_RedirectResponse_RefusedNotFollowed()
    {
        // Even if the underlying handler ever ships configured to follow
        // redirects, the in-app guard rejects 3xx responses to defend
        // against cross-origin redirect SSRF.
        var requests = new List<Uri>();
        var handler = new CapturingHandler(req =>
        {
            requests.Add(req.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri("https://169.254.169.254/latest/meta-data/");
            return response;
        });
        var (auditor, _) = BuildAuditor(devFetchPort: null, handler: handler);
        var result = await auditor.AuditAsync("example.com", "https", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(RobotsAuditOutcome.FetchFailed));
            Assert.That(result.ErrorMessage, Does.Contain("Refused to follow"));
            Assert.That(requests, Has.Count.EqualTo(1),
                "exactly one request — redirect must NOT be followed");
        });
    }

    private static async Task<Uri?> CaptureFetchUriAsync(int? devFetchPort, string scheme = "https")
    {
        Uri? captured = null;
        var handler = new CapturingHandler(req =>
        {
            captured = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var (auditor, _) = BuildAuditor(devFetchPort, handler);
        await auditor.AuditAsync("example.com", scheme, CancellationToken.None);
        return captured;
    }

    private static (DefaultRobotsAuditor Auditor, List<HttpRequestMessage> RequestsSeen) BuildAuditor(
        int? devFetchPort,
        HttpMessageHandler? handler = null)
    {
        var seen = new List<HttpRequestMessage>();
        handler ??= new CapturingHandler(req =>
        {
            seen.Add(req);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var disposeHandler = handler is CapturingHandler;
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        httpFactory.CreateClient().Returns(_ => new HttpClient(handler, disposeHandler: false));

        var settings = new AiVisibilitySettings
        {
            RobotsAuditor = new RobotsAuditorSettings { DevFetchPort = devFetchPort },
        };
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(settings);
        var caches = new AppCaches(
            new ObjectCacheAppCache(),
            Substitute.For<IRequestCache>(),
            new IsolatedCaches(_ => new ObjectCacheAppCache()));

        var auditor = new DefaultRobotsAuditor(
            httpFactory,
            caches,
            AiBotList.ForTesting(new[] { "GPTBot" }),
            monitor,
            NullLogger<DefaultRobotsAuditor>.Instance);

        // For CapturingHandler-based scenarios the seen-list is populated via
        // the handler's responder; for the per-test handler argument the seen-list
        // is independent. Return the local list either way; tests using a custom
        // handler can ignore it.
        return (auditor, seen);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
