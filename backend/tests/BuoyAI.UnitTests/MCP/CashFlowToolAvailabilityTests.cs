using System.Linq;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.MCP.Tools;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.MCP;

/// <summary>
/// get_cash_flow has to be REACHABLE, not just correct.
///
/// The AI reported budget allocations as actual income and spending because it had no tool for
/// actual totals. A tool the router filters away on a budget-intent turn — which is precisely the
/// turn that produced the wrong answer ("how am I doing with my budget this month?") — would leave
/// the model inferring totals from allocations all over again.
///
/// ChatToolRouter.AlwaysDomains is { "user", "financial" }, so the "financial" tag is what keeps
/// this tool on every filtered turn.
/// </summary>
public class CashFlowToolAvailabilityTests
{
    [Fact]
    public void CashFlowReadTools_IsRegisteredWithTheExecutor()
    {
        // Registration is three separate edits (DI, the scan list, the domain map). Missing the
        // scan list means the model never sees the tool at all.
        McpToolExecutor.KnownToolTypes.Should().Contain(typeof(CashFlowReadTools));
    }

    [Fact]
    public void CashFlowReadTools_IsInAnAlwaysAvailableDomain()
    {
        var domain = McpToolExecutor.DomainForToolClass(nameof(CashFlowReadTools));

        domain.Should().Be("financial");
        ChatToolRouter.AlwaysDomains.Should().Contain(domain,
            "actual income/spend must stay reachable even when the router filters to a "
            + "single domain — a budget question is exactly when the model needs actuals");
    }

    [Fact]
    public void BudgetTools_AreNotInAnAlwaysAvailableDomain()
    {
        // Guards the premise of the test above: "budget" really is a filterable domain, so tagging
        // the cash-flow tool that way would have hidden it.
        var domain = McpToolExecutor.DomainForToolClass(nameof(BudgetReadTools));

        domain.Should().Be("budget");
        ChatToolRouter.AlwaysDomains.Should().NotContain(domain);
    }
}
