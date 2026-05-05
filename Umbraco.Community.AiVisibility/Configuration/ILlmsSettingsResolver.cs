namespace LlmsTxt.Umbraco.Configuration;

/// <summary>
/// Story 3.1 extension point — resolves the effective per-site settings
/// (appsettings + Settings doctype overlay) for the given (hostname, culture)
/// pair. Cached per (host, culture) at <c>llms:settings:{host}:{culture}</c>
/// for <see cref="LlmsTxtSettings.SettingsResolverCachePolicySeconds"/>
/// (default 300s).
/// </summary>
/// <remarks>
/// <para>
/// <b>Adopter override discipline</b> — same as
/// <see cref="Builders.ILlmsTxtBuilder"/> (Story 2.1). Adopters wanting to
/// swap the resolver register
/// <c>services.AddScoped&lt;ILlmsSettingsResolver, MyResolver&gt;()</c> before
/// or after <see cref="Composers.SettingsComposer"/>; our composer's
/// <c>TryAddScoped</c> bows out for pre-registrations, and DI's
/// last-registration-wins semantics handle post-registrations.
/// </para>
/// <para>
/// <b>Lifetime: Scoped.</b> The default impl reads request-scoped
/// <see cref="Umbraco.Cms.Core.Web.IUmbracoContextAccessor"/>; Singleton would
/// form a captive dependency at the root provider (the project-context.md
/// § Testing Rules canonical gate). Pinned by
/// <c>SettingsComposerTests.Compose_StartupValidation_LlmsSettingsResolver_NoCaptiveDependency</c>.
/// </para>
/// <para>
/// <b>Throw policy:</b> the resolver MUST surface unexpected errors as
/// exceptions; controllers (Stories 3.1 Tasks 5–7) catch + log <c>Warning</c>
/// + fall back to the appsettings snapshot. Same shape as Story 2.3's
/// hreflang resolver-throw graceful degradation.
/// </para>
/// </remarks>
public interface ILlmsSettingsResolver
{
    /// <summary>
    /// Resolves the effective settings for the given (hostname, culture) pair.
    /// The first call per cache key walks the published cache for the matching
    /// <c>llmsSettings</c> root content node and overlays its values onto the
    /// current <see cref="LlmsTxtSettings"/> snapshot; subsequent calls within
    /// <see cref="LlmsTxtSettings.SettingsResolverCachePolicySeconds"/> return
    /// the cached record without re-walking.
    /// </summary>
    /// <param name="hostname">
    /// The request hostname (raw <c>HttpContext.Request.Host.Host</c> — the
    /// resolver normalises via <c>AiVisibilityCacheKeys.NormaliseHost</c>). Null /
    /// empty / whitespace routes to the <c>"_"</c> sentinel — distinct from
    /// any real hostname.
    /// </param>
    /// <param name="culture">
    /// The resolved culture (BCP-47 — the resolver lowercases via
    /// <c>AiVisibilityCacheKeys.NormaliseCulture</c>). Null / empty routes to the
    /// <c>"_"</c> sentinel for invariant-content sites.
    /// </param>
    Task<ResolvedLlmsSettings> ResolveAsync(string? hostname, string? culture, CancellationToken cancellationToken);
}

/// <summary>
/// Story 3.1 — immutable record returned by
/// <see cref="ILlmsSettingsResolver.ResolveAsync"/>. Carries the per-field
/// overlay of Settings doctype values onto the appsettings snapshot.
/// </summary>
/// <param name="SiteName">
/// Effective site name. Doctype value wins; falls back to appsettings
/// <see cref="LlmsTxtSettings.SiteName"/> when the doctype field is empty.
/// May be <c>null</c> if neither layer provides a value — downstream callers
/// (e.g. <c>DefaultLlmsTxtBuilder.ResolveSiteName</c>) fall back to the
/// matched root content node's <c>Name</c> or the literal <c>"Site"</c>.
/// </param>
/// <param name="SiteSummary">
/// Effective site summary. Same per-field overlay rule as
/// <paramref name="SiteName"/>. Truncated at 500 characters (word-boundary,
/// ellipsis on truncation) per Story 3.1 Failure &amp; Edge Cases.
/// </param>
/// <param name="ExcludedDoctypeAliases">
/// <b>Union</b> of <see cref="LlmsTxtSettings.ExcludedDoctypeAliases"/>
/// (top-level appsettings list) AND the doctype's
/// <c>excludedDoctypeAliases</c> property (parsed for newline / comma /
/// semicolon separators). Case-insensitive
/// (<see cref="StringComparer.OrdinalIgnoreCase"/>). Empty when neither
/// layer contributes any aliases.
/// </param>
/// <param name="BaseSettings">
/// Full <see cref="LlmsTxtSettings"/> snapshot at resolution time.
/// Downstream callers needing fields the resolver doesn't overlay
/// (<see cref="LlmsTxtSettings.MaxLlmsFullSizeKb"/>,
/// <see cref="LlmsTxtSettings.LlmsTxtBuilder"/>, etc.) read from here.
/// </param>
public sealed record ResolvedLlmsSettings(
    string? SiteName,
    string? SiteSummary,
    IReadOnlyCollection<string> ExcludedDoctypeAliases,
    LlmsTxtSettings BaseSettings);
