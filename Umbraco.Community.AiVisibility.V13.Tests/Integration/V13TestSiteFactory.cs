using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Umbraco.Community.AiVisibility.V13.Tests.Integration;

/// <summary>
/// Boots the <c>Umbraco.Community.AiVisibility.V13.TestSite</c> in-process via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> so the content-bearing v13
/// smokes (<c>.md</c>, <c>/llms.txt</c>, <c>/llms-full.txt</c>, content
/// negotiation, multi-site) run against a real Umbraco 13 stack serving real
/// Clean 4.2.2 Razor-rendered content.
///
/// <para>
/// <b>Why not <c>UmbracoTestServerTestBase</c>?</b> That harness cannot serve
/// seeded published content (the URL provider returns <c>/</c> for all seeds —
/// the content-seeding gap documented in the v17 <c>IntegrationTestBase</c> and
/// equally present on v13). The TestSite, by contrast, unattended-installs and
/// seeds Clean demo content on first boot, so its published pages are reachable
/// over HTTP.
/// </para>
///
/// <para>
/// <b>Environment.</b> Forced to <c>Development</c> so the TestSite's
/// <c>appsettings.Development.json</c> applies (SQLite connection string +
/// unattended install). The first boot runs the unattended install + the
/// AiVisibility v13 sync migration + the Clean package migration (~30-60s); the
/// SQLite database persists under the TestSite content root, so a warm db is
/// reused on subsequent runs.
/// </para>
///
/// <para>
/// <b>Run host.</b> The TestSite is net8 and boots the full Umbraco v13 framework,
/// so this factory requires the .NET 8 runtime (the dotnet@8 keg). Running under
/// net10 roll-forward is not a sanctioned mode.
/// </para>
/// </summary>
internal sealed class V13TestSiteFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}
