using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.TestServerTest;

namespace Umbraco.Community.AiVisibility.Tests.Integration;

/// <summary>
/// Base class for Story 1.5's integration tests. Inherits
/// <see cref="UmbracoTestServerTestBase"/> so each test gets a real Umbraco
/// request pipeline (HTTP client, route resolver, cache decorator).
///
/// <para>
/// <b>Scope (Story 1.5):</b> only the bare-404 test
/// <c>Get_UnpublishedPath_Returns404</c> ships under this harness. It exercises
/// the controller's not-found path without seeded content, proving the LlmsTxt
/// pipeline filter mounts and the controller is invoked for unresolved paths.
/// </para>
///
/// <para>
/// <b>Content-seeded tests deferred.</b> ACs 2/3/4/5/6 require seeded published
/// content reachable via <c>Client.GetAsync</c>. The canonical
/// content-seeding pattern from <c>umbraco/Umbraco-CMS@release/17.4.0</c>
/// (`CustomTestSetup` + `LocalServerMessenger` + `IDocumentUrlService.InitAsync` +
/// `RefreshContentCache`) was attempted on 17.3.2 but the URL provider returns
/// <c>/</c> for both root and child seeds — a v17.3.2-vs-v17.4 harness divergence
/// the architect documented in a follow-up story. Revisit when 17.4.0 ships on
/// NuGet and the canonical pattern is reachable on a runtime version we use.
/// </para>
///
/// <para>
/// <b>Database lifecycle.</b> <c>NewSchemaPerFixture</c> — schema created once
/// per test class; cheap because the bare-404 test seeds nothing.
/// </para>
///
/// <para>
/// <b>Parallelism.</b> <see cref="NonParallelizableAttribute"/> on the fixture
/// because Umbraco's <c>IScopeProvider</c> ambient scope is AsyncLocal-managed
/// and concurrent fixtures can leak ambient state.
/// </para>
/// </summary>
[NonParallelizable]
[Category("Integration")]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerFixture)]
public abstract class IntegrationTestBase : UmbracoTestServerTestBase
{
}
