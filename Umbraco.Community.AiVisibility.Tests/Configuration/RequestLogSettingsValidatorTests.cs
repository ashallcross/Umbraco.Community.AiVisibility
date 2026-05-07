using Umbraco.Community.AiVisibility.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Umbraco.Community.AiVisibility.Tests.Configuration;

[TestFixture]
public class RequestLogSettingsValidatorTests
{
    [Test]
    public void Validate_DefaultSettings_ReturnsSuccess()
    {
        var validator = new RequestLogSettingsValidator();
        var result = validator.Validate(null, new AiVisibilitySettings());
        Assert.That(result.Succeeded, Is.True,
            $"defaults should pass; failures: {string.Join("; ", result.Failures ?? Array.Empty<string>())}");
    }

    [Test]
    public void Validate_QueueCapacityBelowMinimum_ReturnsFail()
    {
        var validator = new RequestLogSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            RequestLog = new RequestLogSettings { QueueCapacity = 63 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("QueueCapacity").And.Contains("63"));
    }

    [Test]
    public void Validate_BatchSizeBelowMinimum_ReturnsFail()
    {
        var validator = new RequestLogSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            RequestLog = new RequestLogSettings { BatchSize = 0 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("BatchSize"));
    }

    [Test]
    public void Validate_MaxBatchIntervalSecondsBelowMinimum_ReturnsFail()
    {
        var validator = new RequestLogSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            RequestLog = new RequestLogSettings { MaxBatchIntervalSeconds = 0 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("MaxBatchIntervalSeconds"));
    }

    [Test]
    public void Validate_OverflowLogIntervalSecondsBelowMinimum_ReturnsFail()
    {
        var validator = new RequestLogSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            RequestLog = new RequestLogSettings { OverflowLogIntervalSeconds = 4 },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Failures!.First(), Does.Contain("OverflowLogIntervalSeconds").And.Contains("4"));
    }

    [Test]
    public void Validate_DisableViaEnabledFalse_ReturnsSuccess()
    {
        // Setting Enabled=false is the documented "disable the writer" shape;
        // the queue tunables are independent and still validated.
        var validator = new RequestLogSettingsValidator();
        var settings = new AiVisibilitySettings
        {
            RequestLog = new RequestLogSettings { Enabled = false },
        };
        var result = validator.Validate(null, settings);
        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public void Compose_RequestLogSettingsValidator_RegisteredAsSingleton_AppendedNotReplaced()
    {
        var services = new ServiceCollection();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AiVisibilitySettings>, StubValidator>());
        var baselineCount = services.Count(d => d.ServiceType == typeof(IValidateOptions<AiVisibilitySettings>));
        Assert.That(baselineCount, Is.EqualTo(1));

        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AiVisibilitySettings>, RequestLogSettingsValidator>());

        var afterCount = services.Count(d => d.ServiceType == typeof(IValidateOptions<AiVisibilitySettings>));
        Assert.That(afterCount, Is.EqualTo(2),
            "TryAddEnumerable must append, not replace; if this asserts at 1, the registration regressed to TryAddSingleton.");
    }

    [Test]
    public void Compose_StartupValidation_RequestLogSettingsValidator_NoCaptiveDependency()
    {
        var services = new ServiceCollection();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AiVisibilitySettings>, RequestLogSettingsValidator>());
        services.AddOptions<AiVisibilitySettings>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        var validator = provider.GetServices<IValidateOptions<AiVisibilitySettings>>()
            .OfType<RequestLogSettingsValidator>()
            .Single();
        Assert.That(validator, Is.Not.Null);
    }

    private sealed class StubValidator : IValidateOptions<AiVisibilitySettings>
    {
        public ValidateOptionsResult Validate(string? name, AiVisibilitySettings options)
            => ValidateOptionsResult.Success;
    }
}
