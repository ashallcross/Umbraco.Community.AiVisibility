namespace Umbraco.Community.AiVisibility.Backoffice;

/// <summary>
/// Story 3.2 — view model returned by <c>GET /umbraco/management/api/v1/llmstxt/settings/</c>
/// (and the round-trip body of <c>PUT /</c>). Carries the per-field
/// resolver overlay <see cref="Configuration.ISettingsResolver.ResolveAsync"/>
/// produces, plus the live <c>llmsSettings</c> content node key so the dashboard
/// can deep-link into the standard Umbraco content tree if the editor wants to
/// edit related properties (e.g. add the per-page composition to a doctype).
/// </summary>
/// <param name="SiteName">
/// Effective site name (Settings doctype value if non-empty, else the appsettings
/// <c>LlmsTxt:SiteName</c> value, else <c>null</c>).
/// </param>
/// <param name="SiteSummary">
/// Effective site summary (same per-field overlay rule as <paramref name="SiteName"/>);
/// truncated server-side to <see cref="SummaryMaxChars"/>.
/// </param>
/// <param name="ExcludedDoctypeAliases">
/// Union of the appsettings <c>LlmsTxt:ExcludedDoctypeAliases</c> list and the
/// Settings node's <c>excludedDoctypeAliases</c> property (case-insensitive).
/// Sorted ascending by ordinal-ignore-case for stable round-trip.
/// </param>
/// <param name="SummaryMaxChars">
/// The 500-char soft cap the server enforces on <see cref="SiteSummary"/>. Surfaced
/// to the client so the form's character counter reads from one source of truth.
/// </param>
/// <param name="SettingsNodeKey">
/// The <c>llmsSettings</c> root content node's <see cref="System.Guid"/>, or <c>null</c>
/// when no Settings node exists yet (uSync-coexistence path / pre-creation state).
/// </param>
public sealed record SettingsViewModel(
    string? SiteName,
    string? SiteSummary,
    IReadOnlyList<string> ExcludedDoctypeAliases,
    int SummaryMaxChars,
    Guid? SettingsNodeKey);

/// <summary>
/// Story 3.2 — request body of <c>PUT /umbraco/management/api/v1/llmstxt/settings/</c>.
/// The controller validates the payload server-side (defence-in-depth — the
/// dashboard's client-side checks in <c>llms-settings-dashboard.element.ts</c>
/// catch the same issues earlier) and writes through <c>IContentService</c>.
/// </summary>
/// <param name="SiteName">
/// New site-name value. Null/empty/whitespace clears the Settings doctype field
/// and the resolver falls back to the appsettings value on the next read.
/// </param>
/// <param name="SiteSummary">
/// New site-summary value. Null/empty/whitespace clears the field. Length validated
/// against <see cref="SettingsViewModel.SummaryMaxChars"/> server-side; longer
/// payloads return <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/> 400.
/// </param>
/// <param name="ExcludedDoctypeAliases">
/// New exclusion list. Server validates: no whitespace-only entries, no case-insensitive
/// duplicates. Persisted as newline-separated text into the Settings doctype's
/// <c>excludedDoctypeAliases</c> textarea (matches the resolver's parser at
/// <see cref="Configuration.DefaultSettingsResolver"/>). Empty list clears all
/// doctype-level exclusions; the appsettings list (if any) still applies.
/// </param>
public sealed record LlmsSettingsUpdateRequest(
    string? SiteName,
    string? SiteSummary,
    IReadOnlyList<string> ExcludedDoctypeAliases);

/// <summary>
/// Story 3.2 — view model returned by <c>GET /umbraco/management/api/v1/llmstxt/settings/doctypes</c>.
/// Populates the dashboard's <c>excludedDoctypeAliases</c> multi-select source.
/// </summary>
/// <param name="Alias">
/// The doctype's <c>IContentType.Alias</c> — case-insensitive matched against
/// <c>IPublishedContent.ContentType.Alias</c> by the resolver and the controllers
/// that filter exclusions.
/// </param>
/// <param name="Name">
/// Human-readable doctype name (<c>IContentType.Name</c>) — surfaced as the
/// multi-select option label.
/// </param>
/// <param name="IconCss">
/// Backoffice icon CSS string (e.g. <c>"icon-document color-blue"</c>) or
/// <c>null</c>; the multi-select renders a leading icon when present.
/// </param>
public sealed record LlmsDoctypeViewModel(
    string Alias,
    string Name,
    string? IconCss);

/// <summary>
/// Story 3.2 — one row in the dashboard's read-only "Excluded pages" table
/// (<c>GET /umbraco/management/api/v1/llmstxt/settings/excluded-pages</c>).
/// Each row represents a published page whose
/// <c>excludeFromLlmExports</c> composition property is <c>true</c>.
/// </summary>
public sealed record LlmsExcludedPageViewModel(
    Guid Key,
    string Name,
    string Path,
    string? Culture,
    string ContentTypeAlias,
    string ContentTypeName);

/// <summary>
/// Story 3.2 — pagination wrapper around
/// <see cref="LlmsExcludedPageViewModel"/>. Walking all descendants per call
/// is O(n); the controller clamps <c>take</c> to <c>[1, 200]</c> and surfaces
/// <see cref="Total"/> so the dashboard can show "showing X of Y" without
/// re-counting.
/// </summary>
public sealed record LlmsExcludedPagesPageViewModel(
    IReadOnlyList<LlmsExcludedPageViewModel> Items,
    int Total,
    int Skip,
    int Take);
