using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace LlmsTxt.Umbraco;

/// <summary>
/// Orchestrator composer — currently a no-op marker that anchors the package's
/// composition boundary. Sub-composers (<see cref="Composers.RoutingComposer"/> in
/// Story 1.1; <c>CachingComposer</c>, <c>NotificationsComposer</c>, etc. in later
/// stories) compose independently — Umbraco's default composer order suffices.
/// </summary>
public sealed class LlmsTxtComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Sub-composers run on their own — no orchestration needed in Story 1.1.
        // Reserved for future cross-cutting concerns that span multiple sub-composers.
    }
}
