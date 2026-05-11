using Umbraco.Community.AiVisibility.Configuration;

namespace Umbraco.Community.AiVisibility.Tests.Configuration;

/// <summary>
/// Story 7.3 AC8 — pins the C# property initialiser default for
/// <see cref="RenderStrategySettings.Mode"/>. The default flipped from
/// <see cref="RenderStrategyMode.Razor"/> (Stories 7.1 + 7.2) to
/// <see cref="RenderStrategyMode.Auto"/> in Story 7.3; this fixture guards
/// against accidental future revert.
/// </summary>
[TestFixture]
public class RenderStrategySettingsTests
{
    [Test]
    public void DefaultMode_IsAuto()
    {
        var settings = new RenderStrategySettings();

        Assert.That(settings.Mode, Is.EqualTo(RenderStrategyMode.Auto),
            "default Mode must be Auto — Story 7.3 flipped this from Razor; reverting silently regresses "
            + "the Epic 7 release-default contract (adopters who upgrade through Epic 7 see Auto OOTB)");
    }
}
