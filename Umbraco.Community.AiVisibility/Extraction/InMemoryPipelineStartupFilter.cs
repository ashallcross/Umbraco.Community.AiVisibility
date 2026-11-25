using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Captures the site's request pipeline at startup, in front of the site's own middleware.
/// </summary>
/// <remarks>
/// Middleware added by startup filters registered before this one (e.g. host filtering,
/// or forwarded headers enabled via <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>) sits
/// outside the captured pipeline and is not run for in-memory requests; everything the
/// site adds in <c>Program.cs</c> is inside. Scheme and path base are copied from the
/// visitor's request instead.
/// </remarks>
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
