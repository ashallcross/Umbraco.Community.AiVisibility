using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Web.Common.Authorization;

namespace LlmsTxt.Umbraco.Controllers.Backoffice;

// SPIKE 0.B — canonical v17 Management API pattern, validated against
// `Umbraco.Cms.Api.Management v17.3.2` via the `/tmp/probebuild` reflection probe:
//   - inherit `Umbraco.Cms.Api.Management.Controllers.ManagementApiControllerBase`
//   - apply `[VersionedApiBackOfficeRoute("llmstxt")]` (the framework prepends
//     `/umbraco/management/api/v{version}/` so the resolved route is
//     `/umbraco/management/api/v1/llmstxt/...`)
//   - apply `[MapToApi(Constants.ApiName)]` so the action lands inside our
//     dedicated Swagger document (registered by `LlmsTxtUmbracoApiComposer`)
// This lives next to the older template-scaffold `LlmsTxtUmbracoApiController`
// during the spike. Story 6 (release) reconciles the two; for now both endpoints
// coexist so we can compare behaviour in the manual gate.
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("llmstxt/spike")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
[MapToApi(Constants.ApiName)]
public sealed class LlmsSpikeManagementApiController : ManagementApiControllerBase
{
    [HttpGet("ping")]
    [ProducesResponseType<SpikePingResponse>(StatusCodes.Status200OK)]
    public IActionResult Ping()
    {
        SpikePingResponse response = new(
            Status: "ok",
            Time: DateTimeOffset.UtcNow.ToString("O"),
            InstanceId: $"{Environment.MachineName}/{Environment.ProcessId}");
        return Ok(response);
    }
}

public sealed record SpikePingResponse(string Status, string Time, string InstanceId);
