using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace Umbraco.Community.AiVisibility.TestSite.Spikes.DistributedJob;

// SPIKE 0.B — registers the spike harness pieces only when
// `LlmsTxtSpike:DistributedJob:Enabled=true`, so ordinary TestSite runs
// do not pollute the database. The job, the store, and the inspector
// controller all live in TestSite scope; the package project never
// references any of them.
public sealed class SpikeDistributedJobComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        IServiceCollection services = builder.Services;

        services
            .AddOptions<SpikeDistributedJobOptions>()
            .BindConfiguration(SpikeDistributedJobOptions.SectionName);

        services.AddSingleton<SpikeJobLogStore>();

        // Register the IDistributedBackgroundJob instance only when the
        // operator opted in — otherwise Umbraco's DistributedJobService
        // would discover the job and start writing rows on every dev run.
        services.AddSingleton<IDistributedBackgroundJob>(sp =>
        {
            IOptionsMonitor<SpikeDistributedJobOptions> options =
                sp.GetRequiredService<IOptionsMonitor<SpikeDistributedJobOptions>>();
            if (!options.CurrentValue.Enabled)
            {
                // Return a no-op stub so DI succeeds; Umbraco's DistributedJobService
                // will iterate IEnumerable<IDistributedBackgroundJob> and run this,
                // but the stub does nothing. The shape keeps the registration shape
                // identical between enabled / disabled modes.
                return new InertSpikeJob();
            }

            return ActivatorUtilities.CreateInstance<SpikeDistributedJob>(sp);
        });
    }

    private sealed class InertSpikeJob : IDistributedBackgroundJob
    {
        public string Name => "LlmsTxt.Spike.DistributedJob.Inert";
        public TimeSpan Period => TimeSpan.FromHours(1);
        public Task ExecuteAsync() => Task.CompletedTask;
    }
}
