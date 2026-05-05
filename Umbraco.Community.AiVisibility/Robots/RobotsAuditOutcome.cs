namespace Umbraco.Community.AiVisibility.Robots;

/// <summary>
/// Story 4.2 — top-line outcome bucket for a single host's
/// <c>/robots.txt</c> audit. Each maps to a Backoffice
/// <see cref="Umbraco.Cms.Core.HealthChecks.StatusResultType"/> via
/// <see cref="RobotsAuditHealthCheck"/>.
/// </summary>
public enum RobotsAuditOutcome
{
    /// <summary>
    /// Host has a <c>/robots.txt</c> with no AI-bot blocks
    /// (<see cref="Umbraco.Cms.Core.HealthChecks.StatusResultType.Success"/>).
    /// </summary>
    NoAiBlocks = 0,

    /// <summary>
    /// Host has a <c>/robots.txt</c> blocking one or more AI crawlers
    /// (<see cref="Umbraco.Cms.Core.HealthChecks.StatusResultType.Warning"/>).
    /// </summary>
    BlocksDetected = 1,

    /// <summary>
    /// Host returned HTTP 404 for <c>/robots.txt</c>
    /// (<see cref="Umbraco.Cms.Core.HealthChecks.StatusResultType.Info"/>).
    /// "All crawlers allowed by default" — informational, not a warning.
    /// </summary>
    RobotsTxtMissing = 2,

    /// <summary>
    /// Network failure / timeout / unexpected non-2xx response
    /// (<see cref="Umbraco.Cms.Core.HealthChecks.StatusResultType.Warning"/>).
    /// </summary>
    FetchFailed = 3,

    /// <summary>
    /// HTTP succeeded but the body could not be parsed as a robots.txt
    /// document (<see cref="Umbraco.Cms.Core.HealthChecks.StatusResultType.Warning"/>).
    /// </summary>
    ParseFailed = 4,
}
