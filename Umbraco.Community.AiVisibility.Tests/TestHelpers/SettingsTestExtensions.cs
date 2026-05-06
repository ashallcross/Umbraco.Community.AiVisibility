using Umbraco.Community.AiVisibility.Configuration;

namespace LlmsTxt.Umbraco.Tests.TestHelpers;

/// <summary>
/// Story 3.1 — adapt the Story 2.x tests to the Option-A breaking change to
/// <see cref="LlmsTxt.Umbraco.Builders.LlmsTxtBuilderContext"/> /
/// <see cref="LlmsTxt.Umbraco.Builders.LlmsFullBuilderContext"/>: their
/// <c>Settings</c> field is now <see cref="ResolvedLlmsSettings"/>, not
/// <see cref="AiVisibilitySettings"/>. Existing test fixtures construct an
/// appsettings snapshot and pass it directly; this helper wraps that snapshot
/// into a <see cref="ResolvedLlmsSettings"/> with the same per-field overlay
/// shape <see cref="DefaultSettingsResolver"/> emits when no Settings
/// doctype node exists (the appsettings-only fallback).
/// </summary>
internal static class SettingsTestExtensions
{
    /// <summary>
    /// Wrap an appsettings snapshot into a <see cref="ResolvedLlmsSettings"/>
    /// — the same shape <c>DefaultSettingsResolver</c> produces on the
    /// no-Settings-node fallback path. Useful for Story 2.x tests that built
    /// their fixtures around <see cref="AiVisibilitySettings"/> directly.
    /// </summary>
    public static ResolvedLlmsSettings ToResolved(this AiVisibilitySettings settings)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in settings.ExcludedDoctypeAliases ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                excluded.Add(alias.Trim());
            }
        }
        return new ResolvedLlmsSettings(
            SiteName: settings.SiteName,
            SiteSummary: settings.SiteSummary,
            ExcludedDoctypeAliases: excluded,
            BaseSettings: settings);
    }
}
