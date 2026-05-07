using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Umbraco.Community.AiVisibility.Tests.Configuration;

[TestFixture]
public class LogRetentionSettingsValidatorTests
{
    [Test]
    public void Validate_DefaultSettings_ReturnsSuccess()
    {
        var validator = new LogRetentionSettingsValidator();
        var result = validator.Validate(null, new AiVisibilitySettings());
        Assert.That(result.Succeeded, Is.True,
            $"defaults should pass; failures: {string.Join("; ", result.Failures ?? Array.Empty<string>())}");
    }

    [Test]
    public void Validate_DurationDaysDisableShape_ReturnsSuccess()
    {
        var validator = new LogRetentionSettingsValidator();
        foreach (var disableValue in new[] { 0, -1, -100 })
        {
            var settings = new AiVisibilitySettings
            {
                LogRetention = new LogRetentionSettings { DurationDays = disableValue },
            };
            var result = validator.Validate(null, settings);
            Assert.That(result.Succeeded, Is.True,
                $"DurationDays={disableValue} (documented disable shape) should pass");
        }
    }

    [Test]
    public void Validate_DurationDaysAboveCeiling_ReturnsFail()
    {
        var validator = new LogRetentionSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            LogRetention = new LogRetentionSettings { DurationDays = 3651 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("DurationDays").And.Contains("3651"));
    }

    [Test]
    public void Validate_RunIntervalHoursAboveCeiling_ReturnsFail()
    {
        var validator = new LogRetentionSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            LogRetention = new LogRetentionSettings { RunIntervalHours = 8761 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("RunIntervalHours"));
    }

    [Test]
    public void Validate_RunIntervalSecondsOverrideUnsetShape_ReturnsSuccess()
    {
        var validator = new LogRetentionSettingsValidator();
        foreach (var unsetValue in new int?[] { null, 0, -1 })
        {
            var settings = new AiVisibilitySettings
            {
                LogRetention = new LogRetentionSettings { RunIntervalSecondsOverride = unsetValue },
            };
            var result = validator.Validate(null, settings);
            Assert.That(result.Succeeded, Is.True,
                $"RunIntervalSecondsOverride={unsetValue?.ToString() ?? "null"} (documented unset shape) should pass");
        }
    }

    [Test]
    public void Validate_RunIntervalSecondsOverrideAboveCeiling_ReturnsFail()
    {
        var validator = new LogRetentionSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            LogRetention = new LogRetentionSettings { RunIntervalSecondsOverride = 86401 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("RunIntervalSecondsOverride"));
    }

    [Test]
    public void Compose_LogRetentionSettingsValidator_RegisteredAsSingleton_AppendedNotReplaced()
    {
        var services = new ServiceCollection();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AiVisibilitySettings>, StubValidator>());
        var baselineCount = services.Count(d => d.ServiceType == typeof(IValidateOptions<AiVisibilitySettings>));
        Assert.That(baselineCount, Is.EqualTo(1));

        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AiVisibilitySettings>, LogRetentionSettingsValidator>());

        var afterCount = services.Count(d => d.ServiceType == typeof(IValidateOptions<AiVisibilitySettings>));
        Assert.That(afterCount, Is.EqualTo(2),
            "TryAddEnumerable must append, not replace; if this asserts at 1, the registration regressed to TryAddSingleton.");
    }

    [Test]
    public void Compose_StartupValidation_LogRetentionSettingsValidator_NoCaptiveDependency()
    {
        var services = new ServiceCollection();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AiVisibilitySettings>, LogRetentionSettingsValidator>());
        services.AddOptions<AiVisibilitySettings>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        var validator = provider.GetServices<IValidateOptions<AiVisibilitySettings>>()
            .OfType<LogRetentionSettingsValidator>()
            .Single();
        Assert.That(validator, Is.Not.Null);
    }

    private sealed class StubValidator : IValidateOptions<AiVisibilitySettings>
    {
        public ValidateOptionsResult Validate(string? name, AiVisibilitySettings options)
            => ValidateOptionsResult.Success;
    }
}
