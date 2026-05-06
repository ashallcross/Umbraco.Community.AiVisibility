using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Umbraco.Community.AiVisibility.Configuration;

namespace Umbraco.Community.AiVisibility.Tests.Configuration;

/// <summary>
/// Story 6.0c follow-up — pins the warn-loud probe's behaviour. The probe
/// runs at host startup, scans <see cref="IConfiguration"/> for stale
/// <c>LlmsTxt:</c> keys left over from before the Story 6.0c rename, and
/// emits a single <see cref="LogLevel.Warning"/> line listing them. It does
/// NOT read or honour the old keys (no shim per AC6).
/// </summary>
[TestFixture]
public class LegacyConfigurationProbeTests
{
    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries)
            .Build();

    [Test]
    public async Task StartAsync_NoLegacyKeys_LogsNothing()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["AiVisibility:RequestLog:Enabled"] = "true",
            ["AiVisibility:LlmsTxtBuilder:CachePolicySeconds"] = "3600",
        });
        var logger = Substitute.For<ILogger<LegacyConfigurationProbe>>();
        var probe = new LegacyConfigurationProbe(config, logger);

        await probe.StartAsync(CancellationToken.None);

        // No warning emitted when adopter config is clean.
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>()!);
    }

    [Test]
    public async Task StartAsync_LegacyKeyPresent_LogsSingleWarningListingStaleKeys()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["LlmsTxt:RequestLog:Enabled"] = "false",
            ["LlmsTxt:LogRetention:RetainDays"] = "7",
            ["AiVisibility:RequestLog:Enabled"] = "true",
        });
        var logger = Substitute.For<ILogger<LegacyConfigurationProbe>>();
        var probe = new LegacyConfigurationProbe(config, logger);

        await probe.StartAsync(CancellationToken.None);

        // Exactly one warning emitted, listing both stale keys (alphabetical
        // order via OrdinalIgnoreCase). The probe MUST NOT read the value;
        // verify by message-shape assertion only.
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o =>
                o!.ToString()!.Contains("legacy 'LlmsTxt:'", StringComparison.Ordinal)
                && o.ToString()!.Contains("LlmsTxt:LogRetention:RetainDays", StringComparison.Ordinal)
                && o.ToString()!.Contains("LlmsTxt:RequestLog:Enabled", StringComparison.Ordinal)),
            null,
            Arg.Any<Func<object, Exception?, string>>()!);
    }
}
