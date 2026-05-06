using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;

namespace Umbraco.Community.AiVisibility.Robots;

/// <summary>
/// Story 4.2 — fires the robots audit once per bound hostname during host
/// startup, gated on <see cref="AiVisibilitySettings.RobotsAuditOnStartup"/>.
/// Defensively gated on <see cref="IServerRoleAccessor"/> so multi-instance
/// front-end servers don't all hammer their own <c>/robots.txt</c> at boot;
/// the canonical exactly-once guarantee for the recurring refresh remains
/// the <see cref="LlmsTxt.Umbraco.Background.RobotsAuditRefreshJob"/>'s
/// <c>IDistributedBackgroundJob</c> host-DB-lock coordination.
/// </summary>
public sealed class StartupRobotsAuditRunner : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AiVisibilitySettings> _settings;
    private readonly IServerRoleAccessor _serverRoleAccessor;
    private readonly ILogger<StartupRobotsAuditRunner> _logger;

    public StartupRobotsAuditRunner(
        IServiceProvider services,
        IOptionsMonitor<AiVisibilitySettings> settings,
        IServerRoleAccessor serverRoleAccessor,
        ILogger<StartupRobotsAuditRunner> logger)
    {
        _services = services;
        _settings = settings;
        _serverRoleAccessor = serverRoleAccessor;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.CurrentValue.RobotsAuditOnStartup)
        {
            _logger.LogTrace(
                "Startup robots audit suppressed (LlmsTxt:RobotsAuditOnStartup is false).");
            return Task.CompletedTask;
        }

        // Only run on instances that own scheduled work — multi-instance
        // setups will otherwise have N front-ends each fetching their own
        // /robots.txt at boot.
        var role = _serverRoleAccessor.CurrentServerRole;
        if (role != ServerRole.SchedulingPublisher && role != ServerRole.Single)
        {
            _logger.LogTrace(
                "Startup robots audit suppressed on server role {Role}.",
                role);
            return Task.CompletedTask;
        }

        // Run the audit OUTSIDE the StartAsync window so app boot is never
        // blocked. ASP.NET Core treats StartAsync exceptions as fatal — a
        // transient failure here would prevent the host from starting.
        // Fire-and-forget on the thread pool with an outer try/catch so any
        // unexpected throw is logged, not propagated.
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAuditAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host stopped during boot — quiet exit.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup robots audit: unhandled exception");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    private async Task RunAuditAsync(CancellationToken cancellationToken)
    {
        // Resolve scoped services in a fresh scope. IHostedService is
        // singleton; the auditor is singleton but its support services
        // (IDomainService) are scoped.
        using var scope = _services.CreateScope();
        var auditor = scope.ServiceProvider.GetRequiredService<IRobotsAuditor>();
        var domainService = scope.ServiceProvider.GetRequiredService<IDomainService>();

        var hostnames = ResolveHostnames(domainService, _logger);
        if (hostnames.Count == 0)
        {
            _logger.LogTrace(
                "Startup robots audit: no hostnames to audit (no IDomain bindings).");
            return;
        }

        foreach (var (hostname, scheme) in hostnames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await auditor
                    .AuditAsync(hostname, scheme, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Startup robots audit: {Hostname} → {Outcome} ({Findings} finding(s))",
                    hostname,
                    result.Outcome,
                    result.Findings.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Startup robots audit: failed for hostname {Hostname}",
                    hostname);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Internal so tests can pin the dedup walk shape.
    /// </summary>
    internal static IReadOnlyList<(string Host, string Scheme)> ResolveHostnames(
        IDomainService domainService,
        ILogger logger)
    {
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
#pragma warning disable CS0618
            foreach (var domain in domainService.GetAll(includeWildcards: true))
#pragma warning restore CS0618
            {
                if (string.IsNullOrWhiteSpace(domain.DomainName))
                {
                    continue;
                }

                if (!Uri.TryCreate(domain.DomainName, UriKind.Absolute, out var absolute) &&
                    !Uri.TryCreate($"https://{domain.DomainName}", UriKind.Absolute, out absolute))
                {
                    logger.LogTrace(
                        "Startup robots audit: skipping IDomain {DomainName} — not parseable as a URL",
                        domain.DomainName);
                    continue;
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
            // IDomainService can throw transient DB errors at boot;
            // the recurring refresh will pick up later. LogTrace so a permanent
            // failure is observable in verbose logs without spamming Warning.
            logger.LogTrace(
                ex,
                "Startup robots audit: IDomainService.GetAll failed; this boot will not audit");
        }

        return hosts.Select(kv => (kv.Key, kv.Value)).ToArray();
    }
}
