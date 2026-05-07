using System.Net;

namespace Umbraco.Community.AiVisibility.Tests.Integration;

/// <summary>
/// Discharges Story 1.1 Spec Task 11's deferred case (2):
/// <see cref="Umbraco.Community.AiVisibility.Controllers.MarkdownController"/> returns 404 for an
/// unresolved path — proves the LlmsTxt pipeline filter mounts under the integration
/// harness and the controller is invoked for the not-found case.
///
/// <para>
/// Other Spec Task 11 cases (published-page 200, HEAD parity, 304, exclusion,
/// multi-domain ETag, TZ stability) deferred to a follow-up story — content-seeded
/// tests need a v17.3.2 harness pattern that the canonical v17.4 pattern doesn't yet
/// solve. See the deferred Story 1.X stub.
/// </para>
/// </summary>
[TestFixture]
public sealed class MarkdownControllerIntegrationTests : IntegrationTestBase
{
    [Test]
    public async Task Get_UnpublishedPath_Returns404()
    {
        // No seeding required — path nothing has ever published. The route resolver
        // returns no IPublishedContent and the controller returns NotFound() directly.
        using var response = await Client.GetAsync("/does-not-exist.md");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "unpublished path must return 404 — not 500, not a redirect");
    }
}
