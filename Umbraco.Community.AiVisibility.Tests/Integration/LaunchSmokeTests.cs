using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Umbraco.Community.AiVisibility.Tests.Integration;

/// <summary>
/// Story 6.0b AC4 — launch-readiness integration smoke trio.
/// Three tests pinning the package's most load-bearing routes against the seeded
/// <c>Umbraco.Community.AiVisibility.TestSite</c>:
///
/// <list type="number">
/// <item><description><see cref="Get_Md_For_Seeded_Page_Returns_TextMarkdown_With_Expected_Frontmatter"/>
/// — the per-page Markdown route returns 200 + <c>text/markdown</c> + valid YAML frontmatter.</description></item>
/// <item><description><see cref="Html_Pipeline_Unchanged_For_Same_Seeded_Node"/>
/// — the package's pipeline filter does not corrupt the canonical HTML response.</description></item>
/// <item><description><see cref="If_None_Match_With_Cached_ETag_Returns_304"/>
/// — the manifest controller's ETag + 304 path works end-to-end.</description></item>
/// </list>
///
/// <para>
/// <b>Slip path.</b> If any test fails because the Clean.Core 7.0.5 demo content isn't
/// reachable under <see cref="LaunchSmokeTestBase"/>'s <see cref="WebApplicationFactory{TEntryPoint}"/>
/// boot — the same content-seeding limitation Story 1.5 / Story F.1 documented for
/// the <c>UmbracoTestServerTestBase</c> harness — escalate to Story F.1 (gated on
/// Umbraco 17.4.0). Mark the failure mode in the story's § Spec Drift Notes and
/// surface to the project lead before merging.
/// </para>
/// </summary>
[TestFixture]
public sealed class LaunchSmokeTests : LaunchSmokeTestBase
{
    private const string SeededPagePath = "/home";
    private const string SeededMarkdownPath = "/home.md";
    private const string LlmsTxtPath = "/llms.txt";

    [Test]
    public async Task Get_Md_For_Seeded_Page_Returns_TextMarkdown_With_Expected_Frontmatter()
    {
        using var response = await Client.GetAsync(SeededMarkdownPath);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                $"GET {SeededMarkdownPath} must return 200; got {(int)response.StatusCode} {response.ReasonPhrase}");
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/markdown"),
                "Content-Type must be text/markdown");
        });

        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.StartWith("---"),
                "Body must open with YAML frontmatter delimiter '---'");
            Assert.That(body, Does.Contain("title:"),
                "Frontmatter must contain title:");
            Assert.That(body, Does.Contain("url:"),
                "Frontmatter must contain url:");
            Assert.That(body, Does.Contain("updated:"),
                "Frontmatter must contain updated: (canonical fields per DefaultMarkdownContentExtractor:729-733 — title, url, updated)");
        });
    }

    [Test]
    public async Task Html_Pipeline_Unchanged_For_Same_Seeded_Node()
    {
        // The package's pipeline filter only intercepts when Accept: text/markdown is
        // sent OR the URL ends in .md. A bare GET / (no .md, no Accept override) MUST
        // flow through Umbraco's HTML pipeline unmodified.
        //
        // Use a per-test client with auto-redirect ON so an Umbraco-emitted 301
        // (canonical trailing slash, HTTPS upgrade, language root, etc.) is
        // followed transparently — the smoke trio's question is "does the
        // package's pipeline filter intercept HTML negotiation paths?", not
        // "does Umbraco redirect on this URL?". The base class's no-redirect
        // client is reserved for tests that assert HTTP shape verbatim.
        using var redirectingClient = CreateRedirectingClient();
        using var response = await redirectingClient.GetAsync(SeededPagePath);

        Assert.Multiple(() =>
        {
            Assert.That((int)response.StatusCode, Is.InRange(200, 299),
                $"GET {SeededPagePath} must reach a 2xx HTML response (redirects followed); got {(int)response.StatusCode}");
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"),
                "Content-Type must be text/html (HTML pipeline unaltered)");
        });

        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Not.StartWith("---"),
                "HTML body must not open with YAML frontmatter — that would mean the Markdown pipeline intercepted");
            var trimmed = body.TrimStart();
            var startsWithHtmlRoot =
                trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
            Assert.That(startsWithHtmlRoot, Is.True,
                "HTML body must open with a DOCTYPE or <html> root element");
        });
    }

    private HttpClient CreateRedirectingClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });

    [Test]
    public async Task If_None_Match_With_Cached_ETag_Returns_304()
    {
        // Prime: GET /llms.txt — captures the ETag.
        using var primeResponse = await Client.GetAsync(LlmsTxtPath);
        Assert.That(primeResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"Prime GET {LlmsTxtPath} must return 200; got {(int)primeResponse.StatusCode}");
        var etag = primeResponse.Headers.ETag;
        Assert.That(etag, Is.Not.Null,
            $"Prime GET {LlmsTxtPath} must emit an ETag header for the 304 path to work");

        // Replay with If-None-Match — must return 304 with empty body.
        using var revalidate = new HttpRequestMessage(HttpMethod.Get, LlmsTxtPath);
        revalidate.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag!.ToString()));
        using var revalidateResponse = await Client.SendAsync(revalidate);

        Assert.Multiple(() =>
        {
            Assert.That(revalidateResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotModified),
                $"GET {LlmsTxtPath} with If-None-Match must return 304; got {(int)revalidateResponse.StatusCode}");
            // 304 must not carry a body — HTTP/1.1 § 4.1
            Assert.That(revalidateResponse.Content.Headers.ContentLength.GetValueOrDefault(0), Is.EqualTo(0),
                "304 response must have empty body");
        });
    }
}
