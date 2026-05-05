using Microsoft.Extensions.Options;

namespace LlmsTxt.Umbraco.Configuration;

/// <summary>
/// Story 5.2 code-review P11 — startup validator for the
/// <see cref="LlmsTxtSettings.Analytics"/> sub-block. Surfaces operator
/// typos (e.g. <c>DefaultPageSize: 0</c>, <c>MaxPageSize: -10</c>) at first
/// configuration read instead of letting the controller's <c>Math.Max</c>
/// defences silently coerce the values.
/// </summary>
/// <remarks>
/// <para>
/// <c>MaxResultRows = 0</c> (or negative) is the documented "disable cap"
/// shape (<see cref="AnalyticsSettings.MaxResultRows"/> xmldoc) and is NOT
/// flagged. Every other Analytics setting MUST be at least <c>1</c> for the
/// dashboard to be useful.
/// </para>
/// <para>
/// Cross-field invariants:
/// <list type="bullet">
/// <item><c>DefaultPageSize</c> must not exceed <c>MaxPageSize</c>.</item>
/// <item><c>DefaultRangeDays</c> must not exceed <c>MaxRangeDays</c>.</item>
/// </list>
/// Both are coerced harmlessly at the controller, but a
/// <c>DefaultPageSize</c> larger than the cap signals operator intent that
/// will silently be clamped — worth surfacing.
/// </para>
/// </remarks>
internal sealed class LlmsTxtSettingsValidator : IValidateOptions<LlmsTxtSettings>
{
    public ValidateOptionsResult Validate(string? name, LlmsTxtSettings options)
    {
        var failures = new List<string>();
        var a = options.Analytics;

        if (a.DefaultPageSize < 1)
        {
            failures.Add($"LlmsTxt:Analytics:DefaultPageSize must be >= 1 (got {a.DefaultPageSize}).");
        }

        if (a.MaxPageSize < 1)
        {
            failures.Add($"LlmsTxt:Analytics:MaxPageSize must be >= 1 (got {a.MaxPageSize}).");
        }

        if (a.DefaultPageSize > a.MaxPageSize && a.MaxPageSize >= 1)
        {
            failures.Add(
                $"LlmsTxt:Analytics:DefaultPageSize ({a.DefaultPageSize}) exceeds MaxPageSize ({a.MaxPageSize}); " +
                "requests without ?pageSize will be silently clamped down.");
        }

        if (a.DefaultRangeDays < 1)
        {
            failures.Add($"LlmsTxt:Analytics:DefaultRangeDays must be >= 1 (got {a.DefaultRangeDays}).");
        }

        if (a.MaxRangeDays < 1)
        {
            failures.Add($"LlmsTxt:Analytics:MaxRangeDays must be >= 1 (got {a.MaxRangeDays}).");
        }

        if (a.DefaultRangeDays > a.MaxRangeDays && a.MaxRangeDays >= 1)
        {
            failures.Add(
                $"LlmsTxt:Analytics:DefaultRangeDays ({a.DefaultRangeDays}) exceeds MaxRangeDays ({a.MaxRangeDays}); " +
                "requests without ?from will be silently clamped to a narrower span.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
