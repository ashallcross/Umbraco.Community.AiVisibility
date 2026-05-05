namespace LlmsTxt.Umbraco.TestSite.Spikes.DistributedJob;

// SPIKE 0.B — bound from `LlmsTxtSpike:DistributedJob:*` in appsettings.
// The job stays inert unless `Enabled=true` so ordinary TestSite runs do
// not write rows. Removed when Story 5.1 ships the real retention job.
public sealed class SpikeDistributedJobOptions
{
    public const string SectionName = "LlmsTxtSpike:DistributedJob";

    public bool Enabled { get; set; }

    public TimeSpan Period { get; set; } = TimeSpan.FromSeconds(60);
}
