using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace LlmsTxt.Umbraco.Routing;

/// <summary>
/// Registers the <c>{**path:nonfile}.md</c> route for <see cref="Controllers.MarkdownController"/>
/// inside Umbraco's <see cref="UmbracoPipelineFilter.Endpoints"/> callback so it resolves
/// before Umbraco's content fallback returns 404.
/// </summary>
public sealed class LlmsPipelineFilter : UmbracoPipelineFilter
{
    public LlmsPipelineFilter()
        : base("LlmsTxt")
    {
        Endpoints = MapEndpoints;
        // Story 1.3 — Accept-header content negotiation runs after Umbraco's routing
        // middleware (so UmbracoRouteValues is populated on HttpContext.Features) and
        // before authentication/authorization. PostRouting is the canonical stage.
        PostRouting = MapPostRouting;
    }

    private static void MapEndpoints(IApplicationBuilder app)
    {
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllerRoute(
                name: Constants.Routes.MarkdownRouteName,
                pattern: Constants.Routes.MarkdownRoutePattern,
                defaults: new { controller = "Markdown", action = "Render" },
                constraints: new { path = new MarkdownSuffixRouteConstraint() });
        });
    }

    private static void MapPostRouting(IApplicationBuilder app)
    {
        app.UseMiddleware<AcceptHeaderNegotiationMiddleware>();
    }

    /// <summary>
    /// Pure-function predicate exposed for unit testing (and reuse from
    /// <see cref="Composers.RoutingComposer"/> when composing
    /// <c>UmbracoRequestOptions.HandleAsServerSideRequest</c>).
    /// </summary>
    internal static bool IsMarkdownPath(PathString path)
        => path.HasValue
           && path.Value!.EndsWith(Constants.Routes.MarkdownSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Composes the package's <c>.md</c> predicate with any pre-existing
    /// <see cref="Umbraco.Cms.Web.Common.AspNetCore.UmbracoRequestOptions"/>
    /// <c>HandleAsServerSideRequest</c> delegate (per AR2 — never overwrite).
    /// </summary>
    /// <param name="previous">
    /// The delegate the adopter (or Umbraco core) has already registered, or null
    /// if there is none.
    /// </param>
    internal static Func<HttpRequest, bool> ComposeHandleAsServerSideRequest(
        Func<HttpRequest, bool>? previous)
    {
        return req =>
        {
            if (previous is not null && previous(req))
            {
                return true;
            }

            return IsMarkdownPath(req.Path);
        };
    }

    /// <summary>
    /// Route constraint that matches paths ending in <c>.md</c> (case-insensitive).
    /// Wired alongside the <c>{**path:nonfile}</c> catch-all so Umbraco's content
    /// fallback handles non-<c>.md</c> requests unchanged.
    /// </summary>
    private sealed class MarkdownSuffixRouteConstraint : IRouteConstraint
    {
        public bool Match(
            HttpContext? httpContext,
            IRouter? route,
            string routeKey,
            RouteValueDictionary values,
            RouteDirection routeDirection)
        {
            if (!values.TryGetValue(routeKey, out var value) || value is null)
            {
                return false;
            }

            return value.ToString() is string s
                && s.EndsWith(Constants.Routes.MarkdownSuffix.TrimStart('/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
