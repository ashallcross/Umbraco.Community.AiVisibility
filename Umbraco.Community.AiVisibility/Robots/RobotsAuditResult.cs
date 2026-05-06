namespace Umbraco.Community.AiVisibility.Robots;

/// <summary>
/// Story 4.2 — outcome of a single host's <c>/robots.txt</c> audit. Cached
/// at <c>llms:robots:{hostname}</c> for
/// <see cref="Umbraco.Community.AiVisibility.Configuration.RobotsAuditorSettings.RefreshIntervalHours"/>
/// hours and rewritten by <see cref="Umbraco.Community.AiVisibility.Background.RobotsAuditRefreshJob"/>.
/// Read by <see cref="RobotsAuditHealthCheck"/> at view-render time.
/// </summary>
/// <param name="Hostname">Lowercased host the audit ran against.</param>
/// <param name="Outcome">Top-line bucket — see <see cref="RobotsAuditOutcome"/>.</param>
/// <param name="Findings">Individual matched-and-blocked AI crawlers.
/// Empty when <see cref="Outcome"/> is <see cref="RobotsAuditOutcome.NoAiBlocks"/>,
/// <see cref="RobotsAuditOutcome.RobotsTxtMissing"/>,
/// <see cref="RobotsAuditOutcome.FetchFailed"/>, or
/// <see cref="RobotsAuditOutcome.ParseFailed"/>.</param>
/// <param name="CapturedAtUtc">When the audit ran. Surfaces in the
/// Backoffice as "audited at &lt;timestamp&gt;" so editors know how stale
/// the result is.</param>
/// <param name="ErrorMessage">Diagnostic when <see cref="Outcome"/> is
/// <see cref="RobotsAuditOutcome.FetchFailed"/> or
/// <see cref="RobotsAuditOutcome.ParseFailed"/>. <c>null</c> on success
/// outcomes.</param>
public sealed record RobotsAuditResult(
    string Hostname,
    RobotsAuditOutcome Outcome,
    IReadOnlyList<RobotsAuditFinding> Findings,
    DateTime CapturedAtUtc,
    string? ErrorMessage = null);
