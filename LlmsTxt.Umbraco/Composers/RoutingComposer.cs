using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Extraction;
using LlmsTxt.Umbraco.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Cms.Web.Common.Routing;

namespace LlmsTxt.Umbraco.Composers;

/// <summary>
/// Registers the <c>{**path:nonfile}.md</c> route, the
/// <see cref="LlmsPipelineFilter"/>, the
/// <see cref="UmbracoRequestOptions.HandleAsServerSideRequest"/> composition,
/// and the extraction-pipeline DI graph (<see cref="PageRenderer"/>,
/// <see cref="MarkdownConverter"/>, <see cref="IContentRegionSelector"/>,
/// <see cref="IMarkdownContentExtractor"/>).
/// </summary>
public sealed class RoutingComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Strongly-typed appsettings binding.
        builder.Services
            .AddOptions<LlmsTxtSettings>()
            .Bind(builder.Config.GetSection(LlmsTxtSettings.SectionName));

        // Pipeline filter — registers the .md endpoint via UmbracoPipelineFilter.Endpoints.
        builder.Services.Configure<UmbracoPipelineOptions>(opts =>
            opts.AddFilter(new LlmsPipelineFilter()));

        // Compose UmbracoRequestOptions.HandleAsServerSideRequest with any pre-existing
        // delegate (per AR2 — never overwrite). PostConfigure runs after Configure so
        // we observe whatever Umbraco core / adopters have already set.
        builder.Services.PostConfigure<UmbracoRequestOptions>(opts =>
        {
            var previous = opts.HandleAsServerSideRequest;
            opts.HandleAsServerSideRequest =
                LlmsPipelineFilter.ComposeHandleAsServerSideRequest(previous);
        });

        // HttpContextAccessor needed by PageRenderer to derive scheme/host.
        builder.Services.AddHttpContextAccessor();

        // Extraction pipeline — TryAdd* per AR17 so adopters can override before our
        // composer runs by registering their own implementation first.
        builder.Services.TryAddTransient<PageRenderer>();
        builder.Services.TryAddSingleton<MarkdownConverter>();
        builder.Services.TryAddTransient<IContentRegionSelector, Extraction.DefaultContentRegionSelector>();
        builder.Services.TryAddTransient<IMarkdownContentExtractor, Extraction.DefaultMarkdownContentExtractor>();
    }
}
