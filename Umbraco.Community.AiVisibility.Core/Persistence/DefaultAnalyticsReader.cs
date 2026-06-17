using System.Data;
using System.Text;
using Umbraco.Community.AiVisibility.Persistence.Entities;
using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace Umbraco.Community.AiVisibility.Persistence;

/// <summary>
/// Story 5.2 — default <see cref="IAnalyticsReader"/> implementation
/// reading directly from the host DB's <c>aiVisibilityRequestLog</c> table
/// via Infrastructure-flavour <see cref="IScopeProvider"/> + NPoco.
/// </summary>
/// <remarks>
/// Internal — see <see cref="IAnalyticsReader"/> for the
/// "no public substitution" rationale. Each query opens a fresh scope at
/// <see cref="IsolationLevel.ReadCommitted"/>, runs the SQL, and calls
/// <c>scope.Complete()</c>. Read-only by design — no <c>INSERT</c> /
/// <c>UPDATE</c> / <c>DELETE</c> SQL leaves this class.
/// </remarks>
internal sealed class DefaultAnalyticsReader : IAnalyticsReader
{
    internal const string TableName = "aiVisibilityRequestLog";

    private readonly IScopeProvider _scopeProvider;

    public DefaultAnalyticsReader(IScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
    }

    public Page<RequestLogEntry> ReadRequestsPage(
        DateTime from,
        DateTime to,
        IReadOnlyList<string> filterClasses,
        int page,
        int pageSize)
    {
        var sqlBuilder = new StringBuilder("WHERE createdUtc >= @0 AND createdUtc < @1");
        var args = new List<object> { from, to };

        if (filterClasses.Count > 0)
        {
            sqlBuilder.Append(" AND userAgentClass IN (");
            for (var i = 0; i < filterClasses.Count; i++)
            {
                if (i > 0)
                {
                    sqlBuilder.Append(", ");
                }

                sqlBuilder.Append('@').Append(args.Count);
                args.Add(filterClasses[i]);
            }

            sqlBuilder.Append(')');
        }

        sqlBuilder.Append(" ORDER BY createdUtc DESC, id DESC");

        using var scope = _scopeProvider.CreateScope(IsolationLevel.ReadCommitted);
        var result = scope.Database.Page<RequestLogEntry>(
            page,
            pageSize,
            sqlBuilder.ToString(),
            args.ToArray());
        scope.Complete();
        return result;
    }

    public IReadOnlyList<AnalyticsClassificationRow> ReadClassifications(DateTime from, DateTime to)
    {
        const string sql =
            "SELECT userAgentClass AS userAgentClass, COUNT(*) AS count " +
            "FROM " + TableName + " " +
            "WHERE createdUtc >= @0 AND createdUtc < @1 " +
            "GROUP BY userAgentClass " +
            "ORDER BY COUNT(*) DESC, userAgentClass ASC";

        using var scope = _scopeProvider.CreateScope(IsolationLevel.ReadCommitted);
        var rows = scope.Database.Fetch<AnalyticsClassificationRow>(sql, from, to);
        scope.Complete();
        return rows;
    }

    public AnalyticsSummaryRow ReadSummary(DateTime from, DateTime to)
    {
        const string sql =
            "SELECT COUNT(*) AS totalRequests, " +
            "MIN(createdUtc) AS firstSeenUtc, " +
            "MAX(createdUtc) AS lastSeenUtc " +
            "FROM " + TableName + " " +
            "WHERE createdUtc >= @0 AND createdUtc < @1";

        using var scope = _scopeProvider.CreateScope(IsolationLevel.ReadCommitted);
        var row = scope.Database
            .Fetch<AnalyticsSummaryRow>(sql, from, to)
            .FirstOrDefault()
            ?? new AnalyticsSummaryRow();
        scope.Complete();
        return row;
    }
}
