using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Umbraco.Community.AiVisibility.Tests.Configuration;

[TestFixture]
public class RobotsAuditorSettingsValidatorTests
{
    [Test]
    public void Validate_DefaultSettings_ReturnsSuccess()
    {
        var validator = new RobotsAuditorSettingsValidator();
        var result = validator.Validate(null, new AiVisibilitySettings());
        Assert.That(result.Succeeded, Is.True,
            $"defaults should pass; failures: {string.Join("; ", result.Failures ?? Array.Empty<string>())}");
    }

    [Test]
    public void Validate_RefreshIntervalHoursDisableShape_ReturnsSuccess()
    {
        var validator = new RobotsAuditorSettingsValidator();
        foreach (var disableValue in new[] { 0, -1, -100 })
        {
            var settings = new AiVisibilitySettings
            {
                RobotsAuditor = new RobotsAuditorSettings { RefreshIntervalHours = disableValue },
            };
            var result = validator.Validate(null, settings);
            Assert.That(result.Succeeded, Is.True,
                $"RefreshIntervalHours={disableValue} (documented disable shape) should pass");
        }
    }

    [Test]
    public void Validate_RefreshIntervalHoursAboveCeiling_ReturnsFail()
    {
        var validator = new RobotsAuditorSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            RobotsAuditor = new RobotsAuditorSettings { RefreshIntervalHours = 8761 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("RefreshIntervalHours"));
    }

    [Test]
    public void Validate_FetchTimeoutSecondsBelowMinimum_ReturnsFail()
    {
        var validator = new RobotsAuditorSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            RobotsAuditor = new RobotsAuditorSettings { FetchTimeoutSeconds = 0 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("FetchTimeoutSeconds"));
    }

    [Test]
    public void Validate_DevFetchPortNullOrInRange_ReturnsSuccess()
    {
        var validator = new RobotsAuditorSettingsValidator();
        foreach (var validValue in new int?[] { null, 1, 80, 443, 44314, 65535 })
        {
            var settings = new AiVisibilitySettings
            {
                RobotsAuditor = new RobotsAuditorSettings { DevFetchPort = validValue },
            };
            var result = validator.Validate(null, settings);
            Assert.That(result.Succeeded, Is.True,
                $"DevFetchPort={validValue?.ToString() ?? "null"} (in TCP port range or unset) should pass");
        }
    }

    [Test]
    public void Validate_DevFetchPortOutsideTcpRange_ReturnsFail()
    {
        var validator = new RobotsAuditorSettingsValidator();
        foreach (var invalidValue in new[] { 0, -1, 65536, 99999 })
        {
            var settings = new AiVisibilitySettings
            {
                RobotsAuditor = new RobotsAuditorSettings { DevFetchPort = invalidValue },
            };
            var result = validator.Validate(null, settings);
            Assert.That(result.Succeeded, Is.False, $"DevFetchPort={invalidValue} should fail");
            Assert.That(result.Failures!.First(), Does.Contain("DevFetchPort"));
        }
    }

    [Test]
    public void Validate_RefreshIntervalSecondsOverrideAboveCeiling_ReturnsFail()
    {
        var validator = new RobotsAuditorSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            RobotsAuditor = new RobotsAuditorSettings { RefreshIntervalSecondsOverride = 86401 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("RefreshIntervalSecondsOverride"));
    }

    [Test]
    public void Compose_RobotsAuditorSettingsValidator_RegisteredAsSingleton_AppendedNotReplaced()
    {
        var services = new ServiceCollection();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AiVisibilitySettings>, StubValidator>());
        var baselineCount = services.Count(d => d.ServiceType == typeof(IValidateOptions<AiVisibilitySettings>));
        Assert.That(baselineCount, Is.EqualTo(1));

        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AiVisibilitySettings>, RobotsAuditorSettingsValidator>());

        var afterCount = services.Count(d => d.ServiceType == typeof(IValidateOptions<AiVisibilitySettings>));
        Assert.That(afterCount, Is.EqualTo(2),
            "TryAddEnumerable must append, not replace; if this asserts at 1, the registration regressed to TryAddSingleton.");
    }

    [Test]
    public void Compose_StartupValidation_RobotsAuditorSettingsValidator_NoCaptiveDependency()
    {
        var services = new ServiceCollection();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AiVisibilitySettings>, RobotsAuditorSettingsValidator>());
        services.AddOptions<AiVisibilitySettings>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        var validator = provider.GetServices<IValidateOptions<AiVisibilitySettings>>()
            .OfType<RobotsAuditorSettingsValidator>()
            .Single();
        Assert.That(validator, Is.Not.Null);
    }

    private sealed class StubValidator : IValidateOptions<AiVisibilitySettings>
    {
        public ValidateOptionsResult Validate(string? name, AiVisibilitySettings options)
            => ValidateOptionsResult.Success;
    }
}
