namespace Umbraco.Community.AiVisibility.V13.Tests.Integration;

/// <summary>
/// Namespaced <see cref="SetUpFixtureAttribute"/> — boots the V13.TestSite ONCE for
/// the whole <c>…Integration</c> namespace and shares the booted
/// <see cref="HttpClient"/> across every integration fixture. Booting Umbraco 13 +
/// the unattended install + Clean seed costs ~30-60s, so a per-fixture boot would be
/// wasteful; one shared boot keeps the suite fast.
///
/// <para>
/// All integration fixtures are <c>[NonParallelizable]</c> because they share this
/// single Umbraco host (and Umbraco's ambient <c>IScopeProvider</c> is AsyncLocal).
/// </para>
/// </summary>
[SetUpFixture]
public sealed class SharedV13Site
{
    private static V13TestSiteFactory? _factory;

    /// <summary>The booted-once in-process HTTP client against the V13.TestSite.</summary>
    internal static HttpClient Client { get; private set; } = null!;

    /// <summary>The factory — exposed so fixtures can resolve services (e.g. IDomainService for the multi-site smoke).</summary>
    internal static V13TestSiteFactory Factory => _factory!;

    [OneTimeSetUp]
    public void BootOnce()
    {
        if (Environment.Version.Major != 8)
        {
            Assert.Inconclusive(
                "The v13 integration suite boots a full Umbraco 13 site and must run on the .NET 8 runtime. " +
                $"Current runtime: {Environment.Version}.");
        }

        _factory = new V13TestSiteFactory();
        // First client access triggers the Umbraco boot (install + migrations + Clean seed).
        Client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void TearDownOnce()
    {
        Client?.Dispose();
        _factory?.Dispose();
    }
}
