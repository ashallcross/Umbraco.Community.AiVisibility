using System.Net;
using System.Net.Http.Headers;

// Namespace is `…Integration` (not `…Smoke`) so the namespaced SharedV13Site
// [SetUpFixture] covers this fixture. NUnit applies a SetUpFixture to its own
// namespace and children only, not sibling namespaces.
namespace Umbraco.Community.AiVisibility.V13.Tests.Integration;

/// <summary>
/// Public-surface smoke suite for the v13 package, run against a real Umbraco 13
/// boot with Clean seeded content over the in-process HTTP pipeline.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("Integration")]
public sealed class V13PublicSurfaceSmokeTests
{
    private static HttpClient Client => SharedV13Site.Client;

    // A Clean 4.2.2 content page that renders cleanly in the committed test site.
    private const string FeaturesMarkdownPath = "/features/index.html.md";
    private const string AboutHtmlPath = "/about/";

    // ---- .md route --------------------------------------------------------

    [Test]
    public async Task MdRoute_PublishedPage_Returns200_Markdown_WithFrontmatterAndHeaders()
    {
        using var response = await Client.GetAsync(FeaturesMarkdownPath);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/markdown"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.StartWith("---"), "YAML frontmatter opens the document");
            Assert.That(body, Does.Contain("title: Features"), "frontmatter carries the page title");
            Assert.That(body, Does.Contain("url:"), "frontmatter carries the absolute url");
            Assert.That(body, Does.Contain("updated:"), "frontmatter carries the updated timestamp");
            Assert.That(body, Does.Contain("## "), "rendered headings survive extraction");
            Assert.That(response.Headers.ETag, Is.Not.Null, "the .md response is ETagged");
            Assert.That(response.Headers.CacheControl, Is.Not.Null, "the .md response carries Cache-Control");
            AssertVaryAccept(response, "the .md response advertises Vary: Accept");
        });
    }

    [Test]
    public async Task MdRoute_UnpublishedPath_Returns404()
    {
        using var response = await Client.GetAsync("/does-not-exist.md");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "an unresolved .md path returns 404, not 500 and not a redirect");
    }

    // ---- extraction against Razor-rendered HTML ---------------------------

    [Test]
    public async Task MdRoute_BodyReflectsRazorRenderedContent()
    {
        // Assert known Clean "Features" page prose survives the render-to-extract round trip.
        var body = await Client.GetStringAsync(FeaturesMarkdownPath);

        Assert.That(body, Does.Contain("Rich Text Row"),
            "the extracted Markdown reflects the live v13 Razor render of the Features page");
    }

    // ---- /llms.txt 200 then 304 ------------------------------------------

    [Test]
    public async Task LlmsTxt_Returns200_ThenConditional304_SameETag()
    {
        using var first = await Client.GetAsync("/llms.txt");
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(first.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/markdown"));
        var firstBody = await first.Content.ReadAsStringAsync();
        var etag = first.Headers.ETag;
        Assert.Multiple(() =>
        {
            Assert.That(firstBody, Does.StartWith("# Acme Docs (v13)"),
                "/llms.txt starts with the configured site heading");
            Assert.That(firstBody, Does.Contain("> Acme product documentation"),
                "/llms.txt carries the configured site summary");
            Assert.That(firstBody, Does.Contain(".md"),
                "/llms.txt lists Markdown alternates");
            Assert.That(etag, Is.Not.Null, "/llms.txt is ETagged");
            Assert.That(first.Headers.CacheControl, Is.Not.Null, "/llms.txt carries Cache-Control");
            AssertVaryAccept(first, "/llms.txt advertises Vary: Accept");
        });

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/llms.txt");
        conditional.Headers.IfNoneMatch.Add(etag!);
        using var second = await Client.SendAsync(conditional);

        Assert.Multiple(() =>
        {
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified),
                "matching If-None-Match yields 304");
            Assert.That(second.Headers.ETag, Is.EqualTo(etag), "the 304 echoes the same ETag");
            Assert.That(second.Headers.CacheControl, Is.Not.Null, "the 304 preserves Cache-Control");
            AssertVaryAccept(second, "the 304 preserves Vary: Accept");
        });
        await AssertNoBodyAsync(second, "a 304 carries no body");
    }

    // ---- /llms-full.txt thin parity --------------------------------------

    [Test]
    public async Task LlmsFullTxt_Returns200_ThenConditional304()
    {
        using var first = await Client.GetAsync("/llms-full.txt");
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(first.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/markdown"));
        var body = await first.Content.ReadAsStringAsync();
        var etag = first.Headers.ETag;
        Assert.Multiple(() =>
        {
            Assert.That(body, Is.Not.Empty, "the full export carries content");
            Assert.That(body, Does.Contain("# About"),
                "/llms-full.txt carries page sections from the seeded site");
            Assert.That(body, Does.Contain("_Source: http://localhost/about/_"),
                "/llms-full.txt carries absolute source URLs");
            Assert.That(etag, Is.Not.Null, "/llms-full.txt is ETagged");
            Assert.That(first.Headers.CacheControl, Is.Not.Null, "/llms-full.txt carries Cache-Control");
            AssertVaryAccept(first, "/llms-full.txt advertises Vary: Accept");
        });

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/llms-full.txt");
        conditional.Headers.IfNoneMatch.Add(etag!);
        using var second = await Client.SendAsync(conditional);
        Assert.Multiple(() =>
        {
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(second.Headers.ETag, Is.EqualTo(etag), "the 304 echoes the same ETag");
            Assert.That(second.Headers.CacheControl, Is.Not.Null, "the 304 preserves Cache-Control");
            AssertVaryAccept(second, "the 304 preserves Vary: Accept");
        });
        await AssertNoBodyAsync(second, "a 304 carries no body");
    }

    // ---- Accept negotiation + Vary ---------------------------------------

    [Test]
    public async Task AcceptTextMarkdown_OnHtmlPage_NegotiatesToMarkdown_WithVaryAccept()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AboutHtmlPath);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
        using var response = await Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/markdown"),
            "Accept: text/markdown on an HTML page negotiates to the Markdown representation");
        Assert.That(response.Headers.Vary, Does.Contain("Accept"),
            "the negotiated response advertises Vary: Accept");
    }

    // ---- discoverability Link header -------------------------------------

    [Test]
    public async Task HtmlResponse_AdvertisesMarkdownAlternate_ViaLinkHeader()
    {
        // The Link response header is emitted automatically by the discoverability
        // middleware on every HTML response — no view/layout dependency, so it is the
        // reliable surface to assert over HTTP. The equivalent HTML <link rel="alternate">
        // markup is produced by the <llms-link> TagHelper, whose contract is pinned
        // deterministically in V13LlmsLinkTagHelperTests (asserting the HTML markup here
        // would depend on the Clean theme layout, which the Clean package migration
        // overwrites on a fresh install).
        using var response = await Client.GetAsync(AboutHtmlPath);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var link = response.Headers.TryGetValues("Link", out var values)
            ? string.Join(", ", values)
            : string.Empty;

        Assert.Multiple(() =>
        {
            Assert.That(link, Does.Contain("rel=\"alternate\""),
                "the discoverability Link header uses rel=\"alternate\"");
            Assert.That(link, Does.Contain("type=\"text/markdown\""),
                "the Link header advertises the text/markdown alternate");
            Assert.That(link, Does.Contain(".md>"),
                "the Link header points at the .md alternate URL");
        });
    }

    private static void AssertVaryAccept(HttpResponseMessage response, string message) =>
        Assert.That(response.Headers.Vary, Does.Contain("Accept"), message);

    private static async Task AssertNoBodyAsync(HttpResponseMessage response, string message)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Is.Empty, message);
    }
}
