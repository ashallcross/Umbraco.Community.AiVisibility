using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Community.AiVisibility.Configuration;

namespace Umbraco.Community.AiVisibility;

/// <summary>
/// Orchestrator composer — anchors the package's composition boundary. Sub-composers
/// (<see cref="Composing.RoutingComposer"/> in Story 1.1; <c>CachingComposer</c>,
/// <c>NotificationsComposer</c>, etc. in later stories) compose independently —
/// Umbraco's default composer order suffices. This composer hosts cross-cutting
/// startup-time concerns that span sub-composers (e.g. the Story 6.0c legacy
/// configuration probe).
/// </summary>
public sealed class AiVisibilityComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Story 6.0c follow-up — surface stale `LlmsTxt:` config keys at startup
        // so adopters using envvar overrides don't fail silently when the section
        // rename takes effect. Does NOT read or honour the old keys — warn-loud
        // only, no shim (per AC6).
        builder.Services.AddHostedService<LegacyConfigurationProbe>();
    }
}
