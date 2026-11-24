using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Captures the site's request pipeline at startup, in front of the site's own middleware.
/// </summary>
internal sealed class InMemoryPipelineStartupFilter : IStartupFilter
{
    private readonly InMemoryPipelineHolder _holder;

    public InMemoryPipelineStartupFilter(InMemoryPipelineHolder holder) => _holder = holder;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(sitePipeline =>
        {
            _holder.Pipeline = sitePipeline;
            return sitePipeline;
        });
        next(app);
    };
}
