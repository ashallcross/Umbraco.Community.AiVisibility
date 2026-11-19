using Umbraco.Community.AiVisibility.Persistence.Migrations;

namespace Umbraco.Community.AiVisibility.Tests.Persistence.Migrations;

[TestFixture]
public class AiVisibilityPackageMigrationPlanTests
{
    [Test]
    public void DefinePlan_StepsRunInShippedOrder_WithTabMoveLast()
    {
        var plan = new AiVisibilityPackageMigrationPlan();

        var steps = new List<Type>();
        var state = plan.InitialState;
        while (plan.Transitions.TryGetValue(state, out var transition) && transition is not null)
        {
            steps.Add(transition.MigrationType);
            state = transition.TargetState;
        }

        Assert.That(steps, Is.EqualTo(new[]
        {
            typeof(CreateAiVisibilitySettingsDoctype),
            typeof(AddRequestLogTable_1_0),
            typeof(RenameRequestLogTable_2_0),
            typeof(MoveExclusionGroupIntoTab_1_3),
        }));
        Assert.That(plan.FinalState, Is.EqualTo("7C2E9A41-5B3D-4E8F-A6C1-0D4B8F2E7A93").IgnoreCase);
        Assert.That(state, Is.EqualTo(plan.FinalState));
    }

    [Test]
    public void DefinePlan_EveryShippedStateId_IsUnchanged()
    {
        // Umbraco records the last executed state ID per install. Editing any
        // shipped ID (not just the final one) makes upgraded installs re-run
        // or skip steps, so every intermediate target state is pinned here.
        var plan = new AiVisibilityPackageMigrationPlan();

        var states = new List<string>();
        var state = plan.InitialState;
        while (plan.Transitions.TryGetValue(state, out var transition) && transition is not null)
        {
            states.Add(transition.TargetState.ToUpperInvariant());
            state = transition.TargetState;
        }

        Assert.That(states, Is.EqualTo(new[]
        {
            "A4F2C1E7-8B5D-4A3E-9F1C-2D8E5B7C0A6F",
            "9B3D7E4A-2C8F-4F1B-A5E0-7D9B2A6F1C8E",
            "4F3B2A8C-1D5E-4F7A-9B6C-8E0D2F4A6B1C",
            "7C2E9A41-5B3D-4E8F-A6C1-0D4B8F2E7A93",
        }));
    }
}
