using System.Net;
using System.Net.Sockets;
using Umbraco.Community.AiVisibility.Caching;
using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Cache;

namespace Umbraco.Community.AiVisibility.Robots;

/// <summary>
/// Story 4.2 — default <see cref="IRobotsAuditor"/> implementation.
/// Fetches the host's <c>/robots.txt</c> via
/// <see cref="IHttpClientFactory"/>, parses User-agent / Disallow blocks,
/// and cross-references against <see cref="AiBotList"/>. Results are
/// cached at <c>llms:robots:{hostname}</c> for
/// <see cref="RobotsAuditorSettings.RefreshIntervalHours"/> hours via
/// <see cref="AppCaches.RuntimeCache"/>; concurrent calls share the
/// single-flight factory delegate per Umbraco's
/// <c>IAppPolicyCache.Get</c> contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Detection semantics (v1):</b> a block is recorded when a
/// <c>User-agent</c> block matches an AI-bot token (or <c>*</c> — the
/// wildcard creates one finding per known AI-bot token) AND the block
/// contains a <c>Disallow: /</c> (full-site disallow). Partial-path
/// disallows (<c>Disallow: /private/</c>) are NOT flagged — a bot
/// blocked from one path can still index the rest of the site.
/// </para>
/// <para>
/// <b>Empty <c>Disallow:</c> (no value)</b> is the canonical "allow
/// everything" robots.txt directive and is NOT flagged.
/// </para>
/// <para>
/// <b>Refresh:</b> <see cref="Umbraco.Community.AiVisibility.Telemetry.RobotsAuditRefreshJob"/>
/// re-runs <see cref="AuditAsync"/> on the configured cadence and
/// rewrites the cached entry. The Health Check view reads the cache;
/// it does NOT trigger fetches itself except on cache miss.
/// </para>
/// </remarks>
public sealed class DefaultRobotsAuditor : IRobotsAuditor
{
    /// <summary>
    /// Named <see cref="HttpClient"/> registration used by the auditor. The
    /// composer (<c>RobotsComposer</c>) registers this name with
    /// <see cref="System.Net.Http.HttpClientHandler.AllowAutoRedirect"/> set
    /// to <c>false</c> so a hostile <c>/robots.txt</c> redirect cannot pull
    /// the auditor onto an unintended origin (e.g. cloud metadata endpoints).
    /// </summary>
    public const string HttpClientName = "AiVisibility.RobotsAudit";

    /// <summary>
    /// Hard cap on the response body the auditor will read. robots.txt files
    /// are bytes-to-kilobytes; capping at 1 MB protects against a misconfigured
    /// origin (or a redirect-chained-to-a-large-resource attack scenario)
    /// causing OOM via <see cref="HttpContent.ReadAsStringAsync()"/>.
    /// </summary>
    private const long MaxResponseBytes = 1024L * 1024L; // 1 MB

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppCaches _appCaches;
    private readonly AiBotList _aiBotList;
    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly ILogger<DefaultRobotsAuditor> _logger;
    private readonly TimeProvider _timeProvider;

    public DefaultRobotsAuditor(
        IHttpClientFactory httpClientFactory,
        AppCaches appCaches,
        AiBotList aiBotList,
        IOptionsMonitor<AiVisibilitySettings> settings,
        ILogger<DefaultRobotsAuditor> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _appCaches = appCaches;
        _aiBotList = aiBotList;
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<RobotsAuditResult> AuditAsync(
        string hostname,
        string scheme,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(hostname))
        {
            return Task.FromResult(new RobotsAuditResult(
                Hostname: string.Empty,
                Outcome: RobotsAuditOutcome.FetchFailed,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: _timeProvider.GetUtcNow().UtcDateTime,
                ErrorMessage: "Hostname is empty."));
        }

        var settings = _settings.CurrentValue.RobotsAuditor;
        // Cache key uses AiVisibilityCacheKeys.NormaliseHost (lowercase + port-strip)
        // so callers with casing variants share the entry; the URL builder
        // applies the same normalisation so cache lookups and fetched URIs
        // agree on the canonical host form.
        var cacheKey = AiVisibilityCacheKeys.Robots(hostname);
        // Clamp RefreshIntervalHours to [1, 8760] (one year) so int.MaxValue
        // doesn't overflow TimeSpan.FromHours. Refresh-disabled (≤ 0) uses
        // the lower bound 1h cache TTL — the recurring job won't rewrite it,
        // but the on-demand cache miss will.
        var ttl = TimeSpan.FromHours(Math.Clamp(settings.RefreshIntervalHours, 1, 24 * 365));
        // Clamp FetchTimeoutSeconds to [1, 60]: 1s lower bound is the smallest
        // practically useful HTTP timeout; 60s upper guards against an operator
        // typo (or int.MaxValue) leaving the auditor blocking a Backoffice
        // worker thread for hours.
        var fetchTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.FetchTimeoutSeconds, 1, 60));
        var resolvedScheme = string.IsNullOrWhiteSpace(scheme) ? "https" : scheme;

        // sync-over-async inside the factory — same shape Story 1.2's
        // CachingMarkdownExtractorDecorator uses. IAppPolicyCache.Get
        // serialises the factory per key. The caller's cancellationToken IS
        // forwarded to RunAsync so a request abort short-circuits the fetch
        // (the GetAwaiter().GetResult() unwraps any OperationCanceledException
        // from the linked CTS).
        var cached = _appCaches.RuntimeCache.Get(
            cacheKey,
            () => RunAsync(hostname, resolvedScheme, fetchTimeout, cancellationToken)
                .GetAwaiter().GetResult(),
            ttl,
            isSliding: false) as RobotsAuditResult;

        cancellationToken.ThrowIfCancellationRequested();

        if (cached is null)
        {
            // Cache returned null (rare — unexpected eviction during the
            // factory call, or a misconfigured cache adapter). Fall through
            // to a fresh fetch so the Health Check has SOMETHING to render.
            return RunAsync(hostname, resolvedScheme, fetchTimeout, cancellationToken);
        }

        return Task.FromResult(cached);
    }

    /// <summary>
    /// Force-refresh the audit for a host — bypasses the cache and rewrites
    /// the entry. Called by
    /// <see cref="Umbraco.Community.AiVisibility.Telemetry.RobotsAuditRefreshJob"/> and by
    /// <see cref="RobotsAuditHealthCheck.GetStatusAsync"/> (where the editor's
    /// intent is "show me current state").
    /// </summary>
    public async Task<RobotsAuditResult> RefreshAsync(
        string hostname,
        string scheme,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _settings.CurrentValue.RobotsAuditor;
        // Clamp FetchTimeoutSeconds to [1, 60]: 1s lower bound is the smallest
        // practically useful HTTP timeout; 60s upper guards against an operator
        // typo (or int.MaxValue) leaving the auditor blocking a Backoffice
        // worker thread for hours.
        var fetchTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.FetchTimeoutSeconds, 1, 60));
        var resolvedScheme = string.IsNullOrWhiteSpace(scheme) ? "https" : scheme;
        var cacheKey = AiVisibilityCacheKeys.Robots(hostname);
        // Clamp RefreshIntervalHours to [1, 8760] (one year) so int.MaxValue
        // doesn't overflow TimeSpan.FromHours. Refresh-disabled (≤ 0) uses
        // the lower bound 1h cache TTL — the recurring job won't rewrite it,
        // but the on-demand cache miss will.
        var ttl = TimeSpan.FromHours(Math.Clamp(settings.RefreshIntervalHours, 1, 24 * 365));

        var result = await RunAsync(hostname, resolvedScheme, fetchTimeout, cancellationToken)
            .ConfigureAwait(false);

        // Atomic write: Insert overwrites whatever's at the key, regardless of
        // whether a concurrent AuditAsync factory raced us to populate it.
        // The earlier ClearByKey + Get(key, () => result) pattern was racy —
        // a competing factory could populate the slot first, and Get's
        // factory lambda would silently no-op.
        _appCaches.RuntimeCache.Insert(cacheKey, () => result, ttl, isSliding: false);
        return result;
    }

    private async Task<RobotsAuditResult> RunAsync(
        string hostname,
        string scheme,
        TimeSpan fetchTimeout,
        CancellationToken cancellationToken)
    {
        // Match the cache-key normalisation (lowercase + port-strip) so the
        // URI we fetch is the same canonical host we cached against.
        var canonicalHost = AiVisibilityCacheKeys.NormaliseHost(hostname);
        var capturedAt = _timeProvider.GetUtcNow().UtcDateTime;

        // Reject schemes outside http/https — defends against a hostile
        // adopter override or settings drift passing in file://, gopher://,
        // ftp://, etc.
        if (!scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Robots audit: rejected unsupported scheme {Scheme} for host {Hostname}",
                scheme,
                canonicalHost);
            return new RobotsAuditResult(
                Hostname: canonicalHost,
                Outcome: RobotsAuditOutcome.FetchFailed,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: capturedAt,
                ErrorMessage: $"Unsupported scheme '{scheme}' — only http and https are honoured.");
        }

        // Reject hostnames carrying userinfo (user@host) or path components
        // (host/path) — the auditor fetches host+/robots.txt only, and any
        // such authority parts indicate adopter / IDomain configuration drift.
        if (canonicalHost.Contains('@') || canonicalHost.Contains('/'))
        {
            _logger.LogWarning(
                "Robots audit: rejected malformed hostname {Hostname} (contains userinfo or path)",
                canonicalHost);
            return new RobotsAuditResult(
                Hostname: canonicalHost,
                Outcome: RobotsAuditOutcome.FetchFailed,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: capturedAt,
                ErrorMessage: "Malformed hostname — userinfo or path components are not honoured.");
        }

        // SSRF defence: refuse to fetch from loopback / link-local / private
        // RFC1918 ranges. Catches the realistic scenario of an editor
        // accidentally configuring an IDomain at 169.254.169.254 (cloud
        // metadata) or a CDN/origin redirect chained at the same. Cross-origin
        // redirect-following is separately disabled at HttpClient handler
        // configuration time (see HttpClientName + composer wiring).
        if (IsBlockedHost(canonicalHost))
        {
            _logger.LogWarning(
                "Robots audit: rejected blocked-range hostname {Hostname} (loopback / link-local / private)",
                canonicalHost);
            return new RobotsAuditResult(
                Hostname: canonicalHost,
                Outcome: RobotsAuditOutcome.FetchFailed,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: capturedAt,
                ErrorMessage: "Refusing to fetch loopback / link-local / private-range host.");
        }

        Uri uri;
        try
        {
            var devPort = _settings.CurrentValue.RobotsAuditor.DevFetchPort;
            // UriBuilder handles IPv6 brackets cleanly and round-trips the
            // canonical host without string interpolation footguns.
            // DevFetchPort outside [1, 65535] is treated as unset (operator typo).
            var builder = devPort is { } port and > 0 and <= 65535
                ? new UriBuilder(scheme, canonicalHost, port, "/robots.txt")
                : new UriBuilder(scheme, canonicalHost) { Path = "/robots.txt" };
            uri = builder.Uri;
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(
                ex,
                "Robots audit: failed to compose URI for hostname {Hostname} scheme {Scheme}",
                canonicalHost,
                scheme);
            return new RobotsAuditResult(
                Hostname: canonicalHost,
                Outcome: RobotsAuditOutcome.FetchFailed,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: capturedAt,
                ErrorMessage: $"Invalid URI: {ex.Message}");
        }

        string body;
        try
        {
            // Named client — handler is composer-configured with
            // AllowAutoRedirect=false so a hostile redirect cannot pull the
            // auditor onto an unintended origin (e.g. cloud-metadata IPs).
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            client.MaxResponseContentBufferSize = MaxResponseBytes;

            using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            fetchCts.CancelAfter(fetchTimeout);

            using var response = await client
                .GetAsync(uri, fetchCts.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "Robots audit: {Hostname} returned 404 for /robots.txt — informational",
                    canonicalHost);
                return new RobotsAuditResult(
                    Hostname: canonicalHost,
                    Outcome: RobotsAuditOutcome.RobotsTxtMissing,
                    Findings: Array.Empty<RobotsAuditFinding>(),
                    CapturedAtUtc: capturedAt);
            }

            // Treat 3xx responses as fetch failures rather than following them.
            // Defence-in-depth alongside the AllowAutoRedirect=false handler
            // config — if the handler ever ships configured to follow redirects,
            // this guard still catches the cross-origin SSRF surface.
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                _logger.LogWarning(
                    "Robots audit: {Hostname} returned redirect status {StatusCode} — refusing to follow",
                    canonicalHost,
                    (int)response.StatusCode);
                return new RobotsAuditResult(
                    Hostname: canonicalHost,
                    Outcome: RobotsAuditOutcome.FetchFailed,
                    Findings: Array.Empty<RobotsAuditFinding>(),
                    CapturedAtUtc: capturedAt,
                    ErrorMessage: $"Refused to follow {(int)response.StatusCode} redirect — robots.txt should be served on the origin host.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Robots audit: {Hostname} returned non-success status {StatusCode}",
                    canonicalHost,
                    (int)response.StatusCode);
                return new RobotsAuditResult(
                    Hostname: canonicalHost,
                    Outcome: RobotsAuditOutcome.FetchFailed,
                    Findings: Array.Empty<RobotsAuditFinding>(),
                    CapturedAtUtc: capturedAt,
                    ErrorMessage: $"HTTP {(int)response.StatusCode} {response.StatusCode}.");
            }

            body = await response.Content
                .ReadAsStringAsync(fetchCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Robots audit: {Hostname} fetch timed out after {Timeout}",
                canonicalHost,
                fetchTimeout);
            return new RobotsAuditResult(
                Hostname: canonicalHost,
                Outcome: RobotsAuditOutcome.FetchFailed,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: capturedAt,
                ErrorMessage: $"Fetch timed out after {fetchTimeout.TotalSeconds:F0}s.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Robots audit: {Hostname} fetch failed",
                canonicalHost);
            return new RobotsAuditResult(
                Hostname: canonicalHost,
                Outcome: RobotsAuditOutcome.FetchFailed,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: capturedAt,
                ErrorMessage: ex.Message);
        }

        try
        {
            var findings = ParseAndMatch(body);
            var outcome = findings.Count > 0
                ? RobotsAuditOutcome.BlocksDetected
                : RobotsAuditOutcome.NoAiBlocks;
            return new RobotsAuditResult(
                Hostname: canonicalHost,
                Outcome: outcome,
                Findings: findings,
                CapturedAtUtc: capturedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Robots audit: {Hostname} parse failed",
                canonicalHost);
            return new RobotsAuditResult(
                Hostname: canonicalHost,
                Outcome: RobotsAuditOutcome.ParseFailed,
                Findings: Array.Empty<RobotsAuditFinding>(),
                CapturedAtUtc: capturedAt,
                ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// True when the host is a literal IP in a loopback / link-local /
    /// private RFC1918 range, or a hostname that resolves to such per
    /// case-insensitive comparison (e.g. <c>localhost</c>). Hostnames that
    /// require DNS resolution to classify (e.g. an arbitrary <c>foo.example</c>
    /// that resolves to 10.x) are NOT pre-resolved here — the threat model is
    /// "editor accidentally configured a literal internal IP / loopback name",
    /// not "active DNS rebinding attack" (which an HTTP fetch can't defend
    /// against without architectural-level network policy).
    /// </summary>
    internal static bool IsBlockedHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host == "_")
        {
            return false;
        }

        // Special hostname names.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // IPv6 may arrive bracketed (UriBuilder requires brackets); strip for IPAddress.TryParse.
        var trimmed = host;
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1];
        }

        if (!IPAddress.TryParse(trimmed, out var ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 — link-local incl. cloud metadata 169.254.169.254
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 caught by IsLoopback above; cover unique-local fc00::/7
            // and link-local fe80::/10.
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xfe) == 0xfc) return true; // fc00::/7
        }

        return false;
    }

    /// <summary>
    /// Tolerant robots.txt parser: groups directives by User-agent block,
    /// flags blocks containing <c>Disallow: /</c> as full-site blocks. The
    /// wildcard <c>User-agent: *</c> creates one finding per known AI-bot
    /// token (since the wildcard affects every crawler, including each AI
    /// bot in our list).
    /// </summary>
    /// <remarks>
    /// Internal so <c>DefaultRobotsAuditorTests</c> can pin parse behaviour
    /// without spinning up an HTTP harness.
    /// </remarks>
    internal IReadOnlyList<RobotsAuditFinding> ParseAndMatch(string body)
    {
        var findings = new List<RobotsAuditFinding>();
        if (string.IsNullOrWhiteSpace(body))
        {
            return findings;
        }

        // Group: each entry is a list of (line-trimmed) directives associated
        // with one or more User-agent declarations. Blank lines separate
        // groups per RFC 9309 § 2.2.1.
        var currentAgents = new List<string>();
        var currentDisallows = new List<string>();
        var alreadyMatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (currentAgents.Count == 0)
            {
                currentDisallows.Clear();
                return;
            }

            var hasFullDisallow = currentDisallows.Any(d => d == "/");
            if (!hasFullDisallow)
            {
                currentAgents.Clear();
                currentDisallows.Clear();
                return;
            }

            // Wildcard expansion — flag every AI bot in our list (deduped
            // against earlier explicit findings).
            if (currentAgents.Any(a => a == "*"))
            {
                foreach (var bot in _aiBotList.Entries)
                {
                    if (alreadyMatched.Add(bot.Token))
                    {
                        findings.Add(BuildFinding(bot, "User-agent: *", isWildcard: true));
                    }
                }
            }

            foreach (var agent in currentAgents)
            {
                if (agent == "*")
                {
                    continue;
                }

                var bot = _aiBotList.GetByToken(agent);
                if (bot is null)
                {
                    continue;
                }

                if (alreadyMatched.Add(bot.Token))
                {
                    findings.Add(BuildFinding(bot, $"User-agent: {agent}", isWildcard: false));
                }
            }

            currentAgents.Clear();
            currentDisallows.Clear();
        }

        var sawDirectiveSinceUserAgent = false;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Replace("\r", string.Empty).Trim();

            // Strip inline comments (RFC 9309 § 2.2.1: '#' starts a comment
            // through end of line).
            var hashIndex = line.IndexOf('#', StringComparison.Ordinal);
            if (hashIndex >= 0)
            {
                line = line[..hashIndex].Trim();
            }

            if (line.Length == 0)
            {
                // RFC 9309: blank line ends the current group ONLY if a
                // directive followed the User-agent declarations.
                if (sawDirectiveSinceUserAgent)
                {
                    Flush();
                    sawDirectiveSinceUserAgent = false;
                }
                continue;
            }

            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex <= 0)
            {
                // Malformed line — skip per parser tolerance.
                continue;
            }

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();

            if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                // A new User-agent after directives starts a new group;
                // flush + reset.
                if (sawDirectiveSinceUserAgent)
                {
                    Flush();
                    sawDirectiveSinceUserAgent = false;
                }
                if (!string.IsNullOrEmpty(value))
                {
                    currentAgents.Add(value);
                }
            }
            else if (key.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
            {
                currentDisallows.Add(value);
                sawDirectiveSinceUserAgent = true;
            }
            else
            {
                // Allow:, Crawl-delay:, Sitemap:, etc. — recognised
                // robots.txt directives we don't act on. Mark the group as
                // having directives so subsequent User-agent / blank lines
                // close it correctly.
                sawDirectiveSinceUserAgent = true;
            }
        }

        // Final group at EOF (no trailing blank line).
        Flush();

        return findings;
    }

    private static RobotsAuditFinding BuildFinding(AiBotEntry bot, string matchedHeader, bool isWildcard)
    {
        var matched = $"{matchedHeader}\nDisallow: /";
        var suggested = isWildcard
            ? $"# To allow {bot.Token} while keeping a default-block, add a more permissive\n# block AFTER the wildcard:\nUser-agent: {bot.Token}\nAllow: /"
            : $"# Remove the following lines to allow {bot.Token}:\nUser-agent: {bot.Token}\nDisallow: /";
        return new RobotsAuditFinding(bot, matched, suggested, bot.IsDeprecated);
    }
}
