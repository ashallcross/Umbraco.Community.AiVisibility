using Microsoft.Extensions.Options;

namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Startup validator for the <see cref="AiVisibilitySettings.LogRetention"/>
/// sub-block. Surfaces operator typos at first configuration read instead of
/// letting consumption-time <see cref="Math.Clamp(int, int, int)"/> defences
/// silently coerce values DOWN to the documented ceiling, hiding intent.
/// </summary>
/// <remarks>
/// The disable-shape values (<c>0</c> and negative for both
/// <see cref="LogRetentionSettings.DurationDays"/> and
/// <see cref="LogRetentionSettings.RunIntervalHours"/>) are documented and
/// valid — the validator does NOT flag them. <c>null</c> /
/// <c>&lt;= 0</c> for <see cref="LogRetentionSettings.RunIntervalSecondsOverride"/>
/// is the documented unset shape and is also not flagged. The flagged values
/// are upper-bound typos where consumption-time would silently clamp.
/// </remarks>
internal sealed class LogRetentionSettingsValidator : IValidateOptions<AiVisibilitySettings>
{
    public ValidateOptionsResult Validate(string? name, AiVisibilitySettings options)
    {
        var failures = new List<string>();
        var r = options.LogRetention;

        if (r.DurationDays > 3650)
        {
            failures.Add(
                $"AiVisibility:LogRetention:DurationDays ({r.DurationDays}) exceeds the documented ceiling of 3650 (~10 years); " +
                "consumption-time will silently clamp it. If 3650-day retention is intended, set the value to 3650 explicitly.");
        }

        if (r.RunIntervalHours > 8760)
        {
            failures.Add(
                $"AiVisibility:LogRetention:RunIntervalHours ({r.RunIntervalHours}) exceeds the documented ceiling of 8760 (1 year); " +
                "consumption-time will silently clamp it.");
        }

        if (r.RunIntervalSecondsOverride.HasValue && r.RunIntervalSecondsOverride.Value > 86400)
        {
            failures.Add(
                $"AiVisibility:LogRetention:RunIntervalSecondsOverride ({r.RunIntervalSecondsOverride}) exceeds the documented ceiling of 86400 (1 day); " +
                "consumption-time will silently clamp it. This is a dev/test override — production should use RunIntervalHours.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
