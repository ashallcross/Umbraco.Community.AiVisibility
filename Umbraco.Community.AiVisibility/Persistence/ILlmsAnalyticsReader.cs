using LlmsTxt.Umbraco.Persistence.Entities;
using NPoco;

namespace LlmsTxt.Umbraco.Persistence;

/// <summary>
/// Story 5.2 — testability seam for the AI Traffic dashboard's read path.
/// Wraps the three NPoco queries (paged rows, GROUP-BY classification
/// counts, single-row aggregate) behind an interface so the controller can
/// be unit-tested without mocking the concrete <c>NPoco.Database</c> class
/// (whose <c>Page&lt;T&gt;</c> / <c>Fetch&lt;T&gt;</c> methods are not on the
/// <see cref="IDatabase"/> interface and therefore can't be intercepted by
/// NSubstitute).
/// </summary>
/// <remarks>
/// <para>
/// <b>NOT a documented extension point in v1.</b> Story 5.2's "What NOT to
/// Build" section defers the pluggable read seam to v1.1+ pending
/// real-adopter demand; this interface exists for testability, not as an
/// advertised seam. Adopters who replace <c>ILlmsRequestLog</c> with a
/// non-DB sink (App Insights, Serilog, etc.) ship their own dashboards
/// against their own sinks rather than substituting this reader.
/// </para>
/// <para>
/// The default implementation (<see cref="DefaultLlmsAnalyticsReader"/>) is
/// <see langword="internal"/> — adopter substitutions cannot delegate to it.
/// </para>
/// <para>
/// All three methods open an Infrastructure-flavour
/// <see cref="Umbraco.Cms.Infrastructure.Scoping.IScopeProvider"/> scope
/// at <see cref="System.Data.IsolationLevel.ReadCommitted"/> for stable
/// reads against concurrent inserts from Story 5.1's drainer.
/// </para>
/// </remarks>
public interface ILlmsAnalyticsReader
{
    /// <summary>
    /// Returns one page of <see cref="LlmsTxtRequestLogEntry"/> rows
    /// matching <paramref name="from"/> ≤ createdUtc &lt; <paramref name="to"/>
    /// and (when non-empty) restricted to the supplied
    /// <paramref name="filterClasses"/>. Ordering is
    /// <c>createdUtc DESC, id DESC</c> for stable pagination.
    /// </summary>
    Page<LlmsTxtRequestLogEntry> ReadRequestsPage(
        DateTime from,
        DateTime to,
        IReadOnlyList<string> filterClasses,
        int page,
        int pageSize);

    /// <summary>
    /// Returns one entry per distinct UA classification in
    /// <paramref name="from"/> ≤ createdUtc &lt; <paramref name="to"/>
    /// with the row count, ordered by descending count then ascending name.
    /// </summary>
    IReadOnlyList<LlmsAnalyticsClassificationRow> ReadClassifications(DateTime from, DateTime to);

    /// <summary>
    /// Returns the single-row aggregate (count + first-seen + last-seen)
    /// for <paramref name="from"/> ≤ createdUtc &lt; <paramref name="to"/>.
    /// On an empty result set the row's count is zero and timestamps are
    /// <c>null</c>.
    /// </summary>
    LlmsAnalyticsSummaryRow ReadSummary(DateTime from, DateTime to);
}

// Note: LlmsAnalyticsClassificationRow + LlmsAnalyticsSummaryRow live in
// LlmsTxt.Umbraco.Persistence/LlmsAnalyticsRows.cs (Story 5.2 Spec Drift Note
// #3 — moved out of Controllers/Backoffice to honour the Persistence ←
// Controllers folder dependency boundary once they became reader return
// types). They're public projection POCOs (NPoco's reflection-driven
// materialiser needs public access). The interface above is also `public`
// (Spec Drift Note #6 — required by C# inconsistent-accessibility with the
// public controller ctor); DefaultLlmsAnalyticsReader stays `internal sealed`
// so adopter substitutions cannot delegate to the default impl.
