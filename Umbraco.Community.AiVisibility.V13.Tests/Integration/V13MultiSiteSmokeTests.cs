using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Community.AiVisibility.V13.Tests.Integration;

/// <summary>
/// Multi-site behaviour: <c>/llms.txt</c> re-roots its manifest at the content node
/// bound to the request host. The package resolves the host via
/// <c>IDomainService.GetAll(includeWildcards: true)</c> and builds the manifest from
/// the matched domain's root content node; an unmatched host falls back to the first
/// root. This proves host→root resolution works on Umbraco 13 (the net8 path of the
/// resolver uses <c>IPublishedContentCache.GetAtRoot()</c>).
///
/// <para>
/// The A/B comparison is "default host (no domain → falls back to the site root)" vs
/// "a host bound to the Blog node". Binding is applied to the Blog node only — NOT to
/// the site root — so the seeded site's default URL base is unchanged for the sibling
/// fixtures sharing this booted site. A domain on the root would re-base every page's
/// absolute URL and perturb those fixtures.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("Integration")]
public sealed class V13MultiSiteSmokeTests
{
    private const string BlogHost = "blog.example.test";

    private static HttpClient Client => SharedV13Site.Client;

    [OneTimeSetUp]
    public void BindBlogDomain()
    {
        if (Environment.Version.Major != 8)
        {
            Assert.Inconclusive("The multi-site smoke boots a real Umbraco 13 site and requires the .NET 8 runtime.");
        }

        var services = SharedV13Site.Factory.Services;
        var domainService = services.GetRequiredService<IDomainService>();
        var contentService = services.GetRequiredService<IContentService>();
        var localizationService = services.GetRequiredService<ILocalizationService>();

        var home = contentService.GetRootContent().First();
        var blog = contentService
            .GetPagedChildren(home.Id, 0, 500, out _)
            .First(c => string.Equals(c.Name, "Blog", StringComparison.OrdinalIgnoreCase));

        var languageId = localizationService.GetDefaultLanguageId()
                         ?? localizationService.GetAllLanguages().First().Id;

        var exists = domainService.GetAll(includeWildcards: true)
            .Any(d => string.Equals(d.DomainName, BlogHost, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            domainService.Save(new UmbracoDomain(BlogHost)
            {
                RootContentId = blog.Id,
                LanguageId = languageId,
            });
        }
    }

    [Test]
    public async Task LlmsTxt_DefaultHost_ListsWholeTreeFromSiteRoot()
    {
        // Default host matches no domain, so the resolver falls back to the site root
        // (Home) and the manifest lists the whole tree, including the top-level pages.
        var body = await GetLlmsTxtForHost(host: null);

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("/features/"), "the site-root manifest lists the top-level Features page");
            Assert.That(body, Does.Contain("/about/"), "the site-root manifest lists the top-level About page");
        });
    }

    [Test]
    public async Task LlmsTxt_HostBoundToBlogNode_RerootsToBlogSubtree()
    {
        // blog.example.test is bound to the Blog node, so the manifest re-roots to the
        // Blog subtree (its descendants, e.g. the Community post, addressed relative to
        // the Blog domain) and excludes the top-level Features/About siblings.
        var body = await GetLlmsTxtForHost(BlogHost);

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("/community/"),
                "the Blog-rooted manifest lists a Blog descendant (the Community post)");
            Assert.That(body, Does.Not.Contain("/features/"),
                "the Blog-rooted manifest excludes the top-level Features page (it is not under Blog)");
            Assert.That(body, Does.Not.Contain("/about/"),
                "the Blog-rooted manifest excludes the top-level About page (it is not under Blog)");
        });
    }

    private static async Task<string> GetLlmsTxtForHost(string? host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/llms.txt");
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        using var response = await Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"/llms.txt for host {host ?? "(default)"} returns 200");
        return await response.Content.ReadAsStringAsync();
    }
}
