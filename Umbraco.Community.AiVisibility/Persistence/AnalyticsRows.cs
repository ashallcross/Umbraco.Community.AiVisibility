using NPoco;

namespace Umbraco.Community.AiVisibility.Persistence;

/// <summary>
/// Story 5.2 — public NPoco projection POCO for the <c>GROUP BY userAgentClass</c>
/// aggregation consumed by <see cref="IAnalyticsReader.ReadClassifications"/>.
/// Carries no schema annotations (no <c>[TableName]</c>) — projection only,
/// never inserted/updated.
/// </summary>
/// <remarks>
/// Public access is required because NPoco's reflection-driven materialiser
/// hydrates the type cross-assembly (the materialiser lives in the NPoco
/// assembly). The interface that returns these rows
/// (<see cref="IAnalyticsReader"/>) stays <see langword="internal"/> —
/// adopter substitution is deferred to v1.1+ per Story 5.2 § What NOT to
/// Build.
/// </remarks>
public sealed class AnalyticsClassificationRow
{
    [Column("userAgentClass")]
    public string UserAgentClass { get; set; } = string.Empty;

    [Column("count")]
    public long Count { get; set; }
}

/// <summary>
/// Story 5.2 — public NPoco projection POCO for the single-row count + min +
/// max aggregate consumed by <see cref="IAnalyticsReader.ReadSummary"/>.
/// </summary>
public sealed class AnalyticsSummaryRow
{
    [Column("totalRequests")]
    public long TotalRequests { get; set; }

    [Column("firstSeenUtc")]
    public DateTime? FirstSeenUtc { get; set; }

    [Column("lastSeenUtc")]
    public DateTime? LastSeenUtc { get; set; }
}
