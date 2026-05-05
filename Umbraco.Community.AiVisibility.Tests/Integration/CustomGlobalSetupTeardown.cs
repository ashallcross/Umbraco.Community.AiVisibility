using Umbraco.Cms.Tests.Integration.Testing;

// IMPORTANT: This class deliberately has NO NAMESPACE per Umbraco's integration-test
// harness contract — `[SetUpFixture]` in the global namespace applies to the whole
// test assembly. A namespaced wrapper would make `OneTimeSetUp` / `OneTimeTearDown`
// silently no-op, leaving every integration test booting against an uninitialised
// harness. Verified against Umbraco.Cms.Tests.Integration 17.3.2 xmldoc:
// "This class has NO NAMESPACE so it applies to the whole assembly."

[SetUpFixture]
public sealed class CustomGlobalSetupTeardown
{
    private GlobalSetupTeardown _setupTeardown = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _setupTeardown = new GlobalSetupTeardown();
        _setupTeardown.SetUp();
    }

    [OneTimeTearDown]
    public void TearDown() => _setupTeardown.TearDown();
}
