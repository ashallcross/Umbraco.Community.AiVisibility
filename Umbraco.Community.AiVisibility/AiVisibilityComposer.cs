using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Community.AiVisibility;

/// <summary>
/// Orchestrator composer — currently a no-op marker that anchors the package's
/// composition boundary. Sub-composers (<see cref="Composing.RoutingComposer"/> in
/// Story 1.1; <c>CachingComposer</c>, <c>NotificationsComposer</c>, etc. in later
/// stories) compose independently — Umbraco's default composer order suffices.
/// </summary>
public sealed class AiVisibilityComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Sub-composers run on their own — no orchestration needed in Story 1.1.
        // Reserved for future cross-cutting concerns that span multiple sub-composers.
    }
}
