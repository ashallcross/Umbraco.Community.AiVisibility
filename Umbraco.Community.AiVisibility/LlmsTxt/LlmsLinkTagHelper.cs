using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Routing;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Web.Common.Routing;

namespace Umbraco.Community.AiVisibility.LlmsTxt;

/// <summary>
/// Story 4.1 — adopter-facing Razor TagHelper that emits
/// <c>&lt;link rel="alternate" type="text/markdown" href="/{path}.md" /&gt;</c>
/// inside an Umbraco-routed view's <c>&lt;head&gt;</c>. Consumes
/// <see cref="UmbracoRouteValues"/> from the active <see cref="ViewContext.HttpContext"/>
/// to resolve the canonical page; out-of-Umbraco-context renders nothing.
/// <para>
/// Auto-discovered when adopters add <c>@addTagHelper Umbraco.Community.AiVisibility.LlmsTxt.*, Umbraco.Community.AiVisibility</c>
/// to their <c>_ViewImports.cshtml</c>. The namespace scope (rather than wildcard
/// <c>*, Umbraco.Community.AiVisibility</c>) is deliberate so future internal types in other
/// namespaces don't auto-register as adopter-facing TagHelpers. No DI registration needed —
/// <see cref="ITagHelperFactory"/> constructs the helper per-render via the
/// request scope, which is when the constructor's
/// <see cref="IExclusionEvaluator"/> dep (Scoped) is resolvable.
/// </para>
/// </summary>
[HtmlTargetElement("llms-link", TagStructure = TagStructure.WithoutEndTag)]
public sealed class LlmsLinkTagHelper : TagHelper
{
    private readonly IPublishedUrlProvider _urlProvider;
    private readonly IExclusionEvaluator _exclusion;
    private readonly ILogger<LlmsLinkTagHelper> _logger;

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    public LlmsLinkTagHelper(
        IPublishedUrlProvider urlProvider,
        IExclusionEvaluator exclusion,
        ILogger<LlmsLinkTagHelper> logger)
    {
        _urlProvider = urlProvider;
        _exclusion = exclusion;
        _logger = logger;
    }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        // Default: render nothing. Set tag + attributes only when all gates pass.
        output.SuppressOutput();

        var http = ViewContext?.HttpContext;
        if (http is null)
        {
            return;
        }

        var routeValues = http.Features.Get<UmbracoRouteValues>();
        var content = routeValues?.PublishedRequest?.PublishedContent;
        if (content is null)
        {
            _logger.LogTrace("<llms-link /> rendered outside an Umbraco-routed view; suppressing");
            return;
        }

        var culture = routeValues!.PublishedRequest!.Culture;
        var host = http.Request.Host.HasValue ? http.Request.Host.Host : null;

        try
        {
            if (await _exclusion.IsExcludedAsync(content, culture, host, http.RequestAborted))
            {
                _logger.LogTrace("<llms-link /> page excluded {ContentKey}; suppressing", content.Key);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Request aborted mid-render — suppress the helper's output and let
            // Razor unwind the rest of the view naturally.
            return;
        }

        string canonicalUrl;
        try
        {
            var requestUri = new Uri($"{http.Request.Scheme}://{(host ?? "localhost")}{http.Request.Path}{http.Request.QueryString}");
            canonicalUrl = _urlProvider.GetUrl(content, UrlMode.Default, culture, requestUri);
        }
        catch (UriFormatException ex)
        {
            // Scheme / Path / QueryString shape we cannot turn into a Uri —
            // suppress rather than YSOD the page. Adopters running unusual
            // proxy chains can hit this.
            _logger.LogTrace(ex, "<llms-link /> could not build request Uri {ContentKey}; suppressing", content.Key);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "<llms-link /> URL provider threw {ContentKey}; suppressing", content.Key);
            return;
        }

        if (string.IsNullOrEmpty(canonicalUrl) || canonicalUrl == "#")
        {
            _logger.LogTrace("<llms-link /> URL provider returned null/empty/'#' for {ContentKey}; suppressing", content.Key);
            return;
        }

        var alternateUrl = MarkdownAlternateUrl.Append(canonicalUrl);
        output.TagName = "link";
        output.TagMode = TagMode.SelfClosing;
        output.Attributes.SetAttribute("rel", "alternate");
        output.Attributes.SetAttribute("type", "text/markdown");
        output.Attributes.SetAttribute("href", alternateUrl);
    }
}
