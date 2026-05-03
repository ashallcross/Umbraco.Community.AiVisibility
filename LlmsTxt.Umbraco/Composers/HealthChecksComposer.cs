using System.Net;
using System.Net.Http;
using LlmsTxt.Umbraco.Background;
using LlmsTxt.Umbraco.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace LlmsTxt.Umbraco.Composers;

/// <summary>
/// Story 4.2 — wires the robots audit pipeline:
/// <list type="bullet">
/// <item><see cref="AiBotList"/> (Singleton — loads embedded list once).</item>
/// <item><see cref="IRobotsAuditor"/> → <see cref="DefaultRobotsAuditor"/>
/// (Singleton — stateless + thread-safe; cache-coordinated via
/// <see cref="Umbraco.Cms.Core.Cache.AppCaches"/>). Adopters override via
/// <c>services.AddSingleton&lt;IRobotsAuditor, MyImpl&gt;()</c>; we use
/// <c>TryAdd*</c> so the override path is honoured.</item>
/// <item><see cref="RobotsAuditHealthCheck"/> — auto-discovered by
/// Umbraco's <c>HealthCheckCollectionBuilder</c> via <c>TypeLoader</c>; we
/// register it as Transient so its scoped dependencies
/// (<see cref="Umbraco.Cms.Core.Services.IDomainService"/>) resolve
/// cleanly each render.</item>
/// <item><see cref="StartupRobotsAuditRunner"/> — Singleton via
/// <see cref="ServiceCollectionHostedServiceExtensions.AddHostedService"/>.</item>
/// <item><see cref="RobotsAuditRefreshJob"/> registered as
/// <see cref="IDistributedBackgroundJob"/> (Singleton — Umbraco's
/// <c>DistributedBackgroundJobHostedService</c> resolves the registration
/// list at startup).</item>
/// </list>
/// </summary>
public sealed class HealthChecksComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // TimeProvider is consumed by RobotsAuditRefreshJob's CycleStart log
        // line. .NET 8+ does NOT auto-register TimeProvider with the DI
        // container — register the system clock so DI resolution works without
        // adopters needing to wire it.
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services.TryAddSingleton<AiBotList>();
        builder.Services.TryAddSingleton<IRobotsAuditor, DefaultRobotsAuditor>();
        builder.Services.TryAddTransient<RobotsAuditHealthCheck>();
        builder.Services.AddHostedService<StartupRobotsAuditRunner>();
        builder.Services.AddSingleton<IDistributedBackgroundJob, RobotsAuditRefreshJob>();

        // SSRF defence: the named HttpClient used by DefaultRobotsAuditor
        // refuses to follow redirects so a hostile /robots.txt cannot pull the
        // auditor onto an unintended origin (e.g. cloud-metadata 169.254.169.254).
        // The auditor also rejects 3xx responses in-app as defence-in-depth, but
        // configuring the handler is the primary control.
        builder.Services
            .AddHttpClient(DefaultRobotsAuditor.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        // D2: enforce IRobotsAuditor lifetime is Singleton at composition time.
        // TryAddSingleton above no-ops if an adopter pre-registered the service
        // as Scoped or Transient — left unchecked, that captures a Scoped dep
        // into the Singleton RobotsAuditRefreshJob and the failure surfaces at
        // first refresh-cycle resolution rather than at composition time.
        var auditorRegistration = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(IRobotsAuditor));
        if (auditorRegistration is not null && auditorRegistration.Lifetime != ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                $"LlmsTxt: IRobotsAuditor must be registered as Singleton; found {auditorRegistration.Lifetime}. " +
                "Adopter overrides via services.AddSingleton<IRobotsAuditor, …>() are honoured (see HealthChecks/IRobotsAuditor.cs); " +
                "Scoped or Transient overrides would form a captive dependency in RobotsAuditRefreshJob.");
        }
    }
}
