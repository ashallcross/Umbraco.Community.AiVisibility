using System.Globalization;
using Asp.Versioning;
using LlmsTxt.Umbraco.Configuration;
using LlmsTxt.Umbraco.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Web.Common.Authorization;

namespace LlmsTxt.Umbraco.Controllers.Backoffice;

/// <summary>
/// Story 5.2 — read-only Backoffice Management API for the AI Traffic
/// dashboard. Routes to <c>/umbraco/management/api/v1/llmstxt/analytics/...</c>
/// per Spike 0.B's canonical pattern (locked decision #5):
/// <c>[VersionedApiBackOfficeRoute("llmstxt/analytics")]</c> from
/// <c>Umbraco.Cms.Api.Management.Routing</c> — the framework prepends
/// <c>/umbraco/management/api/v{version}/</c> so the resolved prefix is
/// <c>/umbraco/management/api/v1/llmstxt/analytics/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only contract:</b> the controller ships zero <c>INSERT</c> /
/// <c>UPDATE</c> / <c>DELETE</c> SQL — only <c>SELECT</c> via
/// <see cref="ILlmsAnalyticsReader"/>. Story 5.1 owns the write path
/// (via the bounded-channel drainer); Story 5.2 owns the read pass-through.
/// PII discipline (architect-A4 / project-context.md § Critical Don't-Miss
/// line 224) is preserved by NEVER widening the projection beyond the seven
/// entity columns.
/// </para>
/// <para>
/// <b>Auth pattern (Spike 0.B locked decision #11; Story 3.2 ratified):</b>
/// the Management API enforces bearer-token auth via OpenIddict, NOT cookies.
/// Cookie-only <c>fetch(..., { credentials: "include" })</c> calls return HTTP
/// 401. The dashboard at <c>llms-ai-traffic-dashboard.element.ts</c> uses
/// <c>UMB_AUTH_CONTEXT.getOpenApiConfiguration()</c> to obtain a bearer token
/// per call.
/// </para>
/// <para>
/// <b>Authorization:</b> all four actions are gated by
/// <see cref="AuthorizationPolicies.SectionAccessSettings"/>. UX-DR4 line 165
/// proposed Content section access; architecture.md:376 + Spike 0.B converged
/// on Settings — captured as Story 5.2 § Architectural drift entry 1. Editors
/// without Settings-section access receive HTTP 403; users without any
/// Backoffice auth receive HTTP 401.
/// </para>
/// <para>
/// <b>Swagger doc co-location:</b> <see cref="MapToApiAttribute"/> binds the
/// four operations to the existing <c>llmstxtumbraco</c> Swagger doc (registered
/// by <c>LlmsTxtUmbracoApiComposer</c>). The framework filter
/// <c>BackOfficeSecurityRequirementsOperationFilterBase</c> auto-adds the 401 +
/// 403 response schemas — Story 3.2 Spec Drift Note #6 documents the
/// Swagger-500 collision when explicit
/// <c>[ProducesResponseType(401)]</c> / <c>[ProducesResponseType(403)]</c>
/// attributes are added; this controller deliberately omits them.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("llmstxt/analytics")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
[MapToApi(Constants.ApiName)]
public sealed class LlmsAnalyticsManagementApiController : ManagementApiControllerBase
{
    /// <summary>
    /// Per-request cap on the number of repeated <c>?class=</c> parameters
    /// the controller will accept. Defends against query-bloat callers like
    /// <c>?class=A&amp;class=A&amp;class=A&amp;...</c>. The legitimate ceiling is the
    /// <see cref="UserAgentClass"/> enum-name count (currently 7).
    /// </summary>
    private static readonly int UserAgentClassNamesCount = Enum.GetNames<UserAgentClass>().Length;

    private readonly ILogger<LlmsAnalyticsManagementApiController> _logger;
    private readonly IOptionsMonitor<LlmsTxtSettings> _settings;
    private readonly ILlmsAnalyticsReader _reader;
    private readonly TimeProvider _timeProvider;

    public LlmsAnalyticsManagementApiController(
        ILogger<LlmsAnalyticsManagementApiController> logger,
        IOptionsMonitor<LlmsTxtSettings> settings,
        ILlmsAnalyticsReader reader,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns paginated rows ordered <c>createdUtc DESC, id DESC</c> for the
    /// effective UTC date range and optional UA-class filter set. Page is
    /// 1-based (NPoco convention).
    /// </summary>
    [HttpGet("requests")]
    [ProducesResponseType<LlmsAnalyticsRequestPageViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetRequests(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery(Name = "class")] string[]? @class,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settings.CurrentValue.Analytics;

        var (rangeResult, parsedFrom, parsedTo, rangeWasClamped) = ResolveRange(from, to, settings);
        if (rangeResult is not null)
        {
            return rangeResult;
        }

        var (classResult, filterClasses) = ParseClassFilter(@class);
        if (classResult is not null)
        {
            return classResult;
        }

        var pageSafe = Math.Max(1, page ?? 1);
        var pageSizeSafe = Math.Clamp(
            pageSize ?? settings.DefaultPageSize,
            1,
            Math.Max(1, settings.MaxPageSize));

        try
        {
            var pageResult = _reader.ReadRequestsPage(
                parsedFrom,
                parsedTo,
                filterClasses,
                pageSafe,
                pageSizeSafe);

            long? totalCappedAt = settings.MaxResultRows > 0 && pageResult.TotalItems > settings.MaxResultRows
                ? settings.MaxResultRows
                : null;

            var items = pageResult.Items
                .Select(e => new LlmsAnalyticsRequestViewModel(
                    e.Id,
                    e.CreatedUtc,
                    e.Path,
                    e.ContentKey,
                    e.Culture,
                    e.UserAgentClass,
                    e.ReferrerHost))
                .ToList();

            // Cap at int.MaxValue — NPoco's Page<T>.TotalPages is long; with the
            // result-row cap disabled (MaxResultRows = 0) and a sufficiently
            // large dataset, an unchecked (int) cast wraps to negative.
            var totalPagesSafe = (int)Math.Min(pageResult.TotalPages, int.MaxValue);

            if (rangeWasClamped)
            {
                Response.Headers["X-Llms-Range-Clamped"] = "true";
            }

            return Ok(new LlmsAnalyticsRequestPageViewModel(
                items,
                pageResult.TotalItems,
                pageSafe,
                pageSizeSafe,
                totalPagesSafe,
                parsedFrom,
                parsedTo,
                totalCappedAt));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LlmsTxt analytics GetRequests failed for range {RangeFrom}..{RangeTo} pageSize={PageSize} page={Page}",
                parsedFrom,
                parsedTo,
                pageSizeSafe,
                pageSafe);
            return Problem(
                title: "Analytics query failed",
                detail: "The analytics database query failed; retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Returns the distinct UA classifications with at least one row in the
    /// queried range, ordered by descending count. Drives the dashboard's
    /// chip-toggle source so editors never see chips with zero matching rows.
    /// </summary>
    [HttpGet("classifications")]
    [ProducesResponseType<LlmsAnalyticsClassificationViewModel[]>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetClassifications(
        [FromQuery] string? from,
        [FromQuery] string? to,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settings.CurrentValue.Analytics;

        var (rangeResult, parsedFrom, parsedTo, rangeWasClamped) = ResolveRange(from, to, settings);
        if (rangeResult is not null)
        {
            return rangeResult;
        }

        try
        {
            var rows = _reader.ReadClassifications(parsedFrom, parsedTo);
            var viewModel = rows
                .Select(r => new LlmsAnalyticsClassificationViewModel(r.UserAgentClass, r.Count))
                .ToArray();
            if (rangeWasClamped)
            {
                Response.Headers["X-Llms-Range-Clamped"] = "true";
            }
            return Ok(viewModel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LlmsTxt analytics GetClassifications failed for range {RangeFrom}..{RangeTo}",
                parsedFrom,
                parsedTo);
            return Problem(
                title: "Analytics query failed",
                detail: "The analytics database query failed; retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Single-row aggregate (count + first/last seen) for the queried range.
    /// Feeds the dashboard's "Showing N requests from X to Y" header line.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType<LlmsAnalyticsSummaryViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetSummary(
        [FromQuery] string? from,
        [FromQuery] string? to,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settings.CurrentValue.Analytics;

        var (rangeResult, parsedFrom, parsedTo, rangeWasClamped) = ResolveRange(from, to, settings);
        if (rangeResult is not null)
        {
            return rangeResult;
        }

        try
        {
            var row = _reader.ReadSummary(parsedFrom, parsedTo);
            if (rangeWasClamped)
            {
                Response.Headers["X-Llms-Range-Clamped"] = "true";
            }
            return Ok(new LlmsAnalyticsSummaryViewModel(
                row.TotalRequests,
                row.FirstSeenUtc,
                row.LastSeenUtc,
                parsedFrom,
                parsedTo));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LlmsTxt analytics GetSummary failed for range {RangeFrom}..{RangeTo}",
                parsedFrom,
                parsedTo);
            return Problem(
                title: "Analytics query failed",
                detail: "The analytics database query failed; retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// One-shot read of the configured retention duration so the dashboard
    /// can render AC9's retention-aware empty-state hint without duplicating
    /// the config-binding logic on the TypeScript side. No DB access — just
    /// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>.
    /// </summary>
    [HttpGet("retention")]
    [ProducesResponseType<LlmsAnalyticsRetentionViewModel>(StatusCodes.Status200OK)]
    public IActionResult GetRetention()
        => Ok(new LlmsAnalyticsRetentionViewModel(
            _settings.CurrentValue.LogRetention.DurationDays));

    /// <summary>
    /// Parses + clamps the <c>?from=</c> / <c>?to=</c> query inputs into
    /// timezone-aware UTC <see cref="DateTime"/> values. Returns
    /// <c>(null, from, to)</c> on success; <c>(IActionResult, default,
    /// default)</c> on failure. Sets the <c>X-Llms-Range-Clamped: true</c>
    /// response header when the requested span exceeds
    /// <see cref="AnalyticsSettings.MaxRangeDays"/> and the controller
    /// narrowed it.
    /// </summary>
    private (IActionResult? badRequest, DateTime from, DateTime to, bool wasClamped) ResolveRange(
        string? from,
        string? to,
        AnalyticsSettings settings)
    {
        var defaultRangeDays = Math.Max(1, settings.DefaultRangeDays);
        var maxRangeDays = Math.Max(1, settings.MaxRangeDays);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        DateTime parsedTo;
        if (string.IsNullOrWhiteSpace(to))
        {
            parsedTo = nowUtc;
        }
        else if (!TryParseUtc(to, out parsedTo))
        {
            return (BadRequestProblem(
                "from / to must be ISO-8601 UTC (e.g. 2026-05-04T00:00:00Z)",
                $"Could not parse 'to'='{to}' as a UTC ISO-8601 timestamp."),
                default,
                default,
                false);
        }

        DateTime parsedFrom;
        if (string.IsNullOrWhiteSpace(from))
        {
            parsedFrom = parsedTo.AddDays(-defaultRangeDays);
        }
        else if (!TryParseUtc(from, out parsedFrom))
        {
            return (BadRequestProblem(
                "from / to must be ISO-8601 UTC (e.g. 2026-05-04T00:00:00Z)",
                $"Could not parse 'from'='{from}' as a UTC ISO-8601 timestamp."),
                default,
                default,
                false);
        }

        if (parsedTo <= parsedFrom)
        {
            return (BadRequestProblem(
                "Invalid date range",
                "to must be greater than from"),
                default,
                default,
                false);
        }

        var spanDays = (parsedTo - parsedFrom).TotalDays;
        var wasClamped = false;
        if (spanDays > maxRangeDays)
        {
            var clampedFrom = parsedTo.AddDays(-maxRangeDays);
            _logger.LogInformation(
                "LlmsTxt analytics range clamped — original {OriginalSpanDays:F1} days exceeds max {MaxRangeDays} days; clamped from={ClampedFrom:O}",
                spanDays,
                maxRangeDays,
                clampedFrom);
            parsedFrom = clampedFrom;
            wasClamped = true;
        }

        return (null, parsedFrom, parsedTo, wasClamped);
    }

    /// <summary>
    /// Parses an ISO-8601 timestamp and asserts the input carried an explicit
    /// timezone designator. The <c>AssumeUniversal | AdjustToUniversal</c>
    /// styles combination (RoundtripKind is incompatible — runtime throws)
    /// adjusts offset-bearing inputs to UTC; the explicit designator check
    /// then rejects bare-ISO inputs (no <c>Z</c>, no <c>+/-HH:MM</c>) so the
    /// wire contract stays timezone-unambiguous.
    /// </summary>
    internal static bool TryParseUtc(string? raw, out DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            utc = default;
            return false;
        }

        raw = raw.Trim();

        // First check the timezone designator BEFORE parsing — `AssumeUniversal`
        // would silently treat bare ISO as UTC, defeating our explicit-designator
        // contract. Designator: ends with `Z` / `z` OR contains a `+/-HH:MM`
        // offset AFTER index 10 (the time-component starts at 11 in ISO `YYYY-MM-DDTHH:MM:SS`;
        // an offset's `+` or `-` therefore lives at index ≥ 11).
        var hasOffset = raw.EndsWith('Z') || raw.EndsWith('z')
            || raw.LastIndexOf('+') > 10
            || raw.LastIndexOf('-') > 10;
        if (!hasOffset)
        {
            utc = default;
            return false;
        }

        if (!DateTime.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed))
        {
            utc = default;
            return false;
        }

        if (parsed.Kind != DateTimeKind.Utc)
        {
            utc = default;
            return false;
        }

        utc = parsed;
        return true;
    }

    /// <summary>
    /// Validates the optional repeated <c>?class=</c> query parameter against
    /// the <see cref="UserAgentClass"/> enum names; returns the trimmed +
    /// canonical-cased values on success or a <c>400 ProblemDetails</c> result
    /// on first invalid entry.
    /// </summary>
    private (IActionResult? badRequest, IReadOnlyList<string> filterClasses) ParseClassFilter(string[]? raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return (null, Array.Empty<string>());
        }

        var trimmed = raw
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToArray();

        if (trimmed.Length == 0)
        {
            return (null, Array.Empty<string>());
        }

        var canonical = new List<string>(trimmed.Length);
        foreach (var value in trimmed)
        {
            if (!Enum.TryParse<UserAgentClass>(value, ignoreCase: true, out var parsed))
            {
                var validNames = string.Join(", ", Enum.GetNames<UserAgentClass>());
                return (BadRequestProblem(
                    "Unrecognised UA class",
                    $"Unrecognised UA class '{value}'. Valid: {validNames}"),
                    Array.Empty<string>());
            }

            canonical.Add(parsed.ToString());
        }

        var distinct = canonical.Distinct(StringComparer.Ordinal).ToArray();

        // Cap AFTER dedupe — `?class=AiTraining` repeated 8× collapses to 1 and
        // is legitimate; only an actual fan-out beyond the enum-name count is
        // query-bloat.
        if (distinct.Length > UserAgentClassNamesCount)
        {
            return (BadRequestProblem(
                "Too many class filters",
                $"At most {UserAgentClassNamesCount} distinct class filters are accepted (the count of valid UA class enum names)."),
                Array.Empty<string>());
        }

        return (null, distinct);
    }

    private IActionResult BadRequestProblem(string title, string detail)
        => Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);
}
