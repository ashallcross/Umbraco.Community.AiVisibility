using LlmsTxt.Umbraco.Persistence.Entities;

namespace LlmsTxt.Umbraco.Persistence;

/// <summary>
/// Story 5.1 — extension point for the package's request log writer.
/// Subscribed to all three notifications via the package's default
/// <see cref="LlmsTxt.Umbraco.Notifications.DefaultLlmsRequestLogHandler"/>;
/// adopters override with their own writer to push to App Insights /
/// Serilog / a custom table / etc.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime: Singleton.</b> The default implementation
/// (<see cref="DefaultLlmsRequestLog"/>) owns a process-wide bounded
/// <c>System.Threading.Channels.Channel&lt;LlmsTxtRequestLogEntry&gt;</c>;
/// the drainer reads from it. <see cref="LlmsTxt.Umbraco.Composers.NotificationsComposer"/>
/// throws <see cref="InvalidOperationException"/> at composition time if
/// any registered <c>ILlmsRequestLog</c> has a non-Singleton lifetime
/// (Story 4.2 chunk-3 D2 ratification — same shape as the
/// <c>IRobotsAuditor</c> Singleton requirement).
/// </para>
/// <para>
/// Adopters who want a Scoped writer wrap the Scoped impl in a Singleton
/// facade (documented in <c>docs/extension-points.md</c>).
/// </para>
/// </remarks>
public interface ILlmsRequestLog
{
    /// <summary>
    /// Enqueue a request-log entry for asynchronous batch persistence.
    /// Returns immediately — the actual DB write happens on the
    /// background drainer.
    /// </summary>
    /// <param name="entry">The populated entry. Caller-owned; the writer
    /// retains a reference inside its bounded queue.</param>
    /// <param name="cancellationToken">Honoured at queue-time. The drainer
    /// uses its own lifetime CTS for the actual DB write — caller cancellation
    /// stops only the enqueue.</param>
    Task EnqueueAsync(LlmsTxtRequestLogEntry entry, CancellationToken cancellationToken);
}
