using System.Collections.Generic;
using System.Linq;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class ChatToolRouterTests
{
    private static ChatToolRouter Build(bool enabled, out Mock<IMcpToolExecutor> tools)
    {
        tools = new Mock<IMcpToolExecutor>();
        // Return one fake tool per requested domain so we can assert which domains were selected.
        tools.Setup(t => t.GetToolNamesForDomains(It.IsAny<IEnumerable<string>>()))
            .Returns((IEnumerable<string> ds) =>
                ds.Select(d => $"{d}_tool").ToHashSet(System.StringComparer.OrdinalIgnoreCase));
        tools.Setup(t => t.GetAvailableTools()).Returns(new List<McpToolDefinition>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI:ToolFiltering:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        return new ChatToolRouter(tools.Object, config, NullLogger<ChatToolRouter>.Instance);
    }

    [Fact]
    public void Disabled_ReturnsNull_FullToolset()
    {
        var router = Build(enabled: false, out _);
        router.SelectToolNames("how's my budget looking?").Should().BeNull();
    }

    [Fact]
    public void Enabled_BudgetMessage_SelectsBudgetPlusAlwaysDomains()
    {
        var router = Build(enabled: true, out _);
        var names = router.SelectToolNames("am I over budget on groceries this month?");
        names.Should().NotBeNull();
        // budget (matched) + user + financial (always-included)
        names!.Should().Contain("budget_tool");
        names.Should().Contain("user_tool");
        names.Should().Contain("financial_tool");
        names.Should().NotContain("subscription_tool");
    }

    [Fact]
    public void Enabled_NoDomainMatch_FailsOpenToNull()
    {
        var router = Build(enabled: true, out _);
        router.SelectToolNames("tell me a joke about penguins").Should().BeNull();
    }

    [Fact]
    public void Enabled_EmptyMessage_ReturnsNull()
    {
        var router = Build(enabled: true, out _);
        router.SelectToolNames("   ").Should().BeNull();
    }

    [Fact]
    public void Enabled_SavingsMessage_SelectsSavings()
    {
        var router = Build(enabled: true, out _);
        var names = router.SelectToolNames("add $100 to my emergency fund savings goal");
        names.Should().NotBeNull();
        names!.Should().Contain("savings_tool");
    }
}
