namespace LlmsTxt.Umbraco.Configuration;

/// <summary>
/// Strongly-typed binding for the <c>LlmsTxt:</c> section of <c>appsettings.json</c>.
/// Story 1.1 ships a minimal surface — only <see cref="MainContentSelectors"/> is consumed
/// by the extractor. Story 3.1 fills out the rest of the surface and introduces
/// <c>ILlmsSettingsResolver</c> for the doctype-overlay resolution.
/// </summary>
public sealed class LlmsTxtSettings
{
    public const string SectionName = "LlmsTxt";

    /// <summary>
    /// Adopter-configured CSS selector list, consulted after the built-in
    /// <c>data-llms-content</c> → <c>&lt;main&gt;</c> → <c>&lt;article&gt;</c> chain
    /// and before the SmartReader fallback.
    /// </summary>
    public IReadOnlyList<string> MainContentSelectors { get; init; } = Array.Empty<string>();
}
