using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Umbraco.Community.AiVisibility.Configuration;

/// <summary>
/// Story 6.0c follow-up — startup-once probe that scans
/// <see cref="IConfiguration"/> for residual <c>LlmsTxt:</c> keys left over
/// from before the Story 6.0c package rename. Story 6.0c renamed the
/// configuration section from <c>LlmsTxt:</c> to <c>AiVisibility:</c>; the
/// spec's pre-1.0 "no shim" principle (AC6) means the package does NOT
/// read or honour the old section. This probe does NOT read or honour
/// the old keys either — it only emits a single
/// <see cref="LogLevel.Warning"/> line listing the stale keys so adopters
/// using container envvar overrides (e.g. <c>LlmsTxt__RequestLog__Enabled</c>)
/// don't fail silently.
/// </summary>
internal sealed class LegacyConfigurationProbe : IHostedService
{
    private const string LegacyPrefix = "LlmsTxt:";

    private readonly IConfiguration _configuration;
    private readonly ILogger<LegacyConfigurationProbe> _logger;

    public LegacyConfigurationProbe(
        IConfiguration configuration,
        ILogger<LegacyConfigurationProbe> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Filter to leaf bindings — IConfiguration.AsEnumerable() also
        // surfaces section paths with null values (e.g. the section root
        // "LlmsTxt"), which we don't want to flag.
        var legacyKeys = _configuration.AsEnumerable()
            .Where(kvp => kvp.Key.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
            .Select(kvp => kvp.Key)
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (legacyKeys.Length > 0)
        {
            _logger.LogWarning(
                "AiVisibility: detected {Count} legacy 'LlmsTxt:' configuration value(s) that are no longer read post-Story-6.0c rename. " +
                "These overrides will silently fall back to defaults. Rename to 'AiVisibility:' (envvar prefix 'AiVisibility__'). Stale keys: [{Keys}].",
                legacyKeys.Length,
                string.Join(", ", legacyKeys));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
