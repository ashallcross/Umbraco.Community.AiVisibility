using Microsoft.Extensions.Options;

namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Startup validator for the <see cref="AiVisibilitySettings.RobotsAuditor"/>
/// sub-block. Surfaces operator typos at first configuration read instead of
/// letting silent fallbacks (default port, infinite-disable cycle, silent
/// upper-bound clamp) hide the intent.
/// </summary>
/// <remarks>
/// <see cref="RobotsAuditorSettings.RefreshIntervalHours"/> values <c>&lt;= 0</c>
/// are documented as "disable the recurring refresh" and are NOT flagged.
/// <see cref="RobotsAuditorSettings.DevFetchPort"/> is checked against the
/// TCP port range when set; <c>null</c> is the documented "use the scheme
/// default" shape and is not flagged.
/// <see cref="RobotsAuditorSettings.RefreshIntervalSecondsOverride"/> values
/// <c>null</c> / <c>&lt;= 0</c> are the documented unset shape.
/// </remarks>
internal sealed class RobotsAuditorSettingsValidator : IValidateOptions<AiVisibilitySettings>
{
    public ValidateOptionsResult Validate(string? name, AiVisibilitySettings options)
    {
        var failures = new List<string>();
        var a = options.RobotsAuditor;

        if (a.RefreshIntervalHours > 8760)
        {
            failures.Add(
                $"AiVisibility:RobotsAuditor:RefreshIntervalHours ({a.RefreshIntervalHours}) exceeds the documented ceiling of 8760 (1 year); " +
                "consumption-time will silently clamp it.");
        }

        if (a.FetchTimeoutSeconds < 1)
        {
            failures.Add(
                $"AiVisibility:RobotsAuditor:FetchTimeoutSeconds must be >= 1 (got {a.FetchTimeoutSeconds}). " +
                "There is no documented disable shape — fetch timeouts cannot be zero.");
        }

        if (a.DevFetchPort.HasValue && (a.DevFetchPort.Value < 1 || a.DevFetchPort.Value > 65535))
        {
            failures.Add(
                $"AiVisibility:RobotsAuditor:DevFetchPort ({a.DevFetchPort}) is not a valid TCP port; valid range is [1, 65535]. " +
                "Set to null to use the scheme default (the production-correct shape).");
        }

        if (a.RefreshIntervalSecondsOverride.HasValue && a.RefreshIntervalSecondsOverride.Value > 86400)
        {
            failures.Add(
                $"AiVisibility:RobotsAuditor:RefreshIntervalSecondsOverride ({a.RefreshIntervalSecondsOverride}) exceeds the documented ceiling of 86400 (1 day); " +
                "consumption-time will silently clamp it. This is a dev/test override — production should use RefreshIntervalHours.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
