using BuoyAI.API.Endpoints;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.MCP;

public class McpServerInstructionsTests
{
    [Fact]
    public void ServerInstructions_ExplainCountUpBudgeting()
    {
        McpConnectEndpoints.ServerInstructions.Should().Contain("count-up",
            because: "the primer's core job is teaching the agent BuoyAI's signature model");
    }

    [Fact]
    public void ServerInstructions_StateReadOnlyAndCoreConcepts()
    {
        var s = McpConnectEndpoints.ServerInstructions.ToLowerInvariant();
        s.Should().Contain("read-only for money"); // the honest contract: money can't move
        s.Should().Contain("remember");            // ...but memory CAN be written
        s.Should().Contain("recall_memory");       // and read back for cross-agent memory
        s.Should().Contain("savings goal");
        s.Should().Contain("rollover");
        s.Should().Contain("cash_flow"); // steers the agent to the accurate money-in/out tool
        McpConnectEndpoints.ServerInstructions.Length.Should().BeGreaterThan(500,
            because: "a one-liner wouldn't convey the model");
    }
}
