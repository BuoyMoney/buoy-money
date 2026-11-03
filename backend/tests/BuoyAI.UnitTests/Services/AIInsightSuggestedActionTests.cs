using BuoyAI.Core.DTOs.AI;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The structured SuggestedAction is persisted inside the free-form AIInsight.Data JSON
/// (no migration). These lock in that round-trip so a navigate/handoff action survives save+load.
/// </summary>
public class AIInsightSuggestedActionTests
{
    [Fact]
    public void SerializeData_NoDataNoAction_ReturnsNull()
    {
        AIInsightGenerationService.SerializeData(null, null).Should().BeNull();
    }

    [Fact]
    public void RoundTrip_NavigateAction_IsPreserved()
    {
        var action = new SuggestedAction
        {
            Type = SuggestedAction.TypeNavigate,
            Label = "Review these transactions",
            Route = "/transactions",
        };

        var json = AIInsightGenerationService.SerializeData(null, action);
        var (data, restored) = AIInsightGenerationService.DeserializeData(json);

        data.Should().BeNull(); // no extra data, only the action
        restored.Should().NotBeNull();
        restored!.Type.Should().Be(SuggestedAction.TypeNavigate);
        restored.Label.Should().Be("Review these transactions");
        restored.Route.Should().Be("/transactions");
    }

    [Fact]
    public void RoundTrip_AgentHandoff_IsPreserved()
    {
        var action = new SuggestedAction
        {
            Type = SuggestedAction.TypeAgentHandoff,
            Label = "Fix categories",
            Instruction = "Recategorize the 3 unreviewed Robinhood debits as Transfer",
            Summary = "Marks 3 transactions as transfers",
        };

        var json = AIInsightGenerationService.SerializeData(null, action);
        var (_, restored) = AIInsightGenerationService.DeserializeData(json);

        restored.Should().NotBeNull();
        restored!.Type.Should().Be(SuggestedAction.TypeAgentHandoff);
        restored.Instruction.Should().Be("Recategorize the 3 unreviewed Robinhood debits as Transfer");
        restored.Summary.Should().Be("Marks 3 transactions as transfers");
    }

    [Fact]
    public void RoundTrip_ExtraDataAndAction_BothPreservedAndSeparated()
    {
        var data = new Dictionary<string, object> { ["categoryId"] = 5 };
        var action = new SuggestedAction { Type = SuggestedAction.TypeNavigate, Label = "Go", Route = "/budgets" };

        var json = AIInsightGenerationService.SerializeData(data, action);
        var (restoredData, restoredAction) = AIInsightGenerationService.DeserializeData(json);

        restoredData.Should().NotBeNull();
        restoredData!.Should().ContainKey("categoryId");
        // The reserved action key must not leak into the user-facing Data bag.
        restoredData.Should().NotContainKey("__suggestedAction");
        restoredAction.Should().NotBeNull();
        restoredAction!.Route.Should().Be("/budgets");
    }

    [Fact]
    public void DeserializeData_Garbage_ReturnsNullsWithoutThrowing()
    {
        var (data, action) = AIInsightGenerationService.DeserializeData("not json");
        data.Should().BeNull();
        action.Should().BeNull();
    }
}
