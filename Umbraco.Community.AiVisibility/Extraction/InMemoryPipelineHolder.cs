using Microsoft.AspNetCore.Http;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// The site's request pipeline, captured at startup.
/// </summary>
internal sealed class InMemoryPipelineHolder
{
    public RequestDelegate? Pipeline { get; set; }
}
