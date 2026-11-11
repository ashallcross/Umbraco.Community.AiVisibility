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
}
