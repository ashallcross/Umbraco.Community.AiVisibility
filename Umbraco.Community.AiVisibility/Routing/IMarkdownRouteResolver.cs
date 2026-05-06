using Umbraco.Cms.Core.Models.PublishedContent;

namespace Umbraco.Community.AiVisibility.Routing;

/// <summary>
/// Resolves an incoming absolute request URI to the
/// <see cref="IPublishedContent"/> Umbraco's router would dispatch — the seam
/// <see cref="Controllers.MarkdownController"/> uses to return 404 directly when no
/// content matches.
///
/// <para>
/// The default implementation (<see cref="MarkdownRouteResolver"/>) wraps
/// <see cref="Umbraco.Cms.Core.Routing.IPublishedRouter"/> +
/// <see cref="Umbraco.Cms.Core.Web.IUmbracoContextFactory"/> +
/// <see cref="Umbraco.Cms.Core.Services.IFileService"/>. Public-but-package-stable so
/// the controller's public ctor can take it as a dependency; not advertised as a
/// general-purpose adopter extension point.
/// </para>
/// </summary>
public interface IMarkdownRouteResolver
{
    Task<MarkdownRouteResolution> ResolveAsync(Uri absoluteUri, CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of <see cref="IMarkdownRouteResolver.ResolveAsync"/>:
/// <see cref="Content"/> is non-null when the route resolved, null when the controller
/// should return 404. <see cref="Culture"/> echoes
/// <see cref="Umbraco.Cms.Core.Routing.IPublishedRequest.Culture"/> at resolution time.
/// </summary>
public sealed record MarkdownRouteResolution(IPublishedContent? Content, string? Culture)
{
    public static MarkdownRouteResolution NotFound() => new(null, null);
    public static MarkdownRouteResolution Found(IPublishedContent content, string? culture)
        => new(content, culture);
}
