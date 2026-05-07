using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace Umbraco.Community.AiVisibility.Tests.Integration;

/// <summary>
/// Story 6.0b Task 4 — base fixture for the launch-readiness integration smoke trio.
///
/// <para>
/// Boots the <c>Umbraco.Community.AiVisibility.TestSite</c> entry point in-process
/// via <see cref="WebApplicationFactory{TEntryPoint}"/>. This sidesteps the Story 1.5
/// / Story F.1 content-seeding gap on Umbraco 17.3.x — the canonical
/// <c>UmbracoTestServerTestBase</c> pattern returns <c>/</c> for both root and child
/// seeded nodes; the TestSite's actual SQLite DB + Clean.Core 7.0.5 demo content
/// don't suffer from that limitation.
/// </para>
///
/// <para>
/// <b>Lifecycle.</b> One <see cref="WebApplicationFactory{TEntryPoint}"/> per test class
/// (NUnit <c>[OneTimeSetUp]</c>); <see cref="HttpClient"/> reused across tests.
/// </para>
///
/// <para>
/// <b>Database isolation.</b> The TestSite's <c>appsettings.Development.json</c> SQLite
/// DSN is host-relative (<c>|DataDirectory|/Umbraco.sqlite.db</c>); when run via the
/// factory, <c>|DataDirectory|</c> resolves to the TestSite project's
/// <c>App_Data</c> directory — i.e. tests share the dev DB. Acceptable for the read-only
/// smoke trio (none of the three tests write content); if write tests are added later,
/// override <see cref="ConfigureWebHost"/> to point at a per-fixture temp DB.
/// </para>
/// </summary>
[NonParallelizable]
[Category("Integration")]
[Category("LaunchSmoke")]
public abstract class LaunchSmokeTestBase
{
    /// <summary>
    /// The bound factory. Exposed so individual smoke tests can produce additional
    /// clients with different options (e.g. <see cref="WebApplicationFactoryClientOptions.AllowAutoRedirect"/>
    /// = <c>true</c> for paths that legitimately redirect through Umbraco's
    /// canonical-trailing-slash logic).
    /// </summary>
    protected WebApplicationFactory<Program> Factory { get; private set; } = null!;

    protected HttpClient Client { get; private set; } = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Use the existing Development environment so the unattended-install
                // settings + SiteName/SiteSummary defaults from
                // appsettings.Development.json are honoured.
                builder.UseEnvironment("Development");
            });

        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // Don't auto-follow redirects — the smoke trio asserts HTTP shape verbatim.
            // Tests that need redirects followed (e.g. HTML negotiation past an
            // Umbraco-emitted canonical-trailing-slash 301) build their own client
            // off `Factory` with AllowAutoRedirect = true.
            AllowAutoRedirect = false,
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Client?.Dispose();
        Factory?.Dispose();
    }
}
