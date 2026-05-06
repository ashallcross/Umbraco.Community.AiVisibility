using Umbraco.Community.AiVisibility.Configuration;
using LlmsTxt.Umbraco.Routing;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Web.Common.Routing;

namespace LlmsTxt.Umbraco.TagHelpers;

/// <summary>
/// Story 4.1 — adopter-facing Razor TagHelper that emits a visually-hidden
/// <c>&lt;div role="note"&gt;</c> with text + anchor pointing at the page's
/// Markdown alternate. Visually-hidden via the <c>llms-hint</c> CSS class
/// shipped at <c>/llms-txt-umbraco.css</c> (root-served — package csproj sets
/// <c>StaticWebAssetBasePath=/</c>, so the standard RCL <c>/_content/{PackageId}/</c>
/// prefix does NOT apply); the
/// adopter opts into the stylesheet via a <c>&lt;link rel="stylesheet"&gt;</c>
/// in their layout.
/// <para>
/// Same gating shape as <see cref="LlmsLinkTagHelper"/>:
/// out-of-Umbraco-context / excluded / URL-provider failure → nothing rendered.
/// Body anchor uses a relative URL so copy-paste into AI tools resolves
/// against the source page (Evil Martians' "ChatGPT URL paste" scenario).
/// </para>
/// </summary>
[HtmlTargetElement("llms-hint", TagStructure = TagStructure.NormalOrSelfClosing)]
public sealed class LlmsHintTagHelper : TagHelper
{
    private readonly IPublishedUrlProvider _urlProvider;
    private readonly IExclusionEvaluator _exclusion;
    private readonly ILogger<LlmsHintTagHelper> _logger;

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    public LlmsHintTagHelper(
        IPublishedUrlProvider urlProvider,
        IExclusionEvaluator exclusion,
        ILogger<LlmsHintTagHelper> logger)
    {
        _urlProvider = urlProvider;
        _exclusion = exclusion;
        _logger = logger;
    }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
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
            _logger.LogTrace("<llms-hint /> rendered outside an Umbraco-routed view; suppressing");
            return;
        }

        var culture = routeValues!.PublishedRequest!.Culture;
        var host = http.Request.Host.HasValue ? http.Request.Host.Host : null;

        try
        {
            if (await _exclusion.IsExcludedAsync(content, culture, host, http.RequestAborted))
            {
                _logger.LogTrace("<llms-hint /> page excluded {ContentKey}; suppressing", content.Key);
                return;
            }
        }
        catch (OperationCanceledException)
        {
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
            _logger.LogTrace(ex, "<llms-hint /> could not build request Uri {ContentKey}; suppressing", content.Key);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "<llms-hint /> URL provider threw {ContentKey}; suppressing", content.Key);
            return;
        }

        if (string.IsNullOrEmpty(canonicalUrl) || canonicalUrl == "#")
        {
            _logger.LogTrace("<llms-hint /> URL provider returned null/empty/'#' for {ContentKey}; suppressing", content.Key);
            return;
        }

        var alternateUrl = MarkdownAlternateUrl.Append(canonicalUrl);
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "llms-hint");
        output.Attributes.SetAttribute("role", "note");

        // HTML-encode the URL to defend against any XSS via Umbraco URL provider
        // overrides that return adopter-controlled strings; the relative-path
        // shape we expect doesn't need escaping but defensive encoding costs
        // nothing at the hot path.
        var encodedUrl = HtmlEncoder.Default.Encode(alternateUrl);
        var bodyHtml =
            $"This page is also available as Markdown at <a href=\"{encodedUrl}\" rel=\"alternate\" type=\"text/markdown\">{encodedUrl}</a>.";
        output.Content.SetHtmlContent(bodyHtml);
    }
}
