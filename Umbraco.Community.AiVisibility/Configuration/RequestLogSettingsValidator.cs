using Microsoft.Extensions.Options;

namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Startup validator for the <see cref="AiVisibilitySettings.RequestLog"/>
/// sub-block. Surfaces operator typos at first configuration read instead of
/// letting consumption-time <see cref="Math.Clamp(int, int, int)"/> defences
/// silently coerce values UP to documented minimums (the bounded-channel
/// drainer tunables — too small a queue or too small a batch size silently
/// clamps to a defensive floor).
/// </summary>
/// <remarks>
/// There is no documented disable shape for the queue tunables. To disable
/// the writer, set <see cref="RequestLogSettings.Enabled"/> = <c>false</c>.
/// </remarks>
internal sealed class RequestLogSettingsValidator : IValidateOptions<AiVisibilitySettings>
{
    public ValidateOptionsResult Validate(string? name, AiVisibilitySettings options)
    {
        var failures = new List<string>();
        var r = options.RequestLog;

        if (r.QueueCapacity < 64)
        {
            failures.Add(
                $"AiVisibility:RequestLog:QueueCapacity ({r.QueueCapacity}) is below the documented minimum of 64; " +
                "consumption-time will silently clamp it up. The bounded channel needs headroom to absorb crawl bursts.");
        }

        if (r.BatchSize < 1)
        {
            failures.Add(
                $"AiVisibility:RequestLog:BatchSize must be >= 1 (got {r.BatchSize}); " +
                "consumption-time will silently clamp it up. To disable the writer set RequestLog:Enabled = false.");
        }

        if (r.MaxBatchIntervalSeconds < 1)
        {
            failures.Add(
                $"AiVisibility:RequestLog:MaxBatchIntervalSeconds must be >= 1 (got {r.MaxBatchIntervalSeconds}); " +
                "consumption-time will silently clamp it up.");
        }

        if (r.OverflowLogIntervalSeconds < 5)
        {
            failures.Add(
                $"AiVisibility:RequestLog:OverflowLogIntervalSeconds ({r.OverflowLogIntervalSeconds}) is below the documented minimum of 5 seconds; " +
                "consumption-time will silently clamp it up to avoid log spam under sustained drop pressure.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
