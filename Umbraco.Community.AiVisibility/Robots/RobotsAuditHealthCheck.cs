using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using LlmsTxt.Umbraco;
using Umbraco.Cms.Core.HealthChecks;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Community.AiVisibility.Robots;

/// <summary>
/// Story 4.2 — Backoffice Health Check that surfaces the LlmsTxt robots.txt
/// audit. Auto-discovered by Umbraco's <c>HealthCheckCollectionBuilder</c> via
/// <c>TypeLoader</c>; NO manual <c>services.AddCheck&lt;T&gt;()</c> registration
/// (project-context.md § Framework-Specific Rules).
/// </summary>
/// <remarks>
/// <para>
/// Read-only by design — does NOT override
/// <see cref="HealthCheck.ExecuteAction"/> /
/// <see cref="HealthCheck.ExecuteActionAsync"/>. Adopters review the
/// suggested copy-paste rules and apply them to their own
/// <c>robots.txt</c> manually. Auto-modification of host robots.txt is
/// architecturally forbidden (UX-DR3, project-context.md § Critical
/// Don't-Miss Rules).
/// </para>
/// <para>
/// <b>Hostname resolution:</b> walks
/// <see cref="IDomainService.GetAll(bool)"/> for configured IDomain
/// bindings; falls back to the request's host header (when an
/// <see cref="IHttpContextAccessor"/> is ambient) so single-domain
/// installs without IDomain bindings still get an audit.
/// </para>
/// </remarks>
[HealthCheck(
    Constants.HealthChecks.RobotsAuditGuid,
    "LLMs robots.txt audit",
    Description = "Audits the host's /robots.txt against the LlmsTxt AI-crawler list and surfaces copy-pasteable suggestions when AI bots are blocked. Read-only — never modifies the host's robots.txt.",
    Group = Constants.HealthChecks.GroupName)]
public sealed class RobotsAuditHealthCheck : HealthCheck
{
    /// <summary>
    /// Bounded parallelism for the per-hostname fetch fan-out. Sequential
    /// awaits on a multi-IDomain install with a slow / down origin would yield
    /// N × FetchTimeoutSeconds page-load latency in the Backoffice; full
    /// parallelism risks hammering shared origins. Five is a sane middle:
    /// ≈1s P50 + ≈FetchTimeoutSeconds P99 for a typical 5–10 host install.
    /// </summary>
    private const int MaxConcurrentHostFetches = 5;

    private readonly IRobotsAuditor _auditor;
    private readonly IDomainService _domainService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<RobotsAuditHealthCheck> _logger;

    public RobotsAuditHealthCheck(
        IRobotsAuditor auditor,
        IDomainService domainService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<RobotsAuditHealthCheck> logger)
    {
        _auditor = auditor;
        _domainService = domainService;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public override async Task<IEnumerable<HealthCheckStatus>> GetStatusAsync()
    {
        var hostnames = ResolveHostnames();
        if (hostnames.Count == 0)
        {
            return new[]
            {
                new HealthCheckStatus("No hostnames available to audit.")
                {
                    ResultType = StatusResultType.Info,
                    Description = "No IDomain bindings are configured and no ambient HttpContext is available. " +
                                  "Open the Health Checks view from a Backoffice request to surface the audit.",
                },
            };
        }

        // Bounded parallelism — see MaxConcurrentHostFetches doc. The Backoffice
        // Health Check view's intent is "show me CURRENT state"; RefreshAsync
        // is the canonical force-fresh contract on IRobotsAuditor (bypasses
        // cache on entry, re-inserts on exit). Adopter implementations that
        // don't override RefreshAsync get the default-method delegation to
        // AuditAsync (their cache semantics, their problem).
        using var gate = new SemaphoreSlim(MaxConcurrentHostFetches);
        var tasks = hostnames
            .Select(async pair =>
            {
                var (hostname, scheme) = pair;
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var result = await _auditor
                        .RefreshAsync(hostname, scheme, CancellationToken.None)
                        .ConfigureAwait(false);
                    return BuildStatuses(result);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Robots audit health check: unexpected exception for hostname {Hostname}",
                        hostname);
                    return (IReadOnlyList<HealthCheckStatus>)new[]
                    {
                        new HealthCheckStatus($"Robots audit failed for {hostname}.")
                        {
                            ResultType = StatusResultType.Warning,
                            Description = $"Unexpected exception while auditing `{hostname}`: {ex.Message}",
                        },
                    };
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToArray();

        var perHost = await Task.WhenAll(tasks).ConfigureAwait(false);
        return perHost.SelectMany(s => s);
    }

    /// <summary>
    /// Internal so tests can pin the per-result status conversion without
    /// wiring an <see cref="IDomainService"/>.
    /// </summary>
    internal static IReadOnlyList<HealthCheckStatus> BuildStatuses(RobotsAuditResult result)
    {
        var statuses = new List<HealthCheckStatus>();

        switch (result.Outcome)
        {
            case RobotsAuditOutcome.RobotsTxtMissing:
                statuses.Add(new HealthCheckStatus(
                    $"`{result.Hostname}` — no /robots.txt detected. All crawlers allowed by default.")
                {
                    ResultType = StatusResultType.Info,
                    Description = "Adopters who want explicit AI-crawler controls should add a `robots.txt` " +
                                  "to the site root. The LlmsTxt package does not modify host assets.",
                });
                break;

            case RobotsAuditOutcome.NoAiBlocks:
                statuses.Add(new HealthCheckStatus(
                    $"`{result.Hostname}` — no AI crawler blocks detected.")
                {
                    ResultType = StatusResultType.Success,
                    Description = $"Audited at {result.CapturedAtUtc:u}. " +
                                  "All AI crawlers known to the package can access this host.",
                });
                break;

            case RobotsAuditOutcome.FetchFailed:
                statuses.Add(new HealthCheckStatus(
                    $"`{result.Hostname}` — unable to fetch /robots.txt.")
                {
                    ResultType = StatusResultType.Warning,
                    Description = $"Network error while auditing `{result.Hostname}`: " +
                                  $"{result.ErrorMessage ?? "unknown"}. " +
                                  "Adopters fronted by a CDN should verify the audit reaches the origin host.",
                });
                break;

            case RobotsAuditOutcome.ParseFailed:
                statuses.Add(new HealthCheckStatus(
                    $"`{result.Hostname}` — /robots.txt could not be parsed.")
                {
                    ResultType = StatusResultType.Warning,
                    Description = $"Parse error: {result.ErrorMessage ?? "unknown"}. " +
                                  "AI crawler blocks not detected, but this may not reflect actual access state. " +
                                  "Please verify the file's syntax.",
                });
                break;

            case RobotsAuditOutcome.BlocksDetected:
                AppendBlockStatuses(result, statuses);
                break;
        }

        return statuses;
    }

    private static void AppendBlockStatuses(RobotsAuditResult result, List<HealthCheckStatus> statuses)
    {
        var groups = result.Findings
            .GroupBy(f => f.Bot.Category)
            .OrderBy(g => g.Key);

        foreach (var group in groups)
        {
            // Spec § Other Dev Notes: Unknown-category findings surface as
            // "unclassified" with Info severity (not Warning) — they're a
            // signal to review the curated map, not a deliberate block worth
            // alerting on.
            var severity = group.Key == BotCategory.Unknown
                ? StatusResultType.Info
                : StatusResultType.Warning;
            // The Bellissima Health Check view renders only HealthCheckStatus.Message
            // (via unsafeHTML), HealthCheckStatus.Actions, and ReadMoreLink — NOT
            // HealthCheckStatus.Description. So inline the per-bot breakdown +
            // copy-paste suggested removals as HTML inside Message itself, otherwise
            // adopters get a bare count with no actionable detail. Description is
            // still set with the Markdown form for non-Backoffice consumers (e.g.
            // health-check email notifications) that may render Markdown.
            var messageHtml = new StringBuilder();
            messageHtml.Append("<strong><code>")
                .Append(EncodeForHtml(result.Hostname))
                .Append("</code></strong> — ")
                .Append(group.Count())
                .Append(' ').Append(CategoryLabel(group.Key)).Append(" crawler(s) blocked.");
            messageHtml.Append("<details style=\"margin-top:8px;\"><summary>Show suggested removals</summary>");
            messageHtml.Append("<p style=\"margin-top:8px;\"><em>Audited at ")
                .Append(EncodeForHtml(result.CapturedAtUtc.ToString("u")))
                .Append("</em></p>");

            var descriptionMd = new StringBuilder();
            descriptionMd.AppendLine($"Audited at {result.CapturedAtUtc:u}.");
            descriptionMd.AppendLine();

            foreach (var finding in group.OrderBy(f => f.Bot.Token, StringComparer.OrdinalIgnoreCase))
            {
                var deprecatedHtml = finding.Bot.IsDeprecated
                    ? $" <em>(deprecated — use <code>{EncodeForHtml(finding.Bot.DeprecationReplacement ?? "the modern token")}</code> instead)</em>"
                    : string.Empty;
                var operatorHtml = string.IsNullOrEmpty(finding.Bot.Operator)
                    ? string.Empty
                    : $" — operator: {EncodeForHtml(finding.Bot.Operator)}";

                messageHtml.Append("<p style=\"margin-top:8px;\"><strong><code>")
                    .Append(EncodeForHtml(finding.Bot.Token))
                    .Append("</code></strong>")
                    .Append(operatorHtml)
                    .Append(deprecatedHtml)
                    .Append("</p>");
                messageHtml.Append("<pre style=\"background:#f4f4f4;padding:8px;border-radius:4px;overflow-x:auto;\"><code>")
                    .Append(EncodeForHtml(finding.SuggestedRemoval))
                    .Append("</code></pre>");

                var deprecatedNote = finding.Bot.IsDeprecated
                    ? $" *(deprecated — use `{finding.Bot.DeprecationReplacement ?? "the modern token"}` instead)*"
                    : string.Empty;
                var operatorNote = string.IsNullOrEmpty(finding.Bot.Operator)
                    ? string.Empty
                    : $" — operator: {finding.Bot.Operator}";

                descriptionMd.AppendLine($"- **`{finding.Bot.Token}`**{operatorNote}{deprecatedNote}");
                descriptionMd.AppendLine();
                descriptionMd.AppendLine("```");
                descriptionMd.AppendLine(finding.SuggestedRemoval);
                descriptionMd.AppendLine("```");
                descriptionMd.AppendLine();
            }

            messageHtml.Append("</details>");

            statuses.Add(new HealthCheckStatus(messageHtml.ToString())
            {
                ResultType = severity,
                Description = descriptionMd.ToString().TrimEnd(),
            });
        }

        // Per-finding caveat (e.g. "documented to ignore robots.txt") —
        // data-driven from AiBotEntry.Notes (curated in AiBotList.CuratedNotes).
        // One caveat row per unique Notes string, listing the matched tokens.
        var caveatGroups = result.Findings
            .Where(f => !string.IsNullOrEmpty(f.Bot.Notes))
            .GroupBy(f => f.Bot.Notes!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var caveatGroup in caveatGroups)
        {
            var tokens = string.Join(", ", caveatGroup
                .Select(f => f.Bot.Token)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase));

            var caveatHtml = new StringBuilder()
                .Append("<strong><code>")
                .Append(EncodeForHtml(result.Hostname))
                .Append("</code></strong> — robots-compliance caveat for ")
                .Append(EncodeForHtml(tokens))
                .Append('.')
                .Append("<p style=\"margin-top:8px;\"><em>")
                .Append(EncodeForHtml(caveatGroup.Key))
                .Append("</em></p>")
                .ToString();

            statuses.Add(new HealthCheckStatus(caveatHtml)
            {
                ResultType = StatusResultType.Info,
                Description = caveatGroup.Key,
            });
        }
    }

    /// <summary>
    /// Defensive HTML encoder for content that goes into the Bellissima
    /// Health Check view's <c>unsafeHTML(result.message)</c> rendering. Adopter-
    /// controlled strings (operator names from <see cref="AiBotEntry"/>,
    /// suggested-removal text, hostname) flow through here so a hostile or
    /// buggy override can't inject script.
    /// </summary>
    private static string EncodeForHtml(string value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private IReadOnlyList<(string Host, string Scheme)> ResolveHostnames()
    {
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // CS0618 is tolerated repo-wide for IDomainService.GetAll(bool) — Story 4.1 debug-log notes.
#pragma warning disable CS0618
            foreach (var domain in _domainService.GetAll(includeWildcards: true))
#pragma warning restore CS0618
            {
                if (string.IsNullOrWhiteSpace(domain.DomainName))
                {
                    continue;
                }

                if (!Uri.TryCreate(domain.DomainName, UriKind.Absolute, out var absolute))
                {
                    if (!Uri.TryCreate($"https://{domain.DomainName}", UriKind.Absolute, out absolute))
                    {
                        // E.g. culture-only IDomain entries like "/en" — not a hostname,
                        // can't be audited. Trace so adopters wondering why a domain
                        // is missing have a diagnostic.
                        _logger.LogTrace(
                            "Robots audit health check: skipping IDomain {DomainName} — not parseable as a URL",
                            domain.DomainName);
                        continue;
                    }
                }

                var host = absolute.Host;
                if (!string.IsNullOrEmpty(host))
                {
                    hosts.TryAdd(host, absolute.Scheme);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Robots audit health check: failed to enumerate IDomain bindings; falling back to request host header");
        }

        var requestHost = _httpContextAccessor.HttpContext?.Request.Host;
        if (requestHost?.HasValue == true && !string.IsNullOrEmpty(requestHost.Value.Host))
        {
            var scheme = _httpContextAccessor.HttpContext?.Request.Scheme ?? "https";
            hosts.TryAdd(requestHost.Value.Host, scheme);
        }

        return hosts.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    private static string CategoryLabel(BotCategory category) => category switch
    {
        BotCategory.Training => "training",
        BotCategory.SearchRetrieval => "search-retrieval",
        BotCategory.UserTriggered => "user-triggered",
        BotCategory.OptOut => "opt-out",
        _ => "unclassified",
    };
}
