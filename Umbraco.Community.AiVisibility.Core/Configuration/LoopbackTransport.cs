namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// How the Loopback strategy fetches a page (<c>AiVisibility:RenderStrategy:LoopbackTransport</c>).
/// </summary>
public enum LoopbackTransport
{
    /// <summary>Run the request through the site's own pipeline, in memory.</summary>
    InMemory = 0,

    /// <summary>Send an HTTP request to the site's binding or <c>LoopbackBaseUrl</c>.</summary>
    Network = 1,
}
