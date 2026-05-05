using LlmsTxt.Umbraco.Background;
using LlmsTxt.Umbraco.Configuration;
using Umbraco.Community.AiVisibility.Robots;
using LlmsTxt.Umbraco.Notifications;
using Umbraco.Community.AiVisibility.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace LlmsTxt.Umbraco.Composers;

/// <summary>
/// Story 5.1 — wires the notifications + request-log + retention pipeline:
/// <list type="bullet">
/// <item><see cref="AiBotList"/> Singleton (already registered by Story 4.2's
/// <see cref="HealthChecksComposer"/>; we <c>TryAdd*</c> to coexist).</item>
/// <item><see cref="IUserAgentClassifier"/> →
/// <see cref="DefaultUserAgentClassifier"/> Singleton.</item>
/// <item><see cref="IRequestLog"/> →
/// <see cref="DefaultRequestLog"/> Singleton (process-wide bounded
/// channel).</item>
/// <item><see cref="LlmsRequestLogDrainHostedService"/> as
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/>.</item>
/// <item><see cref="LogRetentionJob"/> as
/// <see cref="IDistributedBackgroundJob"/>.</item>
/// <item>Three <see cref="DefaultLlmsRequestLogHandler"/> registrations —
/// one per notification, via Umbraco's
/// <c>AddNotificationAsyncHandler&lt;TNotification, THandler&gt;</c>
/// extension which uses Scoped lifetime.</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Composer-time hard-validation (AC6):</b> if an adopter pre-registers
/// <see cref="IRequestLog"/> with a non-Singleton lifetime, this
/// composer throws <see cref="InvalidOperationException"/> at composition
/// time. Mirrors Story 4.2 <see cref="HealthChecksComposer"/>'s
/// <see cref="HealthChecks.IRobotsAuditor"/> Singleton-only validation
/// (chunk-3 D2 ratification).
/// </remarks>
public sealed class NotificationsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // TimeProvider.System — .NET 8+ doesn't auto-register; mirror
        // HealthChecksComposer's pattern (Story 4.2 chunk-1 P3).
        builder.Services.TryAddSingleton(TimeProvider.System);

        // AiBotList may already be registered by HealthChecksComposer —
        // TryAdd*'s no-op-on-existing semantics make us order-tolerant.
        builder.Services.TryAddSingleton<AiBotList>();

        builder.Services.TryAddSingleton<IUserAgentClassifier, DefaultUserAgentClassifier>();
        builder.Services.TryAddSingleton<ILlmsNotificationPublisher, DefaultLlmsNotificationPublisher>();
        builder.Services.TryAddSingleton<IRequestLog, DefaultRequestLog>();

        // Story 5.2 — analytics read path. Singleton because
        // DefaultAnalyticsReader is stateless (constructs scopes on
        // demand) and the controller it serves is Transient. TryAdd* honours
        // adopter-provided substitutes (testability seam — not a documented
        // public extension point per Story 5.2 § What NOT to Build).
        builder.Services.TryAddSingleton<IAnalyticsReader, DefaultAnalyticsReader>();

        // Story 5.2 code-review P11 — startup validation for the Analytics
        // settings sub-block. Surfaces operator typos at first config read
        // instead of letting Math.Max defences silently coerce values.
        builder.Services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<LlmsTxtSettings>, LlmsTxtSettingsValidator>());

        builder.Services.AddHostedService<LlmsRequestLogDrainHostedService>();
        builder.Services.AddSingleton<IDistributedBackgroundJob, LogRetentionJob>();

        builder.AddNotificationAsyncHandler<
            MarkdownPageRequestedNotification,
            DefaultLlmsRequestLogHandler>();
        builder.AddNotificationAsyncHandler<
            LlmsTxtRequestedNotification,
            DefaultLlmsRequestLogHandler>();
        builder.AddNotificationAsyncHandler<
            LlmsFullTxtRequestedNotification,
            DefaultLlmsRequestLogHandler>();

        // Composer-time hard-validation (AC6) — Story 4.2
        // HealthChecksComposer line 69 precedent. TryAddSingleton above
        // no-ops if an adopter pre-registered the service as Scoped or
        // Transient — left unchecked, that captures a Scoped dep into
        // the Singleton DefaultLlmsRequestLogHandler chain (and hence
        // the Singleton drainer's static reference to the channel).
        var requestLogRegistration = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(IRequestLog));
        if (requestLogRegistration is not null
            && requestLogRegistration.Lifetime != ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                $"LlmsTxt: IRequestLog must be registered as Singleton; found {requestLogRegistration.Lifetime}. " +
                "Adopter overrides via services.AddSingleton<IRequestLog, ...>() are honoured (see Persistence/IRequestLog.cs); " +
                "Scoped or Transient overrides would form a captive dependency in the request-log drainer.");
        }
    }
}
