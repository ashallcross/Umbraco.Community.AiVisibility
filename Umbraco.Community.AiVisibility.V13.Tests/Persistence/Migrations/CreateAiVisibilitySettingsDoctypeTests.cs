using Microsoft.Extensions.Configuration;
using Umbraco.Community.AiVisibility.V13.Persistence.Migrations;

namespace Umbraco.Community.AiVisibility.V13.Tests.Persistence.Migrations;

[TestFixture]
public sealed class CreateAiVisibilitySettingsDoctypeTests
{
    [Test]
    public void ShouldSkipSettingsDoctype_True_ReturnsTrue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiVisibility:Migrations:SkipSettingsDoctype"] = "true",
            })
            .Build();

        Assert.That(CreateAiVisibilitySettingsDoctype.ShouldSkipSettingsDoctype(configuration), Is.True);
    }

    [Test]
    public void ShouldSkipSettingsDoctype_Missing_ReturnsFalse()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.That(CreateAiVisibilitySettingsDoctype.ShouldSkipSettingsDoctype(configuration), Is.False);
    }
}
