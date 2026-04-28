using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LlmsTxt.Umbraco.Controllers
{
    [ApiVersion("1.0")]
    [ApiExplorerSettings(GroupName = "LlmsTxt.Umbraco")]
    public class LlmsTxtUmbracoApiController : LlmsTxtUmbracoApiControllerBase
    {

        [HttpGet("ping")]
        [ProducesResponseType<string>(StatusCodes.Status200OK)]
        public string Ping() => "Pong";
    }
}
