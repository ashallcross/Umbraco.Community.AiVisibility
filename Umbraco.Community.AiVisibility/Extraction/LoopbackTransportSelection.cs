using Umbraco.Community.AiVisibility.Configuration;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// <c>AiVisibility:RenderStrategy:LoopbackTransport</c>, read once so the loopback
/// client's primary handler and <see cref="ILoopbackUrlResolver"/> always agree.
/// The handler is rebuilt every few minutes; without this single read, a live edit
/// could switch the handler but not the resolver. Read once, the first time a page is
/// rendered through the loopback; restart the site after changing it.
/// </summary>
internal sealed record LoopbackTransportSelection(LoopbackTransport Transport)
{
    public bool IsInMemory => Transport == LoopbackTransport.InMemory;
}
