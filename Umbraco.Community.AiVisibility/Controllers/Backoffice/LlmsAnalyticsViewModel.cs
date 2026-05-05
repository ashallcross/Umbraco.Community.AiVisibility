namespace LlmsTxt.Umbraco.Controllers.Backoffice;

/// <summary>
/// Story 5.2 — one row in the AI Traffic dashboard's table
/// (<c>GET /umbraco/management/api/v1/llmstxt/analytics/requests</c>).
/// Mirrors the columns of Story 5.1's <see cref="Persistence.Entities.RequestLogEntry"/>
/// — projection only, no schema annotations.
/// </summary>
/// <param name="Id">Auto-increment PK; deterministic tiebreaker for rows sharing <see cref="CreatedUtc"/>.</param>
/// <param name="CreatedUtc">Row insert timestamp; ISO-8601 UTC in JSON.</param>
/// <param name="Path">Canonical request path (query-string-stripped per Story 5.1 PII discipline).</param>
/// <param name="ContentKey">Content node GUID (<c>null</c> for manifest routes <c>/llms.txt</c>, <c>/llms-full.txt</c>).</param>
/// <param name="Culture">Effective culture (BCP-47); empty string when unresolved (Story 5.1 AC4).</param>
/// <param name="UserAgentClass">Story 5.1 <see cref="Persistence.UserAgentClass"/> enum NAME (e.g. <c>"AiTraining"</c>).</param>
/// <param name="ReferrerHost">Host segment of the <c>Referer</c> header; never the path / query / fragment.</param>
public sealed record LlmsAnalyticsRequestViewModel(
    long Id,
    DateTime CreatedUtc,
    string Path,
    Guid? ContentKey,
    string Culture,
    string UserAgentClass,
    string? ReferrerHost);

/// <summary>
/// Story 5.2 — paginated wrapper around <see cref="LlmsAnalyticsRequestViewModel"/>.
/// Maps NPoco's <c>Page&lt;T&gt;</c> 1:1 onto the wire shape consumed by
/// <c>llms-ai-traffic-dashboard.element.ts</c>.
/// </summary>
/// <param name="Items">Rows for the current page, ordered <c>createdUtc DESC, id DESC</c>.</param>
/// <param name="Total">Total in-range row count (BEFORE the optional <see cref="TotalCappedAt"/> hint).</param>
/// <param name="Page">Current page (1-based per NPoco convention).</param>
/// <param name="PageSize">Effective page size after clamping.</param>
/// <param name="TotalPages">Page count derived from <see cref="Total"/> / <see cref="PageSize"/>.</param>
/// <param name="RangeFrom">Effective UTC range start (post-clamp).</param>
/// <param name="RangeTo">Effective UTC range end (exclusive upper bound; post-clamp).</param>
/// <param name="TotalCappedAt">
/// When non-null, indicates <see cref="Total"/> exceeded
/// <c>LlmsTxt:Analytics:MaxResultRows</c>; the dashboard shows the
/// "Showing first N results — narrow your range" footer.
/// </param>
public sealed record LlmsAnalyticsRequestPageViewModel(
    IReadOnlyList<LlmsAnalyticsRequestViewModel> Items,
    long Total,
    int Page,
    int PageSize,
    int TotalPages,
    DateTime RangeFrom,
    DateTime RangeTo,
    long? TotalCappedAt);

/// <summary>
/// Story 5.2 — view model returned by
/// <c>GET /umbraco/management/api/v1/llmstxt/analytics/classifications</c>.
/// Populates the dashboard's UA-class filter chip set; classes with zero rows
/// in the current range are silently absent (epic Failure &amp; Edge Cases case 4 —
/// "avoid empty filter options").
/// </summary>
/// <param name="Class">
/// The <see cref="Persistence.UserAgentClass"/> enum NAME (e.g. <c>"AiTraining"</c>),
/// case-sensitive ordinal match against the table's <c>userAgentClass</c> column.
/// </param>
/// <param name="Count">Row count for this class within the queried range.</param>
public sealed record LlmsAnalyticsClassificationViewModel(
    string Class,
    long Count);

/// <summary>
/// Story 5.2 — view model returned by
/// <c>GET /umbraco/management/api/v1/llmstxt/analytics/summary</c>.
/// Feeds the dashboard's "Showing N requests from X to Y" header line.
/// </summary>
/// <param name="TotalRequests">Row count for the queried range; never null (SQL <c>COUNT(*)</c>).</param>
/// <param name="FirstSeenUtc">Earliest <c>createdUtc</c> in range; <c>null</c> on empty result set.</param>
/// <param name="LastSeenUtc">Latest <c>createdUtc</c> in range; <c>null</c> on empty result set.</param>
/// <param name="RangeFrom">Effective UTC range start (post-clamp).</param>
/// <param name="RangeTo">Effective UTC range end (post-clamp).</param>
public sealed record LlmsAnalyticsSummaryViewModel(
    long TotalRequests,
    DateTime? FirstSeenUtc,
    DateTime? LastSeenUtc,
    DateTime RangeFrom,
    DateTime RangeTo);

/// <summary>
/// Story 5.2 — view model returned by
/// <c>GET /umbraco/management/api/v1/llmstxt/analytics/retention</c>.
/// One-shot read of <c>IOptionsMonitor&lt;LlmsTxtSettings&gt;.CurrentValue.LogRetention.DurationDays</c>
/// so the dashboard can render AC9's retention-aware empty-state hint without
/// duplicating the config-binding logic on the TypeScript side.
/// </summary>
public sealed record LlmsAnalyticsRetentionViewModel(int DurationDays);

